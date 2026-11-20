using System.Text;
using Tandem.Core.Viewing;

namespace Tandem.Core.Tests.Viewing;

public class PagedTextReaderTests
{
    private const long OneMiB = 1 << 20;
    private const long EightGiB = 8L << 30;
    private const string Emoji = "😀"; // U+1F600: 4 bytes in UTF-8, a surrogate pair in UTF-16

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PagedTextReader Reader(string text, TextEncodingInfo? encoding = null, byte[]? bom = null)
    {
        encoding ??= TextEncodings.Utf8;
        byte[] bytes = [.. bom ?? [], .. encoding.Encoding.GetBytes(text)];
        return new PagedTextReader(new MemoryStream(bytes), encoding, bom?.Length ?? 0);
    }

    /// <summary>Reads pages of <paramref name="lineCount"/> lines from the start until an empty page.</summary>
    private static async Task<List<TextPage>> AllPages(PagedTextReader reader, int lineCount, long from = 0)
    {
        var pages = new List<TextPage>();
        var offset = from;
        while (true)
        {
            var page = await reader.ReadPageAsync(offset, lineCount, Ct);
            if (page.Lines.Count == 0) return pages;
            Assert.True(page.EndOffset > offset, "a page must move forward");
            pages.Add(page);
            offset = page.EndOffset;
        }
    }

    private static List<TextLine> AllLines(IEnumerable<TextPage> pages) => pages.SelectMany(p => p.Lines).ToList();

    /// <summary>Joins pieces back into text, putting a '\n' after every piece that ends its line.</summary>
    private static string Join(IEnumerable<TextLine> lines) =>
        string.Concat(lines.Select(l => l.EndsLine ? l.Text + "\n" : l.Text));

    [Fact]
    public async Task Reads_lines_with_offsets_and_strips_cr()
    {
        var reader = Reader("a\nbb\r\nccc");

        var page = await reader.ReadPageAsync(0, 10, Ct);

        Assert.Equal(
            [new TextLine(0, 2, "a", true), new TextLine(2, 4, "bb", true), new TextLine(6, 3, "ccc", false)],
            page.Lines);
        Assert.Equal(0, page.StartOffset);
        Assert.Equal(9, page.EndOffset);
    }

    [Fact]
    public async Task Tabs_lone_cr_and_empty_lines_are_kept()
    {
        var page = await Reader("\n\ta\rb\n\r\n").ReadPageAsync(0, 10, Ct);

        Assert.Equal(["", "\ta\rb", ""], page.Lines.Select(l => l.Text));
        Assert.All(page.Lines, l => Assert.True(l.EndsLine));
    }

    [Fact]
    public async Task Offset_inside_a_line_aligns_to_its_start()
    {
        var reader = Reader("first\nsecond line\nthird");

        Assert.Equal(0, await reader.LineStartAsync(3, Ct));
        Assert.Equal(6, await reader.LineStartAsync(6, Ct));
        Assert.Equal(6, await reader.LineStartAsync(12, Ct));
        Assert.Equal(6, await reader.LineStartAsync(17, Ct)); // on the '\n' itself
        Assert.Equal(18, await reader.LineStartAsync(20, Ct));

        var page = await reader.ReadPageAsync(9, 1, Ct);
        Assert.Equal(6, page.StartOffset);
        Assert.Equal("second line", Assert.Single(page.Lines).Text);
        Assert.Equal(18, page.EndOffset);
    }

    [Fact]
    public async Task Page_end_offset_is_the_start_of_the_next_page()
    {
        var text = string.Concat(Enumerable.Range(0, 200).Select(i => $"line {i} " + new string('x', i * 37 % 9000) + "\n"));
        var reader = Reader(text);

        foreach (var lineCount in new[] { 1, 3, 7 })
        {
            var pages = await AllPages(reader, lineCount);
            for (var i = 1; i < pages.Count; i++)
                Assert.Equal(pages[i - 1].EndOffset, pages[i].StartOffset);
            var lines = AllLines(pages);
            for (var i = 1; i < lines.Count; i++)
                Assert.Equal(lines[i - 1].Offset + lines[i - 1].ByteLength, lines[i].Offset);
            Assert.Equal(text, Join(lines));
            Assert.Equal(reader.Length, pages[^1].EndOffset);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("ab")]
    [InlineData("abc")]
    public async Task Utf8_characters_are_never_split(string prefix)
    {
        var text = prefix + new string('я', 5000);
        var reader = Reader(text);

        var lines = AllLines(await AllPages(reader, 1));

        Assert.True(lines.Count >= 3);
        Assert.All(lines, l => Assert.DoesNotContain('�', l.Text));
        Assert.All(lines, l => Assert.InRange(l.ByteLength, 1, PagedTextReader.MaxLineBytes));
        Assert.Equal(text, Join(lines));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("ab")]
    [InlineData("abc")]
    public async Task Utf8_emoji_are_never_split(string prefix)
    {
        var text = prefix + string.Concat(Enumerable.Repeat(Emoji, 3000)) + "\nend";
        var lines = AllLines(await AllPages(Reader(text), 2));

        Assert.All(lines, l => Assert.DoesNotContain('�', l.Text));
        Assert.Equal(text, Join(lines));
    }

    [Theory]
    [InlineData("utf-16le")]
    [InlineData("utf-16be")]
    public async Task Utf16_uses_two_byte_newlines_and_keeps_surrogate_pairs(string id)
    {
        var encoding = TextEncodings.Find(id)!;
        // In UTF-16LE 'Ċ' (U+010A) is the bytes 0A 01 and in UTF-16BE U+0A00 is 0A 00 — neither is a newline;
        // U+0A41 U+0400 U+4100 U+0A41 put 0A 00 (LE) and 00 0A (BE) at odd offsets.
        var text = "Привет\r\nĊ\u0A00 мир\u0A41\u0400\u4100\u0A41\n" + "a" + string.Concat(Enumerable.Repeat(Emoji, 3000)) + "\nконец";
        var reader = Reader(text, encoding);

        var page = await reader.ReadPageAsync(0, 2, Ct);
        Assert.Equal(new TextLine(0, 16, "Привет", true), page.Lines[0]);
        Assert.Equal("Ċ\u0A00 мир\u0A41\u0400\u4100\u0A41", page.Lines[1].Text);
        Assert.Equal(16, page.Lines[1].Offset);

        var lines = AllLines(await AllPages(reader, 3));
        Assert.All(lines, l => Assert.DoesNotContain('�', l.Text));
        Assert.All(lines, l => Assert.Equal(0, l.ByteLength % 2));
        Assert.Contains(lines, l => !l.EndsLine);
        Assert.Equal(text.Replace("\r\n", "\n"), Join(lines));
    }

    [Fact]
    public async Task Utf16_line_start_from_an_odd_offset_or_inside_a_surrogate_pair()
    {
        var text = "ab\n" + Emoji + "cd";
        var reader = Reader(text, TextEncodings.Utf16LE);

        Assert.Equal(6, await reader.LineStartAsync(7, Ct));  // odd: inside the high surrogate
        Assert.Equal(6, await reader.LineStartAsync(8, Ct));  // on the low surrogate
        Assert.Equal(0, await reader.LineStartAsync(5, Ct));  // inside the newline itself
        Assert.Equal(Emoji + "cd", Assert.Single((await reader.ReadPageAsync(9, 5, Ct)).Lines).Text);
    }

    [Fact]
    public async Task Long_line_is_cut_into_pieces_of_at_most_MaxLineBytes()
    {
        var text = new string('x', 10000) + "\nshort\n";
        var reader = Reader(text);

        var page = await reader.ReadPageAsync(0, 10, Ct);

        // Rows break every GridStep (4088) bytes from the line start, and after every line break.
        Assert.Equal(4088, PagedTextReader.GridStep);
        Assert.Equal(
            [(0L, 4088, false), (4088L, 4088, false), (8176L, 1825, true), (10001L, 6, true)],
            page.Lines.Select(l => (l.Offset, l.ByteLength, l.EndsLine)));
        Assert.Equal(new string('x', 1824), page.Lines[2].Text);
        Assert.Equal(4088, await reader.LineStartAsync(5000, Ct));
        Assert.Equal(8176, await reader.LineStartAsync(10000, Ct));
    }

    [Theory]
    [InlineData("\r\n")]
    [InlineData("\n")]
    public async Task Grid_point_on_a_line_break_makes_no_empty_row(string lineBreak)
    {
        // The grid point 4088 falls on the line break ('\n', or the '\n' of "\r\n"); the line still ends there.
        var text = new string('x', 4088 + 1 - lineBreak.Length) + lineBreak + "next";
        var lines = (await Reader(text).ReadPageAsync(0, 10, Ct)).Lines;

        Assert.Equal([new string('x', 4089 - lineBreak.Length), "next"], lines.Select(l => l.Text));
        Assert.Equal([true, false], lines.Select(l => l.EndsLine));
        Assert.Equal(0, await Reader(text).StepBackAsync(text.Length - 1, 1, Ct));
    }

    [Fact]
    public async Task Short_lines_are_never_split_by_the_grid()
    {
        // 16-byte lines straddle the grid points 4088, 8176, ...: each line is still one row.
        var text = string.Concat(Enumerable.Range(0, 2000).Select(i => $"line {i:D10}\n"));
        var lines = AllLines(await AllPages(Reader(text), 9));

        Assert.Equal(2000, lines.Count);
        Assert.All(lines, l => Assert.True(l.EndsLine));
    }

    [Fact]
    public async Task Single_byte_code_page_cuts_at_exactly_GridStep()
    {
        var text = new string('я', 5000) + "\nабв";
        var lines = AllLines(await AllPages(Reader(text, TextEncodings.Windows1251), 1));

        Assert.Equal([4088, 913, 3], lines.Select(l => l.ByteLength));
        Assert.Equal(text, Join(lines));
    }

    [Fact]
    public async Task StepBack_walks_lines_and_stops_at_the_start()
    {
        var reader = Reader("one\ntwo\nthree\n" + new string('я', 3000) + "\nlast");
        // Rows: 0 "one", 4 "two", 8 "three", 14 'я'×2044, 4102 'я'×956, 6015 "last"

        Assert.Equal(6015, await reader.StepBackAsync(6017, 0, Ct));
        Assert.Equal(4102, await reader.StepBackAsync(6017, 1, Ct));
        Assert.Equal(14, await reader.StepBackAsync(6017, 2, Ct));
        Assert.Equal(8, await reader.StepBackAsync(4103, 2, Ct)); // from inside a two-byte 'я'
        Assert.Equal(0, await reader.StepBackAsync(6017, 5, Ct));
        Assert.Equal(0, await reader.StepBackAsync(6017, 100, Ct));
        Assert.Equal(6015, await reader.StepBackAsync(reader.Length, 1, Ct));
        Assert.Equal(0, await reader.StepBackAsync(2, 1, Ct));
    }

    [Fact]
    public async Task StepBack_inside_emoji_lands_on_whole_characters()
    {
        foreach (var encoding in new[] { TextEncodings.Utf8, TextEncodings.Utf16LE, TextEncodings.Utf16BE })
        {
            var text = "a" + string.Concat(Enumerable.Repeat(Emoji, 3000));
            var reader = Reader(text, encoding);
            var pieces = AllLines(await AllPages(reader, 1)).Select(l => l.Offset).ToList();

            for (var offset = pieces[2] - 5; offset <= pieces[2] + 5; offset++)
            {
                var expected = offset >= pieces[2] ? pieces[1] : pieces[0];
                Assert.Equal(expected, await reader.StepBackAsync(offset, 1, Ct));
            }
        }
    }

    /// <summary>
    /// Lines of about 10 B, 5 KiB, 70 KiB and 300 KiB of mixed ASCII, Cyrillic, '€' and emoji (in windows-1251 the
    /// emoji is replaced by 'Ж'), ending alternately in "\n" and "\r\n".
    /// </summary>
    private static string MixedText(TextEncodingInfo encoding)
    {
        string[] alphabet = encoding.Encoding.IsSingleByte ? ["a", "я", "€", "Ж", " "] : ["a", "я", "€", Emoji, " "];
        var sb = new StringBuilder();
        var n = 0;
        var lineNo = 0;
        foreach (var target in new[] { 10, 5 * 1024, 70 * 1024, 300 * 1024, 10, 70 * 1024, 7 })
        {
            for (var lineBytes = 0; lineBytes < target;)
            {
                var piece = alphabet[n++ * 7 % 11 % alphabet.Length];
                sb.Append(piece);
                lineBytes += encoding.Encoding.GetByteCount(piece);
            }
            sb.Append(lineNo++ % 2 == 0 ? "\n" : "\r\n");
        }
        return sb.ToString();
    }

    [Theory]
    [InlineData("utf-8")]
    [InlineData("utf-16le")]
    [InlineData("utf-16be")]
    [InlineData("windows-1251")]
    public async Task Row_boundaries_do_not_depend_on_where_reading_started(string id)
    {
        var encoding = TextEncodings.Find(id)!;
        var text = MixedText(encoding);
        var bytes = encoding.Encoding.GetBytes(text);
        var stream = new SparseStream(bytes);
        var reader = new PagedTextReader(stream, encoding, 0);
        long len = bytes.Length;

        // Forward, one row at a time, from 0 to the end.
        var rows = new List<TextLine>();
        for (long p = 0; ;)
        {
            var page = await reader.ReadPageAsync(p, 1, Ct);
            AssertBounded(stream, 1, "ReadPage");
            if (page.Lines.Count == 0) break;
            Assert.Equal(p, page.StartOffset);
            rows.Add(page.Lines[0]);
            p = page.EndOffset;
        }
        var starts = rows.Select(r => r.Offset).ToList();
        Assert.Equal(len, rows[^1].Offset + rows[^1].ByteLength);
        Assert.All(rows, r => Assert.InRange(r.ByteLength, 1, PagedTextReader.MaxLineBytes));
        if (!encoding.Encoding.IsSingleByte)
            Assert.All(rows, r => Assert.DoesNotContain('\uFFFD', r.Text));
        Assert.Equal(text.Replace("\r\n", "\n"), Join(rows));

        // Pages of 7 rows forward give the same rows.
        Assert.Equal(rows, AllLines(await AllPages(reader, 7)));
        stream.ResetCounters();

        // Backward, one row at a time and 7 rows at a time, from the end to 0.
        var back = new List<long>();
        for (var p = len; p > 0;)
        {
            p = await reader.StepBackAsync(p, 1, Ct);
            AssertBounded(stream, 1, "StepBack");
            back.Add(p);
        }
        back.Reverse();
        Assert.Equal(starts, back);
        var index = starts.Count;
        for (var p = len; p > 0;)
        {
            p = await reader.StepBackAsync(p, 7, Ct);
            AssertBounded(stream, 7, "StepBack");
            index = Math.Max(0, index - 7);
            Assert.Equal(starts[index], p);
        }

        // Sampled offsets: around 64 KiB into each long line (the old transition), deep inside, and spread out.
        var lineStarts = rows.Where((r, i) => i == 0 || rows[i - 1].EndsLine).Select(r => r.Offset).ToList();
        var samples = new SortedSet<long>();
        foreach (var l in lineStarts)
            foreach (var around in new[] { 0L, 64 * 1024, 64 * 1024 + 2048, 200 * 1024 })
                for (var d = -40; d <= 40; d++)
                    samples.Add(Math.Clamp(l + around + d, 0, len));
        for (long o = 0; o <= len; o += 1009)
            samples.Add(o);
        samples.Add(len);

        foreach (var cur in samples)
        {
            var at = starts.FindLastIndex(s => s <= cur);
            var atOffset = cur >= len ? len : starts[at];
            var atIndex = cur >= len ? starts.Count : at;

            Assert.Equal(atOffset, await reader.LineStartAsync(cur, Ct));
            AssertBounded(stream, 0, "LineStart");

            // (1) One row up, then that row: it ends exactly where the row at cur starts.
            var up = await reader.StepBackAsync(cur, 1, Ct);
            AssertBounded(stream, 1, "StepBack");
            Assert.Equal(starts[Math.Max(0, atIndex - 1)], up);
            var upRow = await reader.ReadPageAsync(up, 1, Ct);
            AssertBounded(stream, 1, "ReadPage");
            Assert.Equal(up, upRow.StartOffset);
            if (atIndex > 0)
                Assert.Equal(atOffset, upRow.EndOffset);

            // (2) PageUp then PageDown returns to the same top row, and PageDown then PageUp too.
            var pageUp = await reader.StepBackAsync(cur, 10, Ct);
            AssertBounded(stream, 10, "StepBack");
            var upIndex = Math.Max(0, atIndex - 10);
            Assert.Equal(starts[upIndex], pageUp);
            if (atIndex > 0)
                Assert.Equal(atOffset, (await reader.ReadPageAsync(pageUp, atIndex - upIndex, Ct)).EndOffset);
            AssertBounded(stream, 10, "ReadPage");

            var down = await reader.ReadPageAsync(cur, 10, Ct);
            AssertBounded(stream, 10, "ReadPage");
            Assert.Equal(atOffset, down.StartOffset);
            Assert.Equal(atOffset, await reader.StepBackAsync(down.EndOffset, down.Lines.Count, Ct));
            stream.ResetCounters();
        }
    }

    [Fact]
    public async Task Bom_is_skipped()
    {
        var utf8 = Reader("a\nb", TextEncodings.Utf8, [0xEF, 0xBB, 0xBF]);
        var page = await utf8.ReadPageAsync(0, 10, Ct);
        Assert.Equal([new TextLine(3, 2, "a", true), new TextLine(5, 1, "b", false)], page.Lines);
        Assert.Equal(3, page.StartOffset);
        Assert.Equal(3, await utf8.StepBackAsync(5, 10, Ct));
        Assert.Equal(3, await utf8.LineStartAsync(1, Ct));

        var utf16 = Reader("я\n" + Emoji, TextEncodings.Utf16LE, [0xFF, 0xFE]);
        var lines = (await utf16.ReadPageAsync(0, 10, Ct)).Lines;
        Assert.Equal([new TextLine(2, 4, "я", true), new TextLine(6, 4, Emoji, false)], lines);
        Assert.Equal(2, await utf16.StepBackAsync(9, 10, Ct));
    }

    [Fact]
    public async Task Page_from_the_middle_of_8_GiB_reads_under_one_mebibyte()
    {
        // 2^29 lines "line NNNNNNNNNN\n", 16 bytes each.
        var stream = new SparseStream(EightGiB, pos => LineByte(pos / 16, (int)(pos % 16)));
        var reader = new PagedTextReader(stream, TextEncodings.Utf8, 0);
        const long middle = EightGiB / 2 + 7;

        var page = await reader.ReadPageAsync(middle, 60, Ct);

        Assert.True(stream.BytesRead < OneMiB, $"read {stream.BytesRead} bytes");
        Assert.Equal(EightGiB / 2, page.StartOffset);
        Assert.Equal(60, page.Lines.Count);
        Assert.Equal($"line {EightGiB / 2 / 16:D10}", page.Lines[0].Text);
        Assert.Equal($"line {EightGiB / 2 / 16 + 59:D10}", page.Lines[^1].Text);

        stream.ResetCounters();
        var back = await reader.StepBackAsync(page.StartOffset, 60, Ct);
        Assert.Equal(EightGiB / 2 - 60 * 16, back);
        Assert.True(stream.BytesRead < OneMiB, $"read {stream.BytesRead} bytes");

        stream.ResetCounters();
        var last = await reader.ReadPageAsync(await reader.StepBackAsync(EightGiB, 3, Ct), 10, Ct);
        Assert.Equal($"line {EightGiB / 16 - 1:D10}", last.Lines[^1].Text);
        Assert.Equal(3, last.Lines.Count);
        Assert.True(stream.BytesRead < OneMiB, $"read {stream.BytesRead} bytes");
    }

    private static byte LineByte(long line, int index) =>
        index switch
        {
            < 5 => (byte)"line "[index],
            15 => (byte)'\n',
            _ => (byte)('0' + line / Pow10(14 - index) % 10),
        };

    private static long Pow10(int n)
    {
        var r = 1L;
        for (var i = 0; i < n; i++) r *= 10;
        return r;
    }

    [Fact]
    public async Task File_of_one_8_GiB_line_pages_without_scanning()
    {
        var stream = new SparseStream(EightGiB, pos => (byte)('a' + pos % 26));
        var reader = new PagedTextReader(stream, TextEncodings.Utf8, 0);
        const long half = EightGiB / 2;

        var page = await reader.ReadPageAsync(half, 50, Ct);
        Assert.True(stream.BytesRead < OneMiB, $"page read {stream.BytesRead} bytes");
        Assert.Equal(50, page.Lines.Count);
        Assert.All(page.Lines, l => Assert.False(l.EndsLine));
        Assert.All(page.Lines, l => Assert.Equal(PagedTextReader.GridStep, l.ByteLength));
        Assert.Equal(half / PagedTextReader.GridStep * PagedTextReader.GridStep, page.StartOffset);

        stream.ResetCounters();
        var next = await reader.ReadPageAsync(page.EndOffset, 50, Ct);
        Assert.Equal(page.EndOffset, next.StartOffset);
        Assert.True(stream.BytesRead < OneMiB, $"next page read {stream.BytesRead} bytes");

        stream.ResetCounters();
        var back = await reader.StepBackAsync(page.StartOffset, 50, Ct);
        Assert.Equal(page.StartOffset - 50 * PagedTextReader.GridStep, back);
        Assert.True(stream.BytesRead < OneMiB, $"step back read {stream.BytesRead} bytes");

        stream.ResetCounters();
        var lastStart = await reader.StepBackAsync(EightGiB, 1, Ct);
        var last = await reader.ReadPageAsync(lastStart, 50, Ct);
        Assert.True(stream.BytesRead < OneMiB, $"end of file read {stream.BytesRead} bytes");
        Assert.Equal(EightGiB, last.EndOffset);
        Assert.False(Assert.Single(last.Lines).EndsLine);
    }

    private const int Lookahead = 8;

    /// <summary>The documented bound for StepBack, the loosest of the three: BackScanLimit + (lineCount + 1) × MaxLineBytes + 8.</summary>
    private static long Bound(int lineCount) =>
        PagedTextReader.BackScanLimit + (lineCount + 1L) * PagedTextReader.MaxLineBytes + Lookahead;

    private static void AssertBounded(SparseStream stream, int lineCount, string call)
    {
        Assert.True(stream.BytesRead <= Bound(lineCount), $"{call} read {stream.BytesRead} bytes");
        Assert.True(stream.MaxRequestedCount <= Bound(lineCount), $"{call} asked for {stream.MaxRequestedCount} bytes at once");
        stream.ResetCounters();
    }

    [Fact]
    public async Task Reads_are_bounded_by_the_documented_limit()
    {
        var stream = new SparseStream(EightGiB, pos => (byte)('a' + pos % 26));
        var reader = new PagedTextReader(stream, TextEncodings.Utf8, 0);

        await reader.ReadPageAsync(EightGiB / 3, 10, Ct);
        Assert.InRange(stream.BytesRead, 10 * PagedTextReader.GridStep,
            PagedTextReader.BackScanLimit + 10 * PagedTextReader.MaxLineBytes + Lookahead);
        AssertBounded(stream, 10, "ReadPage");

        await reader.LineStartAsync(EightGiB / 3, Ct);
        Assert.InRange(stream.BytesRead, 1, PagedTextReader.BackScanLimit + Lookahead);
        AssertBounded(stream, 0, "LineStart");

        await reader.StepBackAsync(EightGiB / 3, 10, Ct);
        Assert.True(stream.BytesRead > 0);
        AssertBounded(stream, 10, "StepBack");
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    public async Task Sparse_file_on_disk_opens_from_the_middle()
    {
        using var dir = new Vfs.TempDir();
        var path = dir.Combine("sparse.txt");
        const long length = 6L << 30;
        await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
        {
            file.Seek(length - 1, SeekOrigin.Begin);
            file.WriteByte((byte)'\n');
        }

        await using var stream = File.OpenRead(path);
        var reader = new PagedTextReader(stream, TextEncodings.Windows1251, 0);
        var page = await reader.ReadPageAsync(length / 2, 5, Ct);

        Assert.Equal(5, page.Lines.Count);
        Assert.All(page.Lines, l => Assert.Equal(new string('\0', PagedTextReader.GridStep), l.Text));
        var last = await reader.ReadPageAsync(await reader.StepBackAsync(length, 1, Ct), 5, Ct);
        Assert.True(Assert.Single(last.Lines).EndsLine);
    }

    [Fact]
    public async Task Reading_past_the_end_gives_an_empty_page()
    {
        var reader = Reader("abc\n");

        foreach (var offset in new long[] { 4, 5, 1000 })
        {
            var page = await reader.ReadPageAsync(offset, 10, Ct);
            Assert.Empty(page.Lines);
            Assert.Equal(4, page.StartOffset);
            Assert.Equal(4, page.EndOffset);
        }

        var empty = await Reader("").ReadPageAsync(0, 10, Ct);
        Assert.Empty(empty.Lines);
        Assert.Equal(0, empty.EndOffset);
    }

    [Fact]
    public void Non_seekable_stream_is_rejected()
    {
        var ex = Assert.Throws<ArgumentException>(() => new PagedTextReader(new NonSeekableStream(), TextEncodings.Utf8, 0));
        Assert.Contains("seek", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Leaves_the_stream_open()
    {
        var stream = new MemoryStream(Encoding.UTF8.GetBytes("x\n"));
        await new PagedTextReader(stream, TextEncodings.Utf8, 0).ReadPageAsync(0, 1, Ct);
        Assert.True(stream.CanRead);
    }

    [Fact]
    public async Task Concurrent_calls_do_not_interfere()
    {
        var text = string.Concat(Enumerable.Range(0, 5000).Select(i => $"row {i}\n"));
        var reader = Reader(text);
        long[] offsets = [30000, 100, 17, 40000];
        var expected = new List<TextPage>();
        foreach (var offset in offsets)
            expected.Add(await reader.ReadPageAsync(offset, 20, Ct));
        var expectedBack = await reader.StepBackAsync(30000, 20, Ct);

        var pages = Task.WhenAll(Enumerable.Range(0, 32).Select(i => Task.Run(() => reader.ReadPageAsync(offsets[i % 4], 20, Ct), Ct)));
        var backs = Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => reader.StepBackAsync(30000, 20, Ct), Ct)));
        var results = await pages;

        for (var i = 0; i < results.Length; i++)
        {
            Assert.Equal(expected[i % 4].StartOffset, results[i].StartOffset);
            Assert.Equal(expected[i % 4].EndOffset, results[i].EndOffset);
            Assert.Equal(expected[i % 4].Lines, results[i].Lines);
        }
        Assert.All(await backs, b => Assert.Equal(expectedBack, b));
    }
}
