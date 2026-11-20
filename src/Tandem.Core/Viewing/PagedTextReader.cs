using System.Buffers;

namespace Tandem.Core.Viewing;

/// <summary>
/// One displayed row: a whole text line, or a piece of a long line (see <see cref="PagedTextReader"/>).
/// <paramref name="ByteLength"/> includes the line break. <paramref name="Text"/> has no line break and no '\r'
/// before it. <paramref name="EndsLine"/> is false for a piece that the line continues after, and for a last
/// line without a line break.
/// </summary>
public sealed record TextLine(long Offset, int ByteLength, string Text, bool EndsLine);

/// <summary>
/// Rows read from <paramref name="StartOffset"/>. <paramref name="EndOffset"/> is where the next page starts:
/// the end of the last row, or <paramref name="StartOffset"/> when the page is empty.
/// </summary>
public sealed record TextPage(long StartOffset, long EndOffset, IReadOnlyList<TextLine> Lines);

/// <summary>
/// Reads text a page at a time from a seekable stream of any size. A position is a byte offset; there is no
/// line index, and a call reads only the bytes around the requested position, never the whole file.
/// </summary>
/// <remarks>
/// <para><b>Rows.</b> Where rows begin is a function of the file's bytes alone, never of where reading
/// started. A line break is '\n' in units of the encoding: one byte in UTF-8 and single-byte code pages, two
/// bytes in UTF-16, aligned from <c>bomLength</c>. "\r\n" counts as one break and a lone '\r' is text. A row
/// begins:</para>
/// <list type="bullet">
/// <item>at the start of every line: the text start (<c>bomLength</c>) and just after every line break;</item>
/// <item>inside a long line, at its grid points moved back to a whole character, never inside a UTF-8 sequence or
/// between UTF-16 surrogates. The first 14 grid points are counted from the line start, every
/// <see cref="GridStep"/> bytes. Further on, they are the multiples of <see cref="GridStep"/> counted from the text
/// start. That is how a position deep in a huge line is placed without finding the line start;</item>
/// <item>never at a grid point that falls on the line's own line break, so no empty row appears.</item>
/// </list>
/// <para>So <see cref="LineStartAsync"/>, <see cref="ReadPageAsync"/> and <see cref="StepBackAsync"/> agree by
/// construction:</para>
/// <list type="bullet">
/// <item>paging forward and paging back visit the same row starts;</item>
/// <item>the <see cref="TextPage.EndOffset"/> of a page is the start of the next page;</item>
/// <item>PageUp then PageDown returns to the same row.</item>
/// </list>
/// <para>A row, including its line break, is at most <see cref="MaxLineBytes"/> bytes. Where the grid switches
/// from the line start to the text start, about 57 KB into a line, one row can be short.</para>
/// <para><b>Bytes read per call.</b></para>
/// <list type="bullet">
/// <item><see cref="ReadPageAsync"/>: at most <c>BackScanLimit + lineCount × MaxLineBytes + 8</c>;</item>
/// <item><see cref="LineStartAsync"/>: at most <c>BackScanLimit + 8</c>;</item>
/// <item><see cref="StepBackAsync"/>: at most <c>BackScanLimit + (lineCount + 1) × MaxLineBytes + 8</c>, because each
/// row stepped over can need its own look back for the line start.</item>
/// </list>
/// <para>The 8 bytes of lookahead let a grid point just past the offset see the character or line break it
/// falls in.</para>
/// <para><b>Thread-safety.</b> One reader may be called from several threads at once. Only reading
/// <see cref="Length"/> and the seek-and-read of the stream are serialized; the rest uses buffers private to the
/// call. Reading <see cref="Length"/> blocks while another call is reading the stream. Nothing else may use the
/// stream while the reader is in use. The reader does not close it.</para>
/// <para><see cref="Length"/> is read from the stream on every call, so a growing file shows its new end.</para>
/// </remarks>
public sealed class PagedTextReader
{
    public const int MaxLineBytes = 4096;

    /// <summary>How far back the reader looks for the start of a line.</summary>
    public const int BackScanLimit = 64 * 1024;

    /// <summary>
    /// Distance between grid points, the row starts inside long lines. A grid point moves back up to 3 bytes to a
    /// whole character, and the last row of a line also holds its line break ("\r\n" is 4 bytes in UTF-16). The 8
    /// bytes of slack keep every row within <see cref="MaxLineBytes"/>. It is even, so grid points stay on UTF-16
    /// units.
    /// </summary>
    public const int GridStep = MaxLineBytes - 8;

    /// <summary>Extra bytes read past the offset so a grid point there can see the character it would split.</summary>
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

    public long Length
    {
        get
        {
            _gate.Wait();
            try { return _stream.Length; }
            finally { _gate.Release(); }
        }
    }

    /// <summary>
    /// The start of the row holding <paramref name="offset"/>, i.e. the greatest row start at or before it;
    /// <see cref="Length"/> when the offset is at or past the end.
    /// </summary>
    public async Task<long> LineStartAsync(long offset, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        using var w = await ReadWindowAsync(offset - BackScanLimit, offset + Lookahead, ct).ConfigureAwait(false);
        return RowStart(w, offset);
    }

    /// <summary>Up to <paramref name="lineCount"/> rows from <c>LineStartAsync(offset)</c>.</summary>
    public async Task<TextPage> ReadPageAsync(long offset, int lineCount, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(lineCount);
        using var w = await ReadWindowAsync(offset - BackScanLimit, offset + (long)lineCount * MaxLineBytes + Lookahead, ct)
            .ConfigureAwait(false);
        var start = RowStart(w, offset, out var line);
        var lines = new List<TextLine>(Math.Min(lineCount, 1024));
        var p = start;
        while (lines.Count < lineCount && p < w.Length)
        {
            var row = RowAt(w, p, line);
            var text = _encoding.Encoding.GetString(w.Span(p, row.ContentEnd));
            lines.Add(new TextLine(p, (int)(row.End - p), text, row.EndsLine));
            p = row.End;
            if (row.EndsLine) line = p;
        }
        return new TextPage(start, p, lines);
    }

    /// <summary>
    /// The start of the row <paramref name="lineCount"/> rows above the one holding <paramref name="offset"/>;
    /// never less than <c>bomLength</c>.
    /// </summary>
    public async Task<long> StepBackAsync(long offset, int lineCount, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(lineCount);
        using var w = await ReadWindowAsync(offset - BackScanLimit - ((long)lineCount + 1) * MaxLineBytes, offset + Lookahead, ct)
            .ConfigureAwait(false);
        var cur = RowStart(w, offset);
        for (var i = 0; i < lineCount && cur > w.Bom; i++)
            cur = RowStart(w, cur - 1);
        return cur;
    }

    // ---- Row starts over a window of bytes ----

    /// <summary>Grid points counted from a line's start, before the absolute grid takes over.</summary>
    /// <remarks>
    /// With the line start unknown (more than <see cref="BackScanLimit"/> back), the grid points looked at lie at most
    /// 2 × GridStep before the offset. So they are more than <c>BackScanLimit − 2 × GridStep</c> past the line start,
    /// beyond <see cref="RelativeSpan"/>, and the text-start grid is the right one for them.
    /// </remarks>
    private const int RelativePoints = 14;

    /// <summary>How far the relative grid reaches: <c>RelativePoints × GridStep</c>, which stays inside <see cref="BackScanLimit"/>.</summary>
    private const long RelativeSpan = (long)RelativePoints * GridStep;

    private readonly record struct Row(long End, long ContentEnd, bool EndsLine);

    /// <summary>
    /// The start of the line holding <paramref name="offset"/>: just after the last line break that ends at or before
    /// it, or the text start. Null when neither is within <see cref="BackScanLimit"/> bytes.
    /// </summary>
    private long? FindLineStart(Window w, long offset)
    {
        var lo = Math.Max(w.Bom, offset - BackScanLimit);
        if (_unit == 2) lo = w.Bom + ((lo - w.Bom + 1) & ~1L);
        var end = LastLineBreakEnd(w, lo, offset);
        if (end >= 0) return end;
        return lo == w.Bom ? w.Bom : null;
    }

    /// <summary>
    /// Whether <paramref name="x"/> is a grid point of the line starting at <paramref name="line"/> (null: further
    /// back than <see cref="BackScanLimit"/>). The first <see cref="RelativePoints"/> grid points are counted from
    /// the line start; after <see cref="RelativeSpan"/> the grid is counted from the text start.
    /// </summary>
    private static bool IsGridPoint(long bom, long? line, long x)
    {
        if (line is not { } start) return (x - bom) % GridStep == 0;
        if (x <= start) return false;
        var fromLine = x - start;
        if (fromLine <= RelativeSpan) return fromLine % GridStep == 0;
        return (x - bom) % GridStep == 0;
    }

    /// <summary>
    /// The position where the line's text ends when <paramref name="offset"/> is inside its line break ('\r' of a
    /// "\r\n", or the '\n'); else <see cref="long.MaxValue"/>. A grid point there would make an empty row, so it is
    /// not a row start.
    /// </summary>
    private long ContentEndIfInLineBreak(Window w, long offset)
    {
        var a = _unit == 2 ? w.Bom + ((offset - w.Bom) & ~1L) : offset;
        if (a + _unit > w.Length) return long.MaxValue;
        if (IsUnit(w, a, '\n'))
            return a - _unit >= w.Bom && IsUnit(w, a - _unit, '\r') ? a - _unit : a;
        if (IsUnit(w, a, '\r') && a + 2 * _unit <= w.Length && IsUnit(w, a + _unit, '\n'))
            return a;
        return long.MaxValue;
    }

    /// <summary>The greatest row start at or before <paramref name="offset"/>, or the text length at or past the end.</summary>
    private long RowStart(Window w, long offset) => RowStart(w, offset, out _);

    private long RowStart(Window w, long offset, out long? line)
    {
        line = null;
        if (offset >= w.Length) return w.Length;
        if (offset <= w.Bom)
        {
            line = w.Bom;
            return w.Bom;
        }

        var lineStart = FindLineStart(w, offset);
        line = lineStart;
        var contentEnd = ContentEndIfInLineBreak(w, offset);
        var best = lineStart ?? long.MinValue;

        // Grid points that can land at or before the offset: one step either side of the nearest ones, both counted
        // from the line start and from the text start. A grid point moves back at most 3 bytes.
        Span<long> candidates = stackalloc long[6];
        var count = 0;
        if (lineStart is { } ls)
        {
            var j = (offset - ls) / GridStep;
            for (var d = -1; d <= 1; d++) candidates[count++] = ls + (j + d) * GridStep;
        }
        var m = (offset - w.Bom) / GridStep;
        for (var d = -1; d <= 1; d++) candidates[count++] = w.Bom + (m + d) * GridStep;

        foreach (var x in candidates[..count])
        {
            if (x <= w.Bom || x >= w.Length || x - 3 > offset || !IsGridPoint(w.Bom, lineStart, x)) continue;
            var p = CharStart(w, x);
            if (p <= offset && p < contentEnd && p > best) best = p;
        }
        if (best == long.MinValue)
            throw new InvalidOperationException($"PagedTextReader found no row start at or before {offset}.");
        return best;
    }

    /// <summary>The row starting at <paramref name="start"/>, a row start in the line starting at <paramref name="line"/>.</summary>
    private Row RowAt(Window w, long start, long? line)
    {
        // The first grid point after the row start, from the line start's grid and from the text start's grid.
        var next = w.Length;
        Span<long> candidates = stackalloc long[4];
        var count = 0;
        if (line is { } ls)
        {
            var j = (start - ls) / GridStep;
            candidates[count++] = ls + (j + 1) * GridStep;
            candidates[count++] = ls + (j + 2) * GridStep;
        }
        var m = (start - w.Bom) / GridStep;
        candidates[count++] = w.Bom + (m + 1) * GridStep;
        candidates[count++] = w.Bom + (m + 2) * GridStep;
        candidates[..count].Sort();
        foreach (var x in candidates[..count])
        {
            if (x - 3 >= next) break;
            if (x <= start || x >= w.Length || !IsGridPoint(w.Bom, line, x)) continue;
            var p = CharStart(w, x);
            if (p > start && p < next) next = p;
        }

        // A line break whose text ends at or before that grid point ends the row (the grid point is then no row start).
        var newline = FirstLineBreak(w, start, Math.Min(w.Length, next + 2 * _unit));
        if (newline >= 0)
        {
            var contentEnd = newline - _unit >= start && IsUnit(w, newline - _unit, '\r') ? newline - _unit : newline;
            if (contentEnd <= next) return new Row(newline + _unit, contentEnd, true);
        }
        return new Row(next, next, false);
    }

    /// <summary>
    /// <paramref name="x"/> moved back to the start of the character it is in: never inside a UTF-8 sequence or
    /// between UTF-16 surrogates. At most 3 bytes back.
    /// </summary>
    private long CharStart(Window w, long x)
    {
        switch (_kind)
        {
            case Kind.Utf8:
                if (!IsContinuation(w[x])) return x;
                for (var i = x - 1; i >= Math.Max(w.Bom, x - 3); i--)
                {
                    if (IsContinuation(w[i])) continue;
                    return i + Utf8SequenceLength(w[i]) > x ? i : x;
                }
                return x; // stray continuation bytes: no character to keep whole
            case Kind.Utf16LE or Kind.Utf16BE:
                return x + 2 <= w.Length && x - 2 >= w.Bom && IsLowSurrogate(w, x) && IsHighSurrogate(w, x - 2) ? x - 2 : x;
            default:
                return x;
        }
    }

    /// <summary>The end of the last line break that lies wholly in [<paramref name="from"/>, <paramref name="to"/>), or -1.</summary>
    private long LastLineBreakEnd(Window w, long from, long to)
    {
        if (_unit == 1)
        {
            var index = w.Span(from, to).LastIndexOf((byte)'\n');
            return index >= 0 ? from + index + 1 : -1;
        }
        // Units are aligned from Bom, and so is every row start; a match at an odd distance is not a unit.
        var span = w.Span(from, from + ((to - from) & ~1L));
        var pattern = NewlineUnit;
        while (true)
        {
            var index = span.LastIndexOf(pattern);
            if (index < 0) return -1;
            if ((index & 1) == 0) return from + index + 2;
            span = span[..(index + 1)];
        }
    }

    /// <summary>The position of the first line break that lies wholly in [<paramref name="from"/>, <paramref name="to"/>), or -1.</summary>
    private long FirstLineBreak(Window w, long from, long to)
    {
        if (_unit == 1)
        {
            var index = w.Span(from, to).IndexOf((byte)'\n');
            return index >= 0 ? from + index : -1;
        }
        var span = w.Span(from, to);
        var pattern = NewlineUnit;
        var skipped = 0;
        while (true)
        {
            var index = span.IndexOf(pattern);
            if (index < 0) return -1;
            if (((skipped + index) & 1) == 0) return from + skipped + index;
            skipped += index + 1;
            span = span[(index + 1)..];
        }
    }

    // ---- Units and characters ----

    private ReadOnlySpan<byte> NewlineUnit => _kind == Kind.Utf16LE ? "\n\0"u8 : "\0\n"u8;

    private static bool IsContinuation(byte b) => (b & 0xC0) == 0x80;

    private static int Utf8SequenceLength(byte lead) => lead switch
    {
        < 0xC0 => 1,
        < 0xE0 => 2,
        < 0xF0 => 3,
        < 0xF8 => 4,
        _ => 1,
    };

    /// <summary>Whether the unit at <paramref name="n"/> is the ASCII character <paramref name="c"/>.</summary>
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
    /// Reads bytes [<paramref name="from"/>, <paramref name="to"/>) clamped to the text. The stream length is
    /// read under the same lock as the bytes. If the stream turns out shorter than its length said, the
    /// window's <c>Length</c> is cut to what was read.
    /// </summary>
    private async Task<Window> ReadWindowAsync(long from, long to, CancellationToken ct)
    {
        byte[]? buffer = null;
        long length, bom;
        var read = 0;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            length = _stream.Length;
            bom = Math.Min(_bomLength, length);
            from = Math.Clamp(from, bom, length);
            to = Math.Clamp(to, from, length);
            var count = to - from;
            if (count > Array.MaxLength)
                throw new ArgumentOutOfRangeException(nameof(to), "The requested page is too large.");

            buffer = ArrayPool<byte>.Shared.Rent((int)count);
            _stream.Seek(from, SeekOrigin.Begin);
            while (read < count)
            {
                var n = await _stream.ReadAsync(buffer.AsMemory(read, (int)count - read), ct).ConfigureAwait(false);
                if (n == 0) break;
                read += n;
            }
            if (read < count) length = from + read;
        }
        catch
        {
            if (buffer is not null) ArrayPool<byte>.Shared.Return(buffer);
            throw;
        }
        finally
        {
            _gate.Release();
        }
        return new Window(buffer, from, read, length, bom);
    }

    /// <summary>
    /// Bytes [<c>Start</c>, <c>End</c>) of a text whose bytes are [<c>Bom</c>, <c>Length</c>). Touching a byte
    /// outside the window is a bug in the window sizing and throws <see cref="InvalidOperationException"/>.
    /// </summary>
    private sealed class Window(byte[] buffer, long start, int count, long length, long bom) : IDisposable
    {
        public long Start { get; } = start;
        public long End { get; } = start + count;
        public long Length { get; } = length;
        public long Bom { get; } = bom;

        public byte this[long position]
        {
            get
            {
                Check(position, position + 1);
                return buffer[(int)(position - Start)];
            }
        }

        public ReadOnlySpan<byte> Span(long from, long to)
        {
            Check(from, to);
            return buffer.AsSpan((int)(from - Start), (int)(to - from));
        }

        private void Check(long from, long to)
        {
            if (from < Start || to > End || from > to)
                throw new InvalidOperationException(
                    $"PagedTextReader read window [{Start}, {End}) does not cover [{from}, {to}).");
        }

        public void Dispose() => ArrayPool<byte>.Shared.Return(buffer);
    }
}
