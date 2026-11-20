using System.Buffers;
using System.Text;
using System.Text.RegularExpressions;
using Tandem.Core.Viewing;

namespace Tandem.Core.Search;

/// <summary>
/// Tells whether a file's contents match a <see cref="ContentQuery"/>. The file is read in blocks of
/// <see cref="BlockSize"/> bytes and decoded by one <see cref="Decoder"/>, so a character cut by a block boundary is
/// read whole, and reading stops at the first match. Thread-safe: one matcher can check several files at once.
/// </summary>
/// <remarks>
/// The encoding is <see cref="ContentQuery.EncodingId"/> when set; otherwise <see cref="TextEncodingDetector"/>
/// guesses it from the first block (which is also the detector's sample: <see cref="BlockSize"/> equals
/// <see cref="TextEncodingDetector.SampleSize"/>). Content that looks binary, including a UTF-16 guess without a byte
/// order mark that <see cref="TextEncodingDetector.IsUnlikelyUtf16"/> rejects, is read as Windows-1251, so ASCII and
/// Cyrillic strings inside binary files are found. A byte order mark of the encoding in use is skipped.
/// <para>Plain text is looked for with <see cref="StringComparison.Ordinal"/> or
/// <see cref="StringComparison.OrdinalIgnoreCase"/> in each decoded block preceded by the last
/// <c>Text.Length − 1</c> characters before it. A regular expression is matched against each line (split at
/// <c>\n</c>, a <c>\r</c> before it dropped); a line longer than <see cref="MaxLineChars"/> is matched in pieces of
/// that length. A line on which the expression times out counts as not matching.</para>
/// </remarks>
public sealed class ContentMatcher
{
    /// <summary>How many bytes are read and decoded at a time.</summary>
    public const int BlockSize = 64 * 1024;

    /// <summary>The longest piece of a line a regular expression is matched against.</summary>
    public const int MaxLineChars = 1024 * 1024;

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    private readonly string _text;
    private readonly StringComparison _comparison;
    private readonly Regex? _regex;
    private readonly TextEncodingInfo? _encoding;

    /// <exception cref="SearchQueryException">The text is empty or the regular expression is invalid
    /// (<see cref="SearchField.Content"/>); the encoding is not one of <see cref="TextEncodings.All"/>
    /// (<see cref="SearchField.Encoding"/>).</exception>
    public ContentMatcher(ContentQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (string.IsNullOrEmpty(query.Text))
            throw new SearchQueryException(SearchField.Content, "The text to search for is empty.");
        _text = query.Text;
        _comparison = query.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (query.EncodingId is { } id)
            _encoding = TextEncodings.Find(id)
                        ?? throw new SearchQueryException(SearchField.Encoding, $"Unknown encoding '{id}'.");
        if (query.IsRegex)
        {
            var options = RegexOptions.CultureInvariant | (query.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
            try
            {
                _regex = new Regex(query.Text, options, RegexTimeout);
            }
            catch (ArgumentException e)
            {
                throw new SearchQueryException(SearchField.Content, e.Message);
            }
        }
    }

    /// <summary>Reads <paramref name="content"/> from its current position until the first match or its end. The
    /// stream is not disposed. Cancellation is checked before every block.</summary>
    /// <exception cref="IOException">The stream cannot be read.</exception>
    public async Task<bool> IsMatchAsync(Stream content, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        var bytes = ArrayPool<byte>.Shared.Rent(BlockSize);
        char[]? chars = null;
        Sink? sink = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            var read = await FillAsync(content, bytes, ct).ConfigureAwait(false);
            var (encoding, skip) = ChooseEncoding(bytes.AsSpan(0, read), sampleIsWholeFile: read < BlockSize);
            var decoder = encoding.Encoding.GetDecoder();
            chars = ArrayPool<char>.Shared.Rent(encoding.Encoding.GetMaxCharCount(BlockSize));
            sink = _regex is null ? new TextSink(_text, _comparison, chars.Length) : new LineSink(_regex);

            var start = skip;
            while (true)
            {
                var last = read == 0;
                var input = bytes.AsSpan(start, read - start);
                // One pass per block unless the characters do not fit; with flush on the last (empty) pass the
                // decoder gives out what it held back.
                do
                {
                    decoder.Convert(input, chars, last, out var used, out var produced, out _);
                    input = input[used..];
                    if (sink.Add(chars.AsSpan(0, produced))) return true;
                } while (!input.IsEmpty);
                if (last) return sink.End();

                ct.ThrowIfCancellationRequested();
                read = await content.ReadAsync(bytes.AsMemory(0, BlockSize), ct).ConfigureAwait(false);
                start = 0;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
            if (chars is not null) ArrayPool<char>.Shared.Return(chars);
            sink?.Dispose();
        }
    }

    /// <summary>Reads until <see cref="BlockSize"/> bytes are in <paramref name="buffer"/> or the stream ends.</summary>
    private static async Task<int> FillAsync(Stream content, byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < BlockSize)
        {
            var n = await content.ReadAsync(buffer.AsMemory(read, BlockSize - read), ct).ConfigureAwait(false);
            if (n == 0) break;
            read += n;
        }
        return read;
    }

    /// <summary>The encoding to decode with and how many leading bytes (a byte order mark) to skip.</summary>
    private (TextEncodingInfo Encoding, int Skip) ChooseEncoding(ReadOnlySpan<byte> sample, bool sampleIsWholeFile)
    {
        var guess = TextEncodingDetector.Detect(sample, sampleIsWholeFile);
        if (_encoding is not null)
            return (_encoding, guess.Encoding == _encoding ? guess.BomLength : 0);
        if (guess.LooksBinary || TextEncodingDetector.IsUnlikelyUtf16(guess, sample, sampleIsWholeFile))
            return (TextEncodings.Windows1251, 0);
        return (guess.Encoding, guess.BomLength);
    }

    /// <summary>Takes decoded text a piece at a time and says whether it matched.</summary>
    private abstract class Sink : IDisposable
    {
        public virtual void Dispose() { }

        /// <summary>True once the text so far matches.</summary>
        public abstract bool Add(ReadOnlySpan<char> chars);

        /// <summary>Whether the end of the text completes a match.</summary>
        public abstract bool End();
    }

    /// <summary>Plain text: each piece (at most <paramref name="maxPiece"/> characters) is searched together with the
    /// end of the one before.</summary>
    private sealed class TextSink(string text, StringComparison comparison, int maxPiece) : Sink
    {
        private readonly int _keep = text.Length - 1;
        private readonly char[] _window = ArrayPool<char>.Shared.Rent(text.Length - 1 + maxPiece);
        private int _kept;

        public override void Dispose() => ArrayPool<char>.Shared.Return(_window);

        public override bool Add(ReadOnlySpan<char> chars)
        {
            if (chars.IsEmpty) return false;
            chars.CopyTo(_window.AsSpan(_kept));
            var length = _kept + chars.Length;
            if (_window.AsSpan(0, length).IndexOf(text, comparison) >= 0) return true;
            _kept = Math.Min(_keep, length);
            _window.AsSpan(length - _kept, _kept).CopyTo(_window);
            return false;
        }

        public override bool End() => false;
    }

    /// <summary>A regular expression, line by line.</summary>
    private sealed class LineSink(Regex regex) : Sink
    {
        private char[] _line = new char[256];
        private int _length;

        public override bool Add(ReadOnlySpan<char> chars)
        {
            while (!chars.IsEmpty)
            {
                var room = MaxLineChars - _length;
                var take = chars[..Math.Min(chars.Length, room)];
                var lineEnd = take.IndexOf('\n');
                if (lineEnd >= 0)
                {
                    Append(take[..lineEnd]);
                    var line = WithoutReturn(_line.AsSpan(0, _length));
                    _length = 0;
                    if (IsMatch(line)) return true;
                    chars = chars[(lineEnd + 1)..];
                }
                else
                {
                    Append(take);
                    chars = chars[take.Length..];
                    if (_length == MaxLineChars)
                    {
                        _length = 0;
                        if (IsMatch(_line.AsSpan(0, MaxLineChars))) return true;
                    }
                }
            }
            return false;
        }

        public override bool End() => _length > 0 && IsMatch(WithoutReturn(_line.AsSpan(0, _length)));

        private static ReadOnlySpan<char> WithoutReturn(ReadOnlySpan<char> line) => line.EndsWith('\r') ? line[..^1] : line;

        private void Append(ReadOnlySpan<char> chars)
        {
            if (_line.Length < _length + chars.Length)
                Array.Resize(ref _line, Math.Min(MaxLineChars, Math.Max(_line.Length * 2, _length + chars.Length)));
            chars.CopyTo(_line.AsSpan(_length));
            _length += chars.Length;
        }

        private bool IsMatch(ReadOnlySpan<char> line)
        {
            try
            {
                return regex.IsMatch(line);
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }
        }
    }
}
