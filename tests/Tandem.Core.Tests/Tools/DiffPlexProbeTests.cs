using System.Diagnostics;
using System.Text;
using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;

namespace Tandem.Core.Tests.Tools;

/// <summary>
/// Pins what DiffPlex 1.9.0 does, for the text comparison (task 9).
/// <list type="bullet">
/// <item>Side by side: <c>new SideBySideDiffBuilder(Differ.Instance).BuildDiffModel(old, new, ignoreWhitespace: false,
/// ignoreCase: false)</c> gives a <c>SideBySideDiffModel</c> whose <c>OldText.Lines</c> and <c>NewText.Lines</c>
/// (<c>List&lt;DiffPiece&gt;</c>) always have the same count: row i of one pane faces row i of the other. A line only
/// on one side is <c>Deleted</c> (old) or <c>Inserted</c> (new) facing an <c>Imaginary</c> filler with
/// <c>Text == null</c> and <c>Position == null</c>; a changed line is <c>Modified</c> on both sides. <c>Position</c> is
/// the 1-based line number in that text.</item>
/// <item><b>Whitespace is ignored by default</b>: the static <c>SideBySideDiffBuilder.Diff</c> has
/// <c>ignoreWhiteSpace = true</c> and so does the two-argument <c>BuildDiffModel</c>. Pass false explicitly.</item>
/// <item>A <c>Modified</c> line's <c>SubPieces</c> are <b>words and the separators between them</b> (the default word
/// chunker; a space is its own piece). A changed word is <c>Deleted</c> in the old line and <c>Inserted</c> in the new
/// one (not <c>Modified</c>); a word only on one side faces <c>Imaginary</c> pieces with empty text. An
/// <c>Unchanged</c> line has no sub-pieces.</item>
/// <item>Line endings: the line chunker splits on "\r\n", "\r" and "\n" and drops them, so "a\r\nb" and "a\nb" compare
/// <b>equal</b> (<c>HasDifferences == false</c>). Telling them apart is up to the caller. A final line break makes an
/// extra empty last line: "a\n" is two lines ("a", ""), so "a\n" against "a" is one <c>Deleted</c> empty line.</item>
/// <item>An empty text has no lines: empty against "x\ny" is two <c>Imaginary</c> rows facing two <c>Inserted</c>.</item>
/// </list>
/// </summary>
public sealed class DiffPlexProbeTests
{
    private static SideBySideDiffModel Diff(string oldText, string newText) =>
        new SideBySideDiffBuilder(Differ.Instance).BuildDiffModel(oldText, newText, ignoreWhitespace: false,
            ignoreCase: false);

    private static ChangeType[] Types(DiffPaneModel pane) => pane.Lines.Select(l => l.Type).ToArray();

    [Fact]
    public void Identical_texts_give_only_unchanged_lines()
    {
        var model = Diff("a\nb\nc", "a\nb\nc");

        Assert.False(model.OldText.HasDifferences);
        Assert.False(model.NewText.HasDifferences);
        Assert.Equal([ChangeType.Unchanged, ChangeType.Unchanged, ChangeType.Unchanged], Types(model.OldText));
        Assert.Equal(["a", "b", "c"], model.NewText.Lines.Select(l => l.Text));
        Assert.Equal([1, 2, 3], model.OldText.Lines.Select(l => l.Position));
    }

    [Fact]
    public void Inserted_deleted_and_modified_lines_are_aligned_side_by_side()
    {
        var model = Diff("keep\ngone\nsame\nold line", "keep\nsame\nadded\nnew line");

        Assert.Equal(model.OldText.Lines.Count, model.NewText.Lines.Count);
        var rows = model.OldText.Lines.Zip(model.NewText.Lines,
            (o, n) => (o.Type, o.Text, n.Type, n.Text)).ToArray();
        Assert.Equal(
        [
            (ChangeType.Unchanged, "keep", ChangeType.Unchanged, "keep"),
            (ChangeType.Deleted, "gone", ChangeType.Imaginary, null),
            (ChangeType.Unchanged, "same", ChangeType.Unchanged, "same"),
            (ChangeType.Modified, "old line", ChangeType.Modified, "added"),
            (ChangeType.Imaginary, null, ChangeType.Inserted, "new line"),
        ], rows!);
        Assert.Null(model.NewText.Lines[1].Position);
    }

    [Fact]
    public void Modified_line_reports_its_changed_pieces()
    {
        var model = Diff("hello brave world", "hello new world");

        var oldLine = Assert.Single(model.OldText.Lines);
        var newLine = Assert.Single(model.NewText.Lines);
        Assert.Equal(ChangeType.Modified, oldLine.Type);
        Assert.Equal(ChangeType.Modified, newLine.Type);
        // By words: "hello", " ", "brave"/"new", " ", "world".
        Assert.Equal(["hello", " ", "brave", " ", "world"], oldLine.SubPieces.Select(p => p.Text));
        Assert.Equal(["hello", " ", "new", " ", "world"], newLine.SubPieces.Select(p => p.Text));
        Assert.Equal([ChangeType.Unchanged, ChangeType.Unchanged, ChangeType.Deleted, ChangeType.Unchanged,
            ChangeType.Unchanged], oldLine.SubPieces.Select(p => p.Type));
        Assert.Equal([ChangeType.Unchanged, ChangeType.Unchanged, ChangeType.Inserted, ChangeType.Unchanged,
            ChangeType.Unchanged], newLine.SubPieces.Select(p => p.Type));

        var added = Diff("hello world", "hello big world");
        Assert.Equal([ChangeType.Unchanged, ChangeType.Unchanged, ChangeType.Imaginary, ChangeType.Imaginary,
            ChangeType.Unchanged], added.OldText.Lines[0].SubPieces.Select(p => p.Type));
        Assert.Equal(["hello", " ", "big", " ", "world"], added.NewText.Lines[0].SubPieces.Select(p => p.Text));
        Assert.Empty(Diff("same", "same").OldText.Lines[0].SubPieces);
    }

    [Fact]
    public void Line_endings_how_lines_are_split()
    {
        var crlf = Diff("a\r\nb", "a\nb");
        var cr = Diff("a\rb", "a\nb");

        Assert.False(crlf.OldText.HasDifferences); // the library does not see the difference
        Assert.Equal(["a", "b"], crlf.OldText.Lines.Select(l => l.Text));
        Assert.False(cr.OldText.HasDifferences);
        Assert.Equal(["a", "b"], cr.OldText.Lines.Select(l => l.Text));

        var finalBreak = Diff("a\n", "a");
        Assert.Equal(["a", ""], finalBreak.OldText.Lines.Select(l => l.Text));
        Assert.Equal([ChangeType.Unchanged, ChangeType.Deleted], Types(finalBreak.OldText));
        Assert.Equal([ChangeType.Unchanged, ChangeType.Imaginary], Types(finalBreak.NewText));
    }

    [Fact]
    public void Empty_against_non_empty_text()
    {
        var model = Diff("", "x\ny");

        Assert.Equal([ChangeType.Imaginary, ChangeType.Imaginary], Types(model.OldText));
        Assert.Equal([ChangeType.Inserted, ChangeType.Inserted], Types(model.NewText));
        Assert.True(model.NewText.HasDifferences);
        Assert.Empty(Diff("", "").OldText.Lines);
    }

    [Fact]
    public void Whitespace_is_ignored_unless_asked_not_to()
    {
        Assert.False(SideBySideDiffBuilder.Diff("a b", "a  b").OldText.HasDifferences);
        Assert.False(new SideBySideDiffBuilder(Differ.Instance).BuildDiffModel("a b", "a  b").OldText.HasDifferences);
        Assert.True(Diff("a b", "a  b").OldText.HasDifferences);
    }

    [Fact, Trait("Category", "Timing")]
    public void Fifty_thousand_lines_with_one_percent_changed_take_under_two_seconds()
    {
        var oldText = Lines(50_000, i => $"line {i} of the original text");
        var newText = Lines(50_000, i => i % 100 == 0 ? $"line {i} changed" : $"line {i} of the original text");

        var watch = Stopwatch.StartNew();
        var model = Diff(oldText, newText);
        watch.Stop();

        Assert.Equal(500, model.NewText.Lines.Count(l => l.Type == ChangeType.Modified));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"took {watch.Elapsed}");
    }

    [Fact, Trait("Category", "Timing")]
    public void Two_unrelated_texts_of_twenty_thousand_lines_finish_under_ten_seconds()
    {
        var oldText = Lines(20_000, i => $"old {i}");
        var newText = Lines(20_000, i => $"new {i}");

        var watch = Stopwatch.StartNew();
        var model = Diff(oldText, newText);
        watch.Stop();

        Assert.Equal(model.OldText.Lines.Count, model.NewText.Lines.Count);
        Assert.DoesNotContain(model.NewText.Lines, l => l.Type == ChangeType.Unchanged);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
    }

    private static string Lines(int count, Func<int, string> line)
    {
        var text = new StringBuilder();
        for (var i = 0; i < count; i++) text.Append(line(i)).Append('\n');
        return text.ToString(0, text.Length - 1); // no final line break: it would add an empty last line
    }
}
