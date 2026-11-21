using Tandem.Core.Tools.Compare;
using Tandem.Core.Vfs;
using Tandem.Core.Vfs.InMemory;
using Tandem.Core.Viewing;

namespace Tandem.Core.Tests.Tools.Compare;

public sealed class ComparisonTests
{
    private static readonly DateTimeOffset Date = DateTimeOffset.UnixEpoch;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Directories_pair_normalized_names_but_keep_case_and_mark_metadata_changes()
    {
        FileEntry Entry(string name, long size = 1, int seconds = 0, EntryKind kind = EntryKind.File, string? target = null) =>
            new(new("m", "/" + name), kind, size, Date.AddSeconds(seconds), Date, null, false, target);

        var result = DirectoryComparer.Compare(
            [Entry("й"), Entry("File"), Entry("different"), Entry("time"), Entry("folder", kind: EntryKind.Directory),
                Entry("link", kind: EntryKind.Symlink, target: "left")],
            [Entry("и\u0306", seconds: 2), Entry("file"), Entry("different", 2), Entry("time", seconds: 3),
                Entry("folder", kind: EntryKind.Directory), Entry("link", kind: EntryKind.Symlink, target: "right")],
            DirectoryComparer.DefaultTolerance);
        Assert.Equal(1, result.UniqueLeft);
        Assert.Equal(1, result.UniqueRight);
        Assert.Equal(3, result.Differing);
        Assert.Equal(["File", "different", "link", "time"], result.MarkLeft.Order(StringComparer.Ordinal));
        Assert.Equal(["different", "file", "link", "time"], result.MarkRight.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Text_rows_align_and_report_inline_spans_and_block_starts()
    {
        var result = TextDiff.Compare("color = red\nkeep\ngone\nend", "color = blue\nkeep\nend\nadded");
        Assert.Equal([0, 2, 4], result.DifferenceStarts);
        Assert.Equal(new DiffSpan(8, 3), Assert.Single(result.Rows[0].LeftChanged));
        Assert.Equal(new DiffSpan(8, 4), Assert.Single(result.Rows[0].RightChanged));
        Assert.Null(result.Rows[2].RightNumber);
        Assert.Null(result.Rows[4].LeftNumber);
        Assert.Equal(4, result.Rows[4].RightNumber);
    }

    [Fact]
    public void Text_identifies_mixed_endings_empty_text_and_final_newline()
    {
        Assert.True(TextDiff.Compare("x\r\ny\r", "x\ny\n").LineEndingsDiffer);
        Assert.Empty(TextDiff.Compare("x\r\ny\r", "x\ny\n").DifferenceStarts);
        Assert.Equal(DiffKind.RightOnly, Assert.Single(TextDiff.Compare("", "x").Rows).Kind);
        Assert.Equal(DiffKind.LeftOnly, TextDiff.Compare("x\n", "x").Rows[^1].Kind);
    }

    [Fact]
    public void Text_options_and_long_changed_lines()
    {
        Assert.Empty(TextDiff.Compare("HELLO world", "hello  world", true, true).DifferenceStarts);
        var line = new string('x', TextDiff.MaxInlineLength + 1);
        Assert.Equal(new DiffSpan(0, line.Length), Assert.Single(TextDiff.Compare(line, "y").Rows[0].LeftChanged));
    }

    [Fact]
    public async Task Binary_counts_all_differences_and_truncates_the_report()
    {
        var left = new byte[3000];
        var right = new byte[3001];
        for (var i = 0; i < left.Length; i += 2) right[i] = 1;
        var result = await BinaryDiff.CompareAsync(new MemoryStream(left), new MemoryStream(right), null, Ct);
        Assert.Equal(1500, result.DifferingBytes);
        Assert.Equal(1000, result.Ranges.Count);
        Assert.Equal(new ByteRange(1998, 1), result.Ranges[^1]);
        Assert.True(result.Truncated);
        Assert.False(result.Identical);
    }

    [Fact]
    public async Task Binary_joins_a_range_crossing_read_blocks_and_stops_equality_early()
    {
        var left = new byte[1024 * 1024];
        var right = new byte[left.Length];
        Array.Fill(right, (byte)1, 60000, 100000);
        var result = await BinaryDiff.CompareAsync(new MemoryStream(left), new MemoryStream(right), null, Ct);
        Assert.Equal(new ByteRange(60000, 100000), Assert.Single(result.Ranges));
        using var first = new MemoryStream(left);
        using var second = new MemoryStream(right);
        Assert.False(await BinaryDiff.AreEqualAsync(first, second, null, null, Ct));
        Assert.InRange(first.Position, 1, left.Length / 2);
    }

    [Fact]
    public async Task File_auto_uses_detected_encoding_and_binary_content()
    {
        var fs = new InMemoryFileSystem();
        var registry = new ProviderRegistry();
        registry.Register(fs);
        var left = new VfsPath(fs.Id, "/left");
        var right = new VfsPath(fs.Id, "/right");
        fs.AddFile(left.Path, TextEncodings.Windows1251.Encoding.GetBytes("Привет"));
        fs.AddFile(right.Path, "Привет");
        var text = await FileComparison.CompareAsync(registry, left, right, CompareMode.Auto, null, null, null, Ct);
        Assert.Equal(TextEncodings.Windows1251, text.LeftEncoding);
        Assert.Empty(text.Text!.DifferenceStarts);
        fs.AddFile(right.Path, new byte[] { 0, 1, 2 });
        var binary = await FileComparison.CompareAsync(registry, left, right, CompareMode.Auto, null, null, null, Ct);
        Assert.NotNull(binary.Binary);
    }
}
