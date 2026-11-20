using System.Text;
using Tandem.Core.Search;
using Tandem.Core.Tests.Viewing;
using Tandem.Core.Viewing;

namespace Tandem.Core.Tests.Search;

public class ContentMatcherTests
{
    private const int Block = ContentMatcher.BlockSize;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<bool> Match(ContentQuery query, byte[] content) =>
        new ContentMatcher(query).IsMatchAsync(new MemoryStream(content), Ct);

    private static Task<bool> Match(string text, byte[] content, bool caseSensitive = false) =>
        Match(new ContentQuery(text, CaseSensitive: caseSensitive), content);

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    private static byte[] Filler(int count, byte value = (byte)'x') => Enumerable.Repeat(value, count).ToArray();

    [Fact]
    public async Task Finds_text_that_straddles_a_block_boundary()
    {
        // "needle" starts 3 bytes before the end of the first block.
        var content = Filler(Block - 3).Concat(Utf8("needle")).Concat(Filler(100)).ToArray();

        Assert.True(await Match("needle", content));
        Assert.True(await Match("xneedlex", content, caseSensitive: true));
        Assert.False(await Match("needlf", content));

        // A boundary that falls inside the text at every offset across several blocks.
        for (var offset = 1; offset < 6; offset++)
        {
            var shifted = Filler(3 * Block - offset).Concat(Utf8("needle")).ToArray();
            Assert.True(await Match("needle", shifted), $"offset {offset}");
        }
    }

    [Fact]
    public async Task Case_insensitive_search_finds_cyrillic()
    {
        var content = Utf8("Первая строка.\nА здесь написано привет, мир.\n");

        Assert.True(await Match("ПРИВЕТ", content));
        Assert.True(await Match("Привет, МИР", content));
    }

    [Fact]
    public async Task Case_sensitive_search_does_not()
    {
        var content = Utf8("Первая строка.\nА здесь написано привет, мир.\n");

        Assert.False(await Match("ПРИВЕТ", content, caseSensitive: true));
        Assert.True(await Match("привет", content, caseSensitive: true));
    }

    [Fact]
    public async Task Auto_encoding_finds_russian_text_in_windows1251_file()
    {
        var content = TextEncodings.Windows1251.Encoding.GetBytes("Съешь же ещё этих мягких французских булок, да выпей чаю.");

        Assert.True(await Match("французских булок", content));
        Assert.True(await Match("ФРАНЦУЗСКИХ", content));
        Assert.False(await Match("немецких", content));
    }

    [Fact]
    public async Task Auto_encoding_finds_text_in_utf16_file()
    {
        const string text = "Hello, мир! Searching inside a UTF-16 text file works.";
        var le = new byte[] { 0xFF, 0xFE }.Concat(TextEncodings.Utf16LE.Encoding.GetBytes(text)).ToArray();
        var be = TextEncodings.Utf16BE.Encoding.GetBytes(text); // no byte order mark

        Assert.True(await Match("мир! searching", le));
        Assert.True(await Match("мир! searching", be));
        Assert.True(await Match(new ContentQuery("^Hello, мир", IsRegex: true), le)); // the mark is skipped
        Assert.False(await Match("goodbye", le));
    }

    [Fact]
    public async Task Explicit_encoding_overrides_detection()
    {
        var cp866 = TextEncodings.Find("cp866")!.Encoding;
        var content = cp866.GetBytes("Отчёт за ноябрь: всё в порядке.");

        Assert.False(await Match("ноябрь", content));
        Assert.True(await Match(new ContentQuery("НОЯБРЬ", EncodingId: "cp866"), content));
        Assert.True(await Match(new ContentQuery("ноябрь", EncodingId: "CP866"), content));
    }

    [Fact]
    public async Task Multibyte_character_split_between_blocks_is_decoded()
    {
        // 'ж' is two bytes in UTF-8, the emoji four; each starts one byte before a block boundary.
        var content = Filler(Block - 1).Concat(Utf8("жz")).ToArray();
        Assert.True(await Match("xжz", content, caseSensitive: true));

        var emoji = Filler(2 * Block - 1).Concat(Utf8("\U0001F600!")).ToArray();
        Assert.True(await Match("x\U0001F600!", emoji, caseSensitive: true));

        // A UTF-16 surrogate pair whose halves fall in different blocks.
        var utf16 = new byte[] { 0xFF, 0xFE }
            .Concat(TextEncodings.Utf16LE.Encoding.GetBytes(new string('y', Block / 2 - 2) + "\U0001F600z")).ToArray();
        Assert.True(await Match("y\U0001F600z", utf16, caseSensitive: true));
    }

    [Fact]
    public async Task Ascii_string_inside_a_binary_file_is_found()
    {
        var random = new Random(18);
        var noise = new byte[3 * Block];
        random.NextBytes(noise);
        for (var i = 0; i < noise.Length; i += 7) noise[i] = 0;
        var content = noise.Take(Block + 100)
            .Concat(Utf8("LICENSE-KEY=abc"))
            .Concat(noise.Skip(Block))
            .Concat(TextEncodings.Windows1251.Encoding.GetBytes("пароль"))
            .Concat(noise.Take(50))
            .ToArray();

        Assert.True(await Match("license-key=ABC", content));
        Assert.True(await Match("ПАРОЛЬ", content));
        Assert.False(await Match("LICENSE-KEY=abd", content));
    }

    [Fact]
    public async Task Binary_data_that_only_looks_like_utf16_is_searched_as_bytes()
    {
        // Small 16-bit numbers: every odd byte is zero, so the detector guesses UTF-16LE without a mark.
        var numbers = Enumerable.Range(0, Block).Select(i => (byte)(i % 2 == 0 ? 1 + i % 29 : 0)).ToArray();
        Assert.Equal(TextEncodings.Utf16LE, TextEncodingDetector.Detect(numbers, sampleIsWholeFile: false).Encoding);
        var content = numbers.Concat(Utf8("MAGIC-TAG")).Concat(numbers.Take(64)).ToArray();

        Assert.True(await Match("magic-tag", content));
    }

    [Fact]
    public async Task Regex_matches_within_a_line_and_not_across_lines()
    {
        var query = new ContentQuery(@"^ERROR \d+$", IsRegex: true);

        Assert.True(await Match(query, Utf8("start\r\nERROR 42\r\nend\r\n")));
        Assert.True(await Match(query, Utf8("start\nerror 7"))); // case-insensitive, last line without a break
        Assert.False(await Match(query with { CaseSensitive = true }, Utf8("start\nerror 7")));
        Assert.False(await Match(query, Utf8("start\nERROR\n42\n")));
        Assert.False(await Match(query, Utf8("ERROR 42 and more\n")));
        Assert.False(await Match(new ContentQuery(@"ERROR\s42", IsRegex: true), Utf8("ERROR\n42")));

        // A line that runs across a block boundary is still one line.
        var straddling = Filler(Block - 4, (byte)'\n').Concat(Utf8("ERROR 12345\n")).ToArray();
        Assert.True(await Match(query, straddling));

        // A line longer than MaxLineChars is matched in pieces of that length.
        var longLine = Filler(ContentMatcher.MaxLineChars + 10, (byte)'a').Concat(Utf8("ERROR 7\n")).ToArray();
        Assert.True(await Match(new ContentQuery(@"^a{10}ERROR \d+$", IsRegex: true), longLine));
        Assert.False(await Match(new ContentQuery(@"a{11}ERROR", IsRegex: true), longLine));
    }

    [Fact]
    public async Task Stops_reading_after_the_first_match()
    {
        var needle = Utf8("needle");
        var stream = new SparseStream(1L << 30, position => position is >= 100 and < 106 ? needle[position - 100] : (byte)'a');

        Assert.True(await new ContentMatcher(new ContentQuery("NEEDLE")).IsMatchAsync(stream, Ct));

        Assert.True(stream.BytesRead <= 2 * Block, $"read {stream.BytesRead} bytes");
    }

    [Fact]
    public void Empty_text_bad_regex_and_unknown_encoding_are_rejected()
    {
        Assert.Equal(SearchField.Content, Assert.Throws<SearchQueryException>(() => new ContentMatcher(new ContentQuery(""))).Field);
        Assert.Equal(SearchField.Content,
            Assert.Throws<SearchQueryException>(() => new ContentMatcher(new ContentQuery("", IsRegex: true))).Field);
        Assert.Equal(SearchField.Content,
            Assert.Throws<SearchQueryException>(() => new ContentMatcher(new ContentQuery("ERROR (", IsRegex: true))).Field);
        Assert.Equal(SearchField.Encoding,
            Assert.Throws<SearchQueryException>(() => new ContentMatcher(new ContentQuery("x", EncodingId: "klingon"))).Field);
    }
}
