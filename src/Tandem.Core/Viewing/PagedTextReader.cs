using System.Buffers;
using System.Diagnostics;

namespace Tandem.Core.Viewing;

/// <summary>
/// One displayed line: a whole text line, or a piece of a line longer than
/// <see cref="PagedTextReader.MaxLineBytes"/>. <paramref name="ByteLength"/> includes the line break.
/// <paramref name="Text"/> has no line break and no '\r' before it. <paramref name="EndsLine"/> is false for a
/// piece that the line continues after, and for a last line without a line break.
/// </summary>
public sealed record TextLine(long Offset, int ByteLength, string Text, bool EndsLine);

/// <summary>
/// Lines read from <paramref name="StartOffset"/>. <paramref name="EndOffset"/> is where the next page starts:
/// the end of the last line, or <paramref name="StartOffset"/> when the page is empty.
/// </summary>
public sealed record TextPage(long StartOffset, long EndOffset, IReadOnlyList<TextLine> Lines);

/// <summary>
/// Reads text a page at a time from a seekable stream of any size. A position is a byte offset; there is no
/// line index, and a call reads only the bytes around the requested position, never the whole file.
/// </summary>
/// <remarks>
/// <para>A line break is '\n' in units of the encoding: one byte in UTF-8 and single-byte code pages, two bytes
/// in UTF-16, counted from <c>bomLength</c>. A line longer than <see cref="MaxLineBytes"/> is shown as pieces
/// of at most that many bytes, each ending on a whole character (never inside a UTF-8 sequence or between
/// UTF-16 surrogates) and never between '\r' and its '\n'.</para>
/// <para>The start of the line holding an offset is looked for at most <see cref="BackScanLimit"/> bytes back.
/// When there is no line break that close and the file start is further away too, the offset is deep inside a
/// very long line whose real start is unknown. Its pieces are then counted from the offset itself (moved back
/// to a whole character), and stepping back moves <see cref="MaxLineBytes"/> at a time. That keeps
/// paging consistent: the <see cref="TextPage.EndOffset"/> of one page is always the start of the next.</para>
/// <para>Bytes read per call: <see cref="ReadPageAsync"/> at most
/// <c>BackScanLimit + lineCount × MaxLineBytes + 8</c>, <see cref="LineStartAsync"/> at most
/// <c>BackScanLimit + 8</c>, <see cref="StepBackAsync"/> at most <c>BackScanLimit + (lineCount + 1) × MaxLineBytes
/// + 8</c>. The 8 bytes of lookahead decide whether a piece cut falls inside a character or a "\r\n".</para>
/// <para>Thread-safety: one reader may be called from several threads at once. Calls take turns only for the
/// seek-and-read of the stream; the rest of the work uses buffers private to the call. Nothing else may use
/// the stream while the reader is in use. The reader does not close the stream.</para>
/// <para><see cref="Length"/> is read from the stream on every call, so a growing file shows its new end.</para>
/// </remarks>
public sealed class PagedTextReader
{
    public const int MaxLineBytes = 4096;
    public const int BackScanLimit = 64 * 1024;

    /// <summary>Extra bytes read past the last piece so a cut can see the characters it would split.</summary>
    private const int Lookahead = 8;

    private readonly Stream _stream;
    private readonly TextEncodingInfo _encoding;
    private readonly Kind _kind;
    private readonly int _unit;
    private readonly long _bomLength;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <param name="seekable">A readable stream with <see cref="Stream.CanSeek"/>; it is not closed by the reader.</param>
    /// <param name="encoding">UTF-8, UTF-16LE, UTF-16BE or a single-byte code page from <see cref="TextEncodings"/>.</param>
    /// <param name="bomLength">Bytes of byte order mark at the start; text starts after them.</param>
    public PagedTextReader(Stream seekable, TextEncodingInfo encoding, int bomLength)
    {
        ArgumentNullException.ThrowIfNull(seekable);
        ArgumentNullException.ThrowIfNull(encoding);
        ArgumentOutOfRangeException.ThrowIfNegative(bomLength);
        if (!seekable.CanSeek)
            throw new ArgumentException("The stream must support seeking; copy a non-seekable source to a temporary file first.", nameof(seekable));
        if (!seekable.CanRead)
            throw new ArgumentException("The stream must be readable.", nameof(seekable));

        _stream = seekable;
        _encoding = encoding;
        _bomLength = bomLength;
        _kind = encoding.Encoding.CodePage switch
        {
            65001 => Kind.Utf8,
            1200 => Kind.Utf16LE,
            1201 => Kind.Utf16BE,
            _ when encoding.Encoding.IsSingleByte => Kind.SingleByte,
            _ => throw new ArgumentException($"Encoding '{encoding.Id}' is not supported for paged reading.", nameof(encoding)),
        };
        _unit = _kind is Kind.Utf16LE or Kind.Utf16BE ? 2 : 1;
    }

    private enum Kind { Utf8, Utf16LE, Utf16BE, SingleByte }

    public long Length => _stream.Length;

    /// <summary>
    /// The start of the displayed line holding <paramref name="offset"/>; <see cref="Length"/> when the offset is
    /// at or past the end.
    /// </summary>
    public async Task<long> LineStartAsync(long offset, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        var (len, bom) = Bounds();
        if (offset >= len) return len;
        offset = Normalize(bom, offset);

        using var w = await ReadWindowAsync(offset - BackScanLimit, offset + Lookahead, len, bom, ct).ConfigureAwait(false);
        return LineStart(w, offset);
    }

    /// <summary>Up to <paramref name="lineCount"/> displayed lines from <c>LineStartAsync(offset)</c>.</summary>
    public async Task<TextPage> ReadPageAsync(long offset, int lineCount, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(lineCount);
        var (len, bom) = Bounds();
        if (offset >= len) return new TextPage(len, len, []);
        offset = Normalize(bom, offset);

        using var w = await ReadWindowAsync(offset - BackScanLimit, offset + (long)lineCount * MaxLineBytes + Lookahead, len, bom, ct)
            .ConfigureAwait(false);
        var start = LineStart(w, offset);
        var lines = new List<TextLine>(lineCount);
        var p = start;
        while (lines.Count < lineCount && p < w.Length)
        {
            var piece = NextPiece(w, p);
            Debug.Assert(piece.End != Unknown, "the window covers every piece of the page");
            var text = _encoding.Encoding.GetString(w.Span(p, piece.ContentEnd));
            lines.Add(new TextLine(p, (int)(piece.End - p), text, piece.EndsLine));
            p = piece.End;
        }
        return new TextPage(start, p, lines);
    }

    /// <summary>
    /// The start of the displayed line <paramref name="lineCount"/> lines above the one holding
    /// <paramref name="offset"/>; never less than <c>bomLength</c>.
    /// </summary>
    public async Task<long> StepBackAsync(long offset, int lineCount, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(lineCount);
        var (len, bom) = Bounds();
        offset = Math.Clamp(offset, bom, len);

        var from = offset - BackScanLimit - ((long)lineCount + 1) * MaxLineBytes;
        using var w = await ReadWindowAsync(from, offset + Lookahead, len, bom, ct).ConfigureAwait(false);
        var cur = LineStart(w, offset);
        for (var i = 0; i < lineCount && cur > bom; i++)
        {
            var target = cur - 1;
            var real = RealLineStart(w, target);
            cur = real is { } start
                ? PieceHolding(w, start, Normalize(w.Bom, target))
                : BoundaryAtOrAfter(w, Math.Max(bom, cur - MaxLineBytes));
        }
        return cur;
    }

    private (long Length, long Bom) Bounds()
    {
        var len = _stream.Length;
        return (len, Math.Min(_bomLength, len));
    }

    // ---- Lines and pieces over a window of bytes ----

    private const long Unknown = long.MaxValue;

    private readonly record struct Piece(long End, long ContentEnd, bool EndsLine);

    private long LineStart(Window w, long offset)
    {
        if (offset >= w.Length) return w.Length;
        var real = RealLineStart(w, offset);
        var at = Normalize(w.Bom, offset);
        return real is { } start ? PieceHolding(w, start, at) : BoundaryAtOrBefore(w, at);
    }

    /// <summary>The offset moved back onto a unit boundary (UTF-16) and not before the text start.</summary>
    private long Normalize(long bom, long offset)
    {
        offset = Math.Max(offset, bom);
        return _unit == 2 ? bom + ((offset - bom) & ~1L) : offset;
    }

    /// <summary>
    /// The start of the text line holding <paramref name="offset"/> when it is within
    /// <see cref="BackScanLimit"/> bytes (or the text start is), else null.
    /// </summary>
    private long? RealLineStart(Window w, long offset)
    {
        offset = Normalize(w.Bom, offset);
        var lo = Math.Max(w.Bom, offset - BackScanLimit);
        Debug.Assert(lo >= w.Start, "the window covers the back scan");
        if (_unit == 1)
        {
            var index = w.Span(lo, offset).LastIndexOf((byte)'\n');
            if (index >= 0) return lo + index + 1;
        }
        else
        {
            for (var n = offset - 2; n >= lo; n -= 2)
                if (IsUnit(w, n, '\n')) return n + 2;
        }
        return lo == w.Bom ? w.Bom : null;
    }

    /// <summary>Walks pieces from a line start to the one holding <paramref name="offset"/>.</summary>
    private long PieceHolding(Window w, long lineStart, long offset)
    {
        var p = lineStart;
        while (true)
        {
            var end = NextPiece(w, p).End;
            if (end > offset) return p;
            p = end;
        }
    }

    /// <summary>
    /// The piece starting at <paramref name="p"/>: to just past the next line break within
    /// <see cref="MaxLineBytes"/>, to the end of the file, or cut on a character boundary. <c>End</c> is
    /// <see cref="Unknown"/> when the window ends before the piece does.
    /// </summary>
    private Piece NextPiece(Window w, long p)
    {
        var limit = Math.Min(p + MaxLineBytes, w.Length);
        var scanEnd = Math.Min(limit, w.End);
        long newline = -1;
        if (_unit == 1)
        {
            var index = w.Span(p, scanEnd).IndexOf((byte)'\n');
            if (index >= 0) newline = p + index;
        }
        else
        {
            for (var n = p; n + 2 <= scanEnd; n += 2)
                if (IsUnit(w, n, '\n')) { newline = n; break; }
        }

        if (newline >= 0)
        {
            var contentEnd = newline - _unit >= p && IsUnit(w, newline - _unit, '\r') ? newline - _unit : newline;
            return new Piece(newline + _unit, contentEnd, true);
        }
        if (scanEnd < limit) return new Piece(Unknown, Unknown, false);
        if (limit == w.Length) return new Piece(limit, limit, false);

        var cut = limit;
        switch (_kind)
        {
            case Kind.Utf8:
                for (var i = cut - 1; i >= Math.Max(p + 1, cut - 3); i--)
                {
                    if (IsContinuation(w[i])) continue;
                    if (i + Utf8SequenceLength(w[i]) > cut) cut = i;
                    break;
                }
                break;
            case Kind.Utf16LE or Kind.Utf16BE:
                if (cut - 2 > p && IsHighSurrogate(w, cut - 2)) cut -= 2;
                break;
        }
        if (cut == limit && cut - _unit > p && IsUnit(w, cut - _unit, '\r')
            && cut + _unit <= w.End && IsUnit(w, cut, '\n'))
            cut -= _unit;
        return new Piece(cut, cut, false);
    }

    // ---- Character boundaries ----

    /// <summary>The nearest whole-character position at or before <paramref name="p"/> (a unit boundary).</summary>
    private long BoundaryAtOrBefore(Window w, long p)
    {
        if (p <= w.Bom || p >= w.Length || p >= w.End) return p;
        switch (_kind)
        {
            case Kind.Utf8:
                if (!IsContinuation(w[p])) return p;
                for (var i = p - 1; i >= Math.Max(w.Bom, Math.Max(w.Start, p - 3)); i--)
                {
                    if (IsContinuation(w[i])) continue;
                    return i + Utf8SequenceLength(w[i]) > p ? i : p;
                }
                return p;
            case Kind.Utf16LE or Kind.Utf16BE:
                return p + 2 <= w.End && p - 2 >= Math.Max(w.Bom, w.Start)
                       && IsLowSurrogate(w, p) && IsHighSurrogate(w, p - 2)
                    ? p - 2
                    : p;
            default:
                return p;
        }
    }

    /// <summary>The nearest whole-character position at or after <paramref name="p"/>.</summary>
    private long BoundaryAtOrAfter(Window w, long p)
    {
        if (_unit == 2) p = w.Bom + ((p - w.Bom + 1) & ~1L);
        var before = BoundaryAtOrBefore(w, p);
        if (before == p) return p;
        return Math.Min(w.Length, _kind == Kind.Utf8 ? before + Utf8SequenceLength(w[before]) : p + 2);
    }

    private static bool IsContinuation(byte b) => (b & 0xC0) == 0x80;

    private static int Utf8SequenceLength(byte lead) => lead switch
    {
        < 0xC0 => 1,
        < 0xE0 => 2,
        < 0xF0 => 3,
        < 0xF8 => 4,
        _ => 1,
    };

    /// <summary>Whether the UTF-16 or single-byte unit at <paramref name="n"/> is the ASCII character <paramref name="c"/>.</summary>
    private bool IsUnit(Window w, long n, char c) => _kind switch
    {
        Kind.Utf16LE => w[n] == c && w[n + 1] == 0,
        Kind.Utf16BE => w[n] == 0 && w[n + 1] == c,
        _ => w[n] == c,
    };

    private byte HighByte(Window w, long n) => _kind == Kind.Utf16LE ? w[n + 1] : w[n];

    private bool IsHighSurrogate(Window w, long n) => HighByte(w, n) is >= 0xD8 and <= 0xDB;

    private bool IsLowSurrogate(Window w, long n) => HighByte(w, n) is >= 0xDC and <= 0xDF;

    // ---- Reading ----

    /// <summary>
    /// Reads bytes [<paramref name="from"/>, <paramref name="to"/>) clamped to the text. If the stream turns out
    /// shorter than its length said, the window's <c>Length</c> is cut to what was read.
    /// </summary>
    private async Task<Window> ReadWindowAsync(long from, long to, long length, long bom, CancellationToken ct)
    {
        from = Math.Max(from, bom);
        to = Math.Min(to, length);
        var count = Math.Max(0, to - from);
        if (count > Array.MaxLength)
            throw new ArgumentOutOfRangeException(nameof(to), "The requested page is too large.");

        var buffer = ArrayPool<byte>.Shared.Rent((int)count);
        var read = 0;
        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                _stream.Seek(from, SeekOrigin.Begin);
                while (read < count)
                {
                    var n = await _stream.ReadAsync(buffer.AsMemory(read, (int)count - read), ct).ConfigureAwait(false);
                    if (n == 0) break;
                    read += n;
                }
            }
            finally
            {
                _gate.Release();
            }
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buffer);
            throw;
        }
        if (read < count) length = from + read;
        return new Window(buffer, from, read, length, bom);
    }

    /// <summary>Bytes [<c>Start</c>, <c>End</c>) of a text whose bytes are [<c>Bom</c>, <c>Length</c>).</summary>
    private sealed class Window(byte[] buffer, long start, int count, long length, long bom) : IDisposable
    {
        public long Start { get; } = start;
        public long End { get; } = start + count;
        public long Length { get; } = length;
        public long Bom { get; } = bom;

        public byte this[long position] => buffer[checked((int)(position - Start))];

        public ReadOnlySpan<byte> Span(long from, long to) =>
            buffer.AsSpan(checked((int)(from - Start)), checked((int)(to - from)));

        public void Dispose() => ArrayPool<byte>.Shared.Return(buffer);
    }
}
