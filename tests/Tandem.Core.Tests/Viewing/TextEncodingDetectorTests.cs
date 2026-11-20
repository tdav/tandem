using System.Text;
using Tandem.Core.Viewing;

namespace Tandem.Core.Tests.Viewing;

public class TextEncodingDetectorTests
{
    private const string Russian = "Привет, мир! Это текст в кодировке Windows-1251.";

    private static EncodingGuess Detect(byte[] sample, bool whole = true) =>
        TextEncodingDetector.Detect(sample, whole);

    private static byte[] Bytes(params int[] values) => values.Select(v => (byte)v).ToArray();

    [Fact]
    public void Bom_decides_utf8_utf16le_utf16be()
    {
        var utf8 = Detect([0xEF, 0xBB, 0xBF, (byte)'h', (byte)'i']);
        Assert.Equal("utf-8", utf8.Encoding.Id);
        Assert.Equal(3, utf8.BomLength);
        Assert.False(utf8.LooksBinary);

        var le = Detect([0xFF, 0xFE, (byte)'h', 0x00]);
        Assert.Equal("utf-16le", le.Encoding.Id);
        Assert.Equal(2, le.BomLength);
        Assert.False(le.LooksBinary);

        var be = Detect([0xFE, 0xFF, 0x00, (byte)'h']);
        Assert.Equal("utf-16be", be.Encoding.Id);
        Assert.Equal(2, be.BomLength);
        Assert.False(be.LooksBinary);
    }

    [Fact]
    public void Ascii_and_empty_are_utf8_and_not_binary()
    {
        foreach (var sample in new[] { Array.Empty<byte>(), Encoding.ASCII.GetBytes("Hello, world!\r\n\tTab\fFeed\x1b[0m") })
        {
            var guess = Detect(sample);
            Assert.Equal("utf-8", guess.Encoding.Id);
            Assert.Equal(0, guess.BomLength);
            Assert.False(guess.LooksBinary);
        }
    }

    [Fact]
    public void Russian_utf8_is_utf8()
    {
        var bytes = Encoding.UTF8.GetBytes(Russian);
        var guess = Detect(bytes);
        Assert.Equal("utf-8", guess.Encoding.Id);
        Assert.Equal(0, guess.BomLength);
        Assert.False(guess.LooksBinary);
        Assert.Equal(Russian, guess.Encoding.Encoding.GetString(bytes));
    }

    [Fact]
    public void Russian_windows1251_is_windows1251()
    {
        var bytes = TextEncodings.Windows1251.Encoding.GetBytes(Russian);
        Assert.Equal(0xCF, bytes[0]); // 'П' in Windows-1251: the sample really is single-byte Cyrillic

        foreach (var whole in new[] { true, false })
        {
            var guess = Detect(bytes, whole);
            Assert.Equal("windows-1251", guess.Encoding.Id);
            Assert.False(guess.LooksBinary);
            Assert.Equal(Russian, guess.Encoding.Encoding.GetString(bytes));
        }
    }

    [Fact]
    public void Single_1251_letter_among_ascii_is_windows1251()
    {
        var bytes = Encoding.ASCII.GetBytes("Price: 5 ").Concat(Bytes(0xF0, '.')).ToArray();
        var guess = Detect(bytes);
        Assert.Equal("windows-1251", guess.Encoding.Id);
        Assert.False(guess.LooksBinary);
        Assert.Equal("Price: 5 р.", guess.Encoding.Encoding.GetString(bytes));
    }

    [Theory]
    [InlineData("Привет, мир", 1)]   // 2-byte 'р' cut after its lead byte
    [InlineData("Привет, мир €", 1)] // 3-byte '€' cut to 2 bytes
    [InlineData("Привет, мир €", 2)] // 3-byte '€' cut to its lead byte
    [InlineData("Привет, мир 😀", 1)] // 4-byte emoji cut to 3 bytes
    [InlineData("Привет, мир 😀", 2)]
    [InlineData("Привет, мир 😀", 3)]
    public void Utf8_cut_inside_a_character_at_sample_end_is_still_utf8(string text, int cut)
    {
        var full = Encoding.UTF8.GetBytes(text);
        var sample = full[..^cut];
        var guess = Detect(sample, whole: false);
        Assert.Equal("utf-8", guess.Encoding.Id);
        Assert.False(guess.LooksBinary);
        Assert.StartsWith("Привет, ми", guess.Encoding.Encoding.GetString(sample));
    }

    [Fact]
    public void Truncated_sequence_in_a_whole_file_is_windows1251()
    {
        var sample = Encoding.UTF8.GetBytes("Привет, мир €")[..^1];
        Assert.Equal("windows-1251", Detect(sample, whole: true).Encoding.Id);
    }

    [Fact]
    public void Invalid_sequence_before_the_end_is_not_excused_by_a_partial_sample()
    {
        // 0xF0 0x2E is invalid wherever it is; a partial sample only excuses an unfinished tail.
        var sample = Encoding.UTF8.GetBytes("мир ").Concat(Bytes(0xF0, '.', 'x', 'y')).ToArray();
        Assert.Equal("windows-1251", Detect(sample, whole: false).Encoding.Id);
    }

    [Theory]
    [InlineData("Hello, world!!!!")]
    [InlineData("Привет, мир!!!!!")]
    public void Utf16le_without_bom_latin_and_cyrillic(string text)
    {
        var bytes = Encoding.Unicode.GetBytes(text);
        var guess = Detect(bytes);
        Assert.Equal("utf-16le", guess.Encoding.Id);
        Assert.Equal(0, guess.BomLength);
        Assert.False(guess.LooksBinary);
        Assert.Equal(text, guess.Encoding.Encoding.GetString(bytes));
    }

    [Theory]
    [InlineData("Hello, world!!!!")]
    [InlineData("Привет, мир!!!!!")]
    public void Utf16be_without_bom(string text)
    {
        var bytes = Encoding.BigEndianUnicode.GetBytes(text);
        var guess = Detect(bytes);
        Assert.Equal("utf-16be", guess.Encoding.Id);
        Assert.Equal(0, guess.BomLength);
        Assert.False(guess.LooksBinary);
        Assert.Equal(text, guess.Encoding.Encoding.GetString(bytes));
    }

    [Fact]
    public void Utf16_cut_to_an_odd_length_is_still_recognized()
    {
        var bytes = Encoding.Unicode.GetBytes("Привет, мир!!!!!")[..^1];
        Assert.Equal("utf-16le", Detect(bytes, whole: false).Encoding.Id);
    }

    [Fact]
    public void Short_utf16_without_bom_is_not_guessed_as_utf16()
    {
        var bytes = Encoding.Unicode.GetBytes("Hi!"); // 6 bytes: below the 16-byte minimum
        var guess = Detect(bytes);
        Assert.NotEqual("utf-16le", guess.Encoding.Id);
        Assert.True(guess.LooksBinary); // contains 0x00
    }

    [Fact]
    public void Png_header_and_zero_filled_data_look_binary()
    {
        var png = Bytes(0x89, 'P', 'N', 'G', 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 'I', 'H', 'D', 'R',
            0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x08, 0x06, 0x00, 0x00, 0x00);
        var pngGuess = Detect(png);
        Assert.True(pngGuess.LooksBinary);
        Assert.Equal("windows-1251", pngGuess.Encoding.Id);
        Assert.Equal(0, pngGuess.BomLength);

        var zeros = Detect(new byte[4096]);
        Assert.True(zeros.LooksBinary);
        Assert.Equal("windows-1251", zeros.Encoding.Id);
    }

    [Fact]
    public void Many_control_bytes_without_zeros_look_binary()
    {
        var sample = Enumerable.Range(0, 100).Select(i => (byte)(i % 5 == 0 ? 0x01 : 'a')).ToArray(); // 20 %
        Assert.True(Detect(sample).LooksBinary);

        var few = Enumerable.Range(0, 100).Select(i => (byte)(i % 20 == 0 ? 0x01 : 'a')).ToArray(); // 5 %
        Assert.False(Detect(few).LooksBinary);
    }

    [Fact]
    public void Detection_never_throws_on_any_bytes()
    {
        var random = new Random(20261120);
        List<byte[]> samples =
        [
            [], [0x00], [0xFF], [0xEF], [0xEF, 0xBB], [0xFE], [0xC0], [0x80],
            new byte[TextEncodingDetector.SampleSize],
            Enumerable.Repeat((byte)0xFF, 1000).ToArray(),
            Enumerable.Repeat((byte)0x04, 17).ToArray(),
        ];
        for (var length = 1; length < 64; length++)
        {
            var bytes = new byte[length];
            random.NextBytes(bytes);
            samples.Add(bytes);
        }
        var big = new byte[TextEncodingDetector.SampleSize];
        random.NextBytes(big);
        samples.Add(big);

        foreach (var sample in samples)
        foreach (var whole in new[] { true, false })
        {
            var guess = TextEncodingDetector.Detect(sample, whole);
            Assert.NotNull(guess.Encoding);
            Assert.InRange(guess.BomLength, 0, Math.Min(3, sample.Length));
            _ = guess.Encoding.Encoding.GetString(sample.AsSpan(guess.BomLength)); // decoding never throws either
        }
    }

    [Fact]
    public void Every_listed_encoding_roundtrips_russian_text_except_windows1252()
    {
        Assert.Equal(
            new[] { "utf-8", "utf-16le", "utf-16be", "windows-1251", "windows-1252", "koi8-r", "cp866", "x-mac-cyrillic" },
            TextEncodings.All.Select(e => e.Id));

        foreach (var info in TextEncodings.All)
        {
            var bytes = info.Encoding.GetBytes(Russian);
            var back = info.Encoding.GetString(bytes);
            if (info.Id == "windows-1252")
                Assert.NotEqual(Russian, back); // no Cyrillic in 1252: replaced, but never throws
            else
                Assert.Equal(Russian, back);
        }
    }

    [Fact]
    public void Listed_encodings_replace_invalid_bytes_instead_of_throwing()
    {
        Assert.Equal("a�b", TextEncodings.Utf8.Encoding.GetString(Bytes('a', 0xFF, 'b')));
        Assert.Contains('�', TextEncodings.Utf16LE.Encoding.GetString(Bytes(0x00, 0xD8, 'a', 0x00)));
        Assert.Contains('�', TextEncodings.Utf16BE.Encoding.GetString(Bytes(0xD8, 0x00, 0x00, 'a')));
        Assert.Equal("a?", TextEncodings.Find("windows-1252")!.Encoding.GetString(
            TextEncodings.Find("windows-1252")!.Encoding.GetBytes("aЖ")));
    }

    [Fact]
    public void Code_page_decoders_replace_undecodable_bytes_with_U_FFFD()
    {
        foreach (var info in TextEncodings.All.Where(i => i.Encoding.IsSingleByte))
        {
            var fallback = Assert.IsType<DecoderReplacementFallback>(info.Encoding.DecoderFallback);
            Assert.Equal("\uFFFD", fallback.DefaultString);
        }

        // .NET's tables give every byte of the listed code pages a character: 0x98, which windows-1251
        // leaves undefined, decodes to the C1 control U+0098 (as on Windows), not to a fallback. That
        // keeps such bytes round-tripping, so they are not "undecodable" and stay as they are.
        var cp1251 = TextEncodings.Windows1251.Encoding;
        Assert.Equal("a\u0098b", cp1251.GetString(Bytes('a', 0x98, 'b')));
        Assert.Equal(Bytes('a', 0x98, 'b'), cp1251.GetBytes("a\u0098b"));
    }

    [Fact]
    public void Shorthand_properties_are_the_listed_instances()
    {
        Assert.Same(TextEncodings.All[0], TextEncodings.Utf8);
        Assert.Same(TextEncodings.All[1], TextEncodings.Utf16LE);
        Assert.Same(TextEncodings.All[2], TextEncodings.Utf16BE);
        Assert.Same(TextEncodings.All[3], TextEncodings.Windows1251);
    }

    [Fact]
    public void Find_is_case_insensitive_and_null_for_unknown()
    {
        Assert.Same(TextEncodings.Windows1251, TextEncodings.Find("Windows-1251"));
        Assert.Same(TextEncodings.Utf8, TextEncodings.Find("UTF-8"));
        Assert.Equal("koi8-r", TextEncodings.Find("KOI8-R")?.Id);
        Assert.Null(TextEncodings.Find("ebcdic"));
        Assert.Null(TextEncodings.Find(""));
    }
}
