using Tandem.Core.Listing;
using Tandem.Core.Vfs;

namespace Tandem.Core.Tests.Listing;

public class EntrySorterTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static FileEntry Entry(string name, EntryKind kind = EntryKind.File, long size = 0,
        DateTimeOffset? modified = null, bool linkPointsToDirectory = false, UnixFileMode? mode = null) =>
        new(VfsPath.Local("/dir").Combine(name), kind, size, modified ?? T0, T0,
            Mode: mode, IsHidden: false, LinkTarget: kind == EntryKind.Symlink ? "/target" : null,
            LinkPointsToDirectory: linkPointsToDirectory);

    private static string[] Sorted(SortSpec spec, params FileEntry[] entries)
    {
        var list = entries.ToList();
        list.Sort(EntrySorter.Create(spec));
        return list.Select(e => e.Name).ToArray();
    }

    [Fact]
    public void Directories_first_even_when_descending()
    {
        FileEntry[] entries =
        [
            Entry("b.txt"), Entry("zdir", EntryKind.Directory), Entry("a.txt"),
            Entry("link", EntryKind.Symlink, linkPointsToDirectory: true), Entry("adir", EntryKind.Directory),
        ];
        Assert.Equal(["adir", "link", "zdir", "a.txt", "b.txt"], Sorted(new SortSpec(SortColumn.Name), entries));
        Assert.Equal(["zdir", "link", "adir", "b.txt", "a.txt"],
            Sorted(new SortSpec(SortColumn.Name, Descending: true), entries));
        Assert.Equal(["adir", "link", "zdir", "a.txt", "b.txt"],
            Sorted(new SortSpec(SortColumn.Size, Descending: true), entries));
    }

    [Fact]
    public void Symlink_to_file_sorts_with_files() =>
        Assert.Equal(["d", "a", "b"],
            Sorted(new SortSpec(SortColumn.Name), Entry("b"), Entry("a", EntryKind.Symlink), Entry("d", EntryKind.Directory)));

    [Fact]
    public void Mode_sorts_by_permissions_with_entries_without_a_mode_together_then_by_name()
    {
        const UnixFileMode rw = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        const UnixFileMode rwr = rw | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
        const UnixFileMode rwx = rwr | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
        FileEntry[] entries =
        [
            Entry("e.sh", mode: rwx), Entry("d.txt", mode: rwr), Entry("x.bin"), Entry("c.txt", mode: rw),
            Entry("b.txt", mode: rwr), Entry("a.bin"), Entry("dir", EntryKind.Directory, mode: rwx),
        ];

        Assert.Equal(["dir", "a.bin", "x.bin", "c.txt", "b.txt", "d.txt", "e.sh"],
            Sorted(new SortSpec(SortColumn.Mode), entries));
        Assert.Equal(["dir", "e.sh", "b.txt", "d.txt", "c.txt", "a.bin", "x.bin"],
            Sorted(new SortSpec(SortColumn.Mode, Descending: true), entries));
    }

    [Fact]
    public void Natural_numeric_order() =>
        Assert.Equal(["file1", "file2", "file10", "file010a", "file100"],
            Sorted(new SortSpec(SortColumn.Name), Entry("file10"), Entry("file100"), Entry("file2"),
                Entry("file010a"), Entry("file1")));

    [Fact]
    public void Numbers_beyond_long_range_compare_by_magnitude() =>
        Assert.Equal(["n99999999999999999999", "n100000000000000000000"],
            Sorted(new SortSpec(SortColumn.Name), Entry("n100000000000000000000"), Entry("n99999999999999999999")));

    [Fact]
    public void Case_insensitive() =>
        Assert.Equal(["A", "b", "c"], Sorted(new SortSpec(SortColumn.Name), Entry("b"), Entry("A"), Entry("c")));

    [Fact]
    public void Size_ties_break_by_name()
    {
        FileEntry[] entries = [Entry("c", size: 10), Entry("a", size: 10), Entry("b", size: 5), Entry("d", size: 20)];
        Assert.Equal(["b", "a", "c", "d"], Sorted(new SortSpec(SortColumn.Size), entries));
        Assert.Equal(["d", "a", "c", "b"], Sorted(new SortSpec(SortColumn.Size, Descending: true), entries));
    }

    [Fact]
    public void Modified_orders_by_instant_and_ties_by_name()
    {
        FileEntry[] entries =
        [
            Entry("late", modified: T0.AddHours(1)),
            Entry("b", modified: T0),
            Entry("a", modified: new DateTimeOffset(2026, 1, 1, 3, 0, 0, TimeSpan.FromHours(3))), // same instant as T0
        ];
        Assert.Equal(["a", "b", "late"], Sorted(new SortSpec(SortColumn.Modified), entries));
    }

    [Fact]
    public void Extension_is_case_insensitive_and_ties_by_name() =>
        Assert.Equal(["noext", "b.MD", "a.txt", "c.TXT"],
            Sorted(new SortSpec(SortColumn.Extension), Entry("c.TXT"), Entry("a.txt"), Entry("b.MD"), Entry("noext")));

    [Fact]
    public void Nfd_and_nfc_names_sort_together()
    {
        const string nfcShortI = "й";        // й, precomposed
        const string nfdShortI = "й";  // и + combining breve
        FileEntry[] entries =
        [
            Entry(nfdShortI + "c"), Entry(nfcShortI + "b"), Entry(nfdShortI + "a"), Entry("иz"),
        ];
        Assert.Equal(["иz", nfdShortI + "a", nfcShortI + "b", nfdShortI + "c"],
            Sorted(new SortSpec(SortColumn.Name), entries));
    }

    [Fact]
    public void Names_equal_after_folding_get_a_deterministic_order()
    {
        var comparer = EntrySorter.Create(new SortSpec(SortColumn.Name));
        var nfc = Entry("й");
        var nfd = Entry("й");
        var upper = Entry("A");
        var lower = Entry("a");

        Assert.NotEqual(0, comparer.Compare(nfc, nfd));
        Assert.Equal(-comparer.Compare(nfc, nfd), comparer.Compare(nfd, nfc));
        Assert.NotEqual(0, comparer.Compare(upper, lower));
        Assert.Equal(-comparer.Compare(upper, lower), comparer.Compare(lower, upper));
        Assert.Equal(0, comparer.Compare(lower, Entry("a")));
    }
}
