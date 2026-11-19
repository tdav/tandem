using System.Runtime.CompilerServices;
using System.Text;
using Tandem.Core.Vfs;

namespace Tandem.Core.Listing;

/// <summary>
/// Builds comparers for directory listings. Directory-like entries always come before files, in either
/// direction. Names compare case-insensitively, after normalization to form C, with digit runs compared
/// by numeric value (<c>file2</c> &lt; <c>file10</c>). Permissions compare by their numeric mode, entries without one
/// first (last when descending). Ties on the sort key fall back to the name in
/// ascending order, and names equal after folding are finally ordered ordinally, so the order is total.
/// </summary>
public static class EntrySorter
{
    public static IComparer<FileEntry> Create(SortSpec spec) => new EntryComparer(spec);

    /// <summary>The case- and normalization-insensitive form of a name used for comparing and matching: form C,
    /// upper case (invariant). Two names that differ only in case or normalization form fold to the same string.</summary>
    public static string Fold(string name) => name.Normalize(NormalizationForm.FormC).ToUpperInvariant();

    private sealed class FoldedKeys(string name, string extension)
    {
        public readonly string Name = name;
        public readonly string Extension = extension;
    }

    // Folding allocates, so it is done once per entry and remembered for as long as the entry lives.
    private static readonly ConditionalWeakTable<FileEntry, FoldedKeys> Keys = new();

    private static FoldedKeys KeysOf(FileEntry entry) =>
        Keys.GetValue(entry, static e => new FoldedKeys(Fold(e.Name), Fold(e.Extension)));

    private sealed class EntryComparer(SortSpec spec) : IComparer<FileEntry>
    {
        public int Compare(FileEntry? x, FileEntry? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;

            if (x.IsDirectoryLike != y.IsDirectoryLike) return x.IsDirectoryLike ? -1 : 1;

            var result = spec.Column switch
            {
                SortColumn.Extension => CompareNatural(KeysOf(x).Extension, KeysOf(y).Extension),
                SortColumn.Size => x.Size.CompareTo(y.Size),
                SortColumn.Modified => x.Modified.UtcTicks.CompareTo(y.Modified.UtcTicks),
                // Entries without a mode (a provider without permissions) come together, before any mode.
                SortColumn.Mode => Nullable.Compare(ModeKey(x), ModeKey(y)),
                _ => CompareNames(x, y),
            };
            if (spec.Descending) result = -result;
            if (result != 0 || spec.Column == SortColumn.Name) return result;
            return CompareNames(x, y);
        }

        private static int? ModeKey(FileEntry entry) => entry.Mode is { } mode ? (int)mode : null;

        private static int CompareNames(FileEntry x, FileEntry y)
        {
            var result = CompareNatural(KeysOf(x).Name, KeysOf(y).Name);
            return result != 0 ? result : string.CompareOrdinal(x.Name, y.Name);
        }
    }

    /// <summary>Ordinal comparison in which each run of ASCII digits compares as one number.</summary>
    private static int CompareNatural(string a, string b)
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsAsciiDigit(a[i]) && char.IsAsciiDigit(b[j]))
            {
                var result = CompareNumberRuns(a, ref i, b, ref j);
                if (result != 0) return result;
            }
            else
            {
                if (a[i] != b[j]) return a[i].CompareTo(b[j]);
                i++;
                j++;
            }
        }
        return (a.Length - i).CompareTo(b.Length - j);
    }

    // Compares two digit runs by value without parsing, so runs of any length work; leading zeros are ignored.
    private static int CompareNumberRuns(string a, ref int i, string b, ref int j)
    {
        while (i < a.Length && a[i] == '0') i++;
        while (j < b.Length && b[j] == '0') j++;
        var startA = i;
        var startB = j;
        while (i < a.Length && char.IsAsciiDigit(a[i])) i++;
        while (j < b.Length && char.IsAsciiDigit(b[j])) j++;

        var lengthA = i - startA;
        var lengthB = j - startB;
        if (lengthA != lengthB) return lengthA.CompareTo(lengthB);
        return a.AsSpan(startA, lengthA).SequenceCompareTo(b.AsSpan(startB, lengthB));
    }
}
