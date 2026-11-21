using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;

namespace Tandem.Core.Tools.Compare;

public enum DiffKind { Same, Changed, LeftOnly, RightOnly }
public readonly record struct DiffSpan(int Start, int Length);
public sealed record DiffRow(DiffKind Kind, int? LeftNumber, string? LeftText, int? RightNumber, string? RightText,
    IReadOnlyList<DiffSpan> LeftChanged, IReadOnlyList<DiffSpan> RightChanged);
public sealed record TextDiffResult(IReadOnlyList<DiffRow> Rows, IReadOnlyList<int> DifferenceStarts, bool LineEndingsDiffer);

public static class TextDiff
{
    public const int MaxInlineLength = 10_000;

    public static TextDiffResult Compare(string left, string right, bool ignoreWhitespace = false, bool ignoreCase = false)
    {
        var model = new SideBySideDiffBuilder(Differ.Instance).BuildDiffModel(left, right, ignoreWhitespace, ignoreCase);
        var rows = new List<DiffRow>(model.OldText.Lines.Count);
        var starts = new List<int>();
        var inDifference = false;
        foreach (var (l, r) in model.OldText.Lines.Zip(model.NewText.Lines))
        {
            var kind = l.Type switch
            {
                ChangeType.Unchanged => DiffKind.Same,
                ChangeType.Imaginary => DiffKind.RightOnly,
                _ when r.Type == ChangeType.Imaginary => DiffKind.LeftOnly,
                _ => DiffKind.Changed,
            };
            var changed = kind != DiffKind.Same;
            if (changed && !inDifference) starts.Add(rows.Count);
            inDifference = changed;
            var longLine = (l.Text?.Length ?? 0) > MaxInlineLength || (r.Text?.Length ?? 0) > MaxInlineLength;
            rows.Add(new(kind, l.Position, l.Text, r.Position, r.Text,
                kind == DiffKind.Changed ? Spans(l, longLine) : [],
                kind == DiffKind.Changed ? Spans(r, longLine) : []));
        }
        return new(rows, starts, starts.Count == 0 && HasDifferentEndings(left, right));
    }

    private static IReadOnlyList<DiffSpan> Spans(DiffPiece line, bool longLine)
    {
        if (longLine) return line.Text is { Length: > 0 } text ? [new(0, text.Length)] : [];
        var spans = new List<DiffSpan>();
        var offset = 0;
        foreach (var piece in line.SubPieces)
        {
            var length = piece.Text?.Length ?? 0;
            if (length == 0) continue;
            if (piece.Type != ChangeType.Unchanged)
            {
                if (spans.Count > 0 && spans[^1].Start + spans[^1].Length == offset)
                    spans[^1] = spans[^1] with { Length = spans[^1].Length + length };
                else
                    spans.Add(new(offset, length));
            }
            offset += length;
        }
        return spans;
    }

    private static bool HasDifferentEndings(string left, string right) => !Endings(left).SequenceEqual(Endings(right));

    private static IEnumerable<char> Endings(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                yield return 'B';
                i++;
            }
            else if (text[i] is '\r' or '\n')
            {
                yield return text[i];
            }
        }
    }
}
