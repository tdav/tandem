using Tandem.Core.Vfs;

namespace Tandem.Core.Tools.Compare;

public sealed record DirectoryDifference(IReadOnlySet<string> MarkLeft, IReadOnlySet<string> MarkRight,
    int UniqueLeft, int UniqueRight, int Differing);

public static class DirectoryComparer
{
    public static readonly TimeSpan DefaultTolerance = TimeSpan.FromSeconds(2);

    public static DirectoryDifference Compare(IReadOnlyList<FileEntry> left, IReadOnlyList<FileEntry> right,
        TimeSpan tolerance)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(tolerance, TimeSpan.Zero);
        var leftMarks = new HashSet<string>(StringComparer.Ordinal);
        var rightMarks = new HashSet<string>(StringComparer.Ordinal);
        int uniqueLeft = 0, uniqueRight = 0, differing = 0;
        foreach (var (l, r) in Pair(left, right))
        {
            if (l is null)
            {
                rightMarks.Add(r!.Name);
                uniqueRight++;
            }
            else if (r is null)
            {
                leftMarks.Add(l.Name);
                uniqueLeft++;
            }
            else if (AreDifferent(l, r, tolerance))
            {
                leftMarks.Add(l.Name);
                rightMarks.Add(r.Name);
                differing++;
            }
        }
        return new(leftMarks, rightMarks, uniqueLeft, uniqueRight, differing);
    }

    private static bool AreDifferent(FileEntry left, FileEntry right, TimeSpan tolerance) =>
        left.Kind != right.Kind || left.Kind switch
        {
            EntryKind.Directory => false,
            EntryKind.Symlink => !string.Equals(left.LinkTarget, right.LinkTarget, StringComparison.Ordinal),
            _ => left.Size != right.Size || (left.Modified - right.Modified).Duration() > tolerance,
        };

    // Prefer exact spellings when a server stores multiple names in the same normalization group.
    internal static IEnumerable<(FileEntry? Left, FileEntry? Right)> Pair(
        IEnumerable<FileEntry> left, IEnumerable<FileEntry> right)
    {
        var leftGroups = left.ToLookup(entry => NameKey.Of(entry.Name), StringComparer.Ordinal);
        var rightGroups = right.ToLookup(entry => NameKey.Of(entry.Name), StringComparer.Ordinal);
        foreach (var key in leftGroups.Select(group => group.Key).Union(rightGroups.Select(group => group.Key),
                     StringComparer.Ordinal))
        {
            var rightByName = rightGroups[key].ToDictionary(entry => entry.Name, StringComparer.Ordinal);
            var unmatchedLeft = new List<FileEntry>();
            foreach (var entry in leftGroups[key])
            {
                if (rightByName.Remove(entry.Name, out var exact)) yield return (entry, exact);
                else unmatchedLeft.Add(entry);
            }

            var unmatchedRight = rightByName.Values.OrderBy(entry => entry.Name, StringComparer.Ordinal).ToArray();
            unmatchedLeft.Sort((a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name));
            for (var i = 0; i < Math.Max(unmatchedLeft.Count, unmatchedRight.Length); i++)
                yield return (i < unmatchedLeft.Count ? unmatchedLeft[i] : null,
                    i < unmatchedRight.Length ? unmatchedRight[i] : null);
        }
    }
}
