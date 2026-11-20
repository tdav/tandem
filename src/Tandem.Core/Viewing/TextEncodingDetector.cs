using System.Buffers;
using System.Text;

namespace Tandem.Core.Viewing;

/// <summary>
/// The detector's verdict: the encoding to decode with, how many leading bytes are a byte order mark
/// to skip, and whether the content looks like binary data rather than text.
/// </summary>
public sealed record EncodingGuess(TextEncodingInfo Encoding, int BomLength, bool LooksBinary);

/// <summary>
/// Guesses the encoding of text from its first bytes. Never throws, whatever the bytes are.
/// </summary>
public static class TextEncodingDetector
{
    /// <summary>How many leading bytes of a file callers should pass as the sample.</summary>
    public const int SampleSize = 64 * 1024;

    private const int MinUtf16Sample = 16;
    private const double Utf16HighByteShare = 0.9;
    private const double Utf16LowByteShare = 0.5;
    /// <summary>A sample is binary when control characters other than tab, line feed, form feed, carriage return
    /// and escape make up more than this share of it.</summary>
    public const double BinaryControlShare = 0.1;

    /// <summary>
    /// Checks, in order: a byte order mark; UTF-16 without one (high bytes 0x00/0x04 on one parity);
    /// binary content (any 0x00, or more than 10 % control bytes); valid UTF-8 (including pure ASCII
    /// and an empty sample); otherwise Windows-1251. When <paramref name="sampleIsWholeFile"/> is false,
    /// a multi-byte UTF-8 character cut off at the end of the sample does not count against UTF-8.
    /// </summary>
    public static EncodingGuess Detect(ReadOnlySpan<byte> sample, bool sampleIsWholeFile)
    {
        if (sample.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
            return new(TextEncodings.Utf8, 3, false);
        if (sample.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]))
            return new(TextEncodings.Utf16LE, 2, false);
        if (sample.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
            return new(TextEncodings.Utf16BE, 2, false);

        if (sample.Length >= MinUtf16Sample)
        {
            var (even, odd) = HighByteShares(sample);
            if (odd >= Utf16HighByteShare && even < Utf16LowByteShare)
                return new(TextEncodings.Utf16LE, 0, false);
            if (even >= Utf16HighByteShare && odd < Utf16LowByteShare)
                return new(TextEncodings.Utf16BE, 0, false);
        }

        if (LooksBinary(sample))
            return new(TextEncodings.Windows1251, 0, true);

        return IsUtf8(sample, sampleIsWholeFile)
            ? new(TextEncodings.Utf8, 0, false)
            : new(TextEncodings.Windows1251, 0, false);
    }

    /// <summary>
    /// Share of bytes equal to 0x00 or 0x04 (the high byte of Latin and Cyrillic UTF-16 code units)
    /// at even and at odd positions, counted over whole byte pairs only.
    /// </summary>
    private static (double Even, double Odd) HighByteShares(ReadOnlySpan<byte> sample)
    {
        var pairs = sample.Length / 2;
        int even = 0, odd = 0;
        for (var i = 0; i < pairs * 2; i += 2)
        {
            if (IsUtf16HighByte(sample[i])) even++;
            if (IsUtf16HighByte(sample[i + 1])) odd++;
        }
        return ((double)even / pairs, (double)odd / pairs);
    }

    private static bool IsUtf16HighByte(byte b) => b is 0x00 or 0x04;

    private static bool LooksBinary(ReadOnlySpan<byte> sample)
    {
        if (sample.IsEmpty) return false;
        if (sample.Contains((byte)0x00)) return true;
        var controls = 0;
        foreach (var b in sample)
            if (b < 0x20 && b is not (0x09 or 0x0A or 0x0C or 0x0D or 0x1B))
                controls++;
        return (double)controls / sample.Length > BinaryControlShare;
    }

    private static bool IsUtf8(ReadOnlySpan<byte> sample, bool sampleIsWholeFile)
    {
        var rest = sample;
        while (!rest.IsEmpty)
        {
            var ascii = rest.IndexOfAnyExceptInRange((byte)0x00, (byte)0x7F);
            if (ascii < 0) return true;
            rest = rest[ascii..];

            var status = Rune.DecodeFromUtf8(rest, out _, out var consumed);
            switch (status)
            {
                case OperationStatus.Done:
                    rest = rest[consumed..];
                    break;
                case OperationStatus.NeedMoreData:
                    // Only reported for a valid but unfinished sequence that runs into the end of the sample,
                    // which is at most 3 bytes; excused only when the file goes on past the sample.
                    return !sampleIsWholeFile;
                default:
                    return false;
            }
        }
        return true;
    }
}
