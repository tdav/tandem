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
    /// Whether a UTF-16 guess <see cref="Detect"/> made without a byte order mark is unlikely to be text; callers
    /// should then treat the content as binary. The guess comes from the share of zero bytes at odd or even positions
    /// alone, which arrays of small 16-bit numbers (tables, sound samples) share with text, and binary data is not
    /// looked for after it. So the sample is decoded here and judged like the 8-bit check: binary when control
    /// characters other than tab, line feed, form feed, carriage return and escape make up more than
    /// <see cref="BinaryControlShare"/> of the units. A lone surrogate is binary at once; a high surrogate at the very
    /// end of a sample cut from a longer file is not counted. Any other guess gives false.
    /// </summary>
    public static bool IsUnlikelyUtf16(EncodingGuess guess, ReadOnlySpan<byte> sample, bool sampleIsWholeFile)
    {
        if (guess.BomLength != 0 || guess.LooksBinary) return false;
        bool bigEndian;
        if (guess.Encoding == TextEncodings.Utf16LE) bigEndian = false;
        else if (guess.Encoding == TextEncodings.Utf16BE) bigEndian = true;
        else return false;

        var units = sample.Length / 2;
        if (units == 0) return false;
        var controls = 0;
        for (var i = 0; i < units; i++)
        {
            var unit = Unit(sample, i, bigEndian);
            if (unit < 0x20)
            {
                if (unit is not ('\t' or '\n' or '\f' or '\r' or '\u001b')) controls++;
            }
            else if (char.IsHighSurrogate(unit))
            {
                if (i + 1 < units)
                {
                    if (!char.IsLowSurrogate(Unit(sample, i + 1, bigEndian))) return true;
                    i++;
                }
                else if (sampleIsWholeFile)
                {
                    return true;
                }
            }
            else if (char.IsLowSurrogate(unit))
            {
                return true;
            }
        }
        return (double)controls / units > BinaryControlShare;
    }

    private static char Unit(ReadOnlySpan<byte> bytes, int index, bool bigEndian) =>
        (char)(bigEndian
            ? bytes[2 * index] << 8 | bytes[2 * index + 1]
            : bytes[2 * index + 1] << 8 | bytes[2 * index]);

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
