using Tandem.Core.Vfs;

namespace Tandem.Archives.Tests;

public sealed class ArchiveIndexTests
{
    private static readonly DateTimeOffset ArchiveTime = new(2025, 5, 6, 7, 8, 9, TimeSpan.Zero);
    private static readonly DateTimeOffset EntryTime = new(2024, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private static RawEntry File(string key, long size = 1) => new(key, false, size, EntryTime, null, false);

    private static RawEntry Dir(string key) => new(key, true, 0, EntryTime, null, false);

    private static RawEntry Link(string key, string target) => new(key, false, 0, EntryTime, target, false);

    private static List<string> Paths(ArchiveIndex index, string directory = "/")
    {
        var paths = new List<string>();
        void Walk(string dir)
        {
            foreach (var child in index.ChildrenOf(dir)!)
            {
                paths.Add(child.Path);
                if (child.Kind == EntryKind.Directory) Walk(child.Path);
            }
        }
        Walk(directory);
        return paths.Order(StringComparer.Ordinal).ToList();
    }

    [Fact]
    public void Slip_keys_are_confined_to_the_root()
    {
        var index = ArchiveIndex.Build(
            [File("../../evil.txt"), File("/abs.txt"), File("a/../../b.txt"), File("dir\\win.txt")], ArchiveTime);

        Assert.Equal(["/a", "/a/b.txt", "/abs.txt", "/dir", "/dir/win.txt", "/evil.txt"], Paths(index));
        Assert.Equal(EntryKind.File, index.Find("/evil.txt")!.Kind);
        Assert.Equal(EntryKind.Directory, index.Find("/dir")!.Kind);
    }

    [Fact]
    public void Entry_below_a_file_or_link_is_dropped()
    {
        var index = ArchiveIndex.Build(
            [Link("link", "/tmp"), File("link/file.txt"), File("plain.txt"), File("plain.txt/inner.txt"),
             Dir("plain.txt/sub/")],
            ArchiveTime);

        Assert.Equal(["/link", "/plain.txt"], Paths(index));
        var link = index.Find("/link")!;
        Assert.Equal(EntryKind.Symlink, link.Kind);
        Assert.Equal("/tmp", link.LinkTarget);
        Assert.Null(index.Find("/link/file.txt"));
        Assert.Null(index.ChildrenOf("/link"));
        Assert.Equal(3, index.RenamedOrDroppedCount);
    }

    [Fact]
    public void Missing_parent_folders_are_created()
    {
        var index = ArchiveIndex.Build([File("x/y/z.txt", 7), Dir("x/")], ArchiveTime);

        Assert.Equal(["/x", "/x/y", "/x/y/z.txt"], Paths(index));
        var y = index.Find("/x/y")!;
        Assert.Equal(EntryKind.Directory, y.Kind);
        Assert.Equal(ArchiveTime, y.Modified);
        Assert.Null(y.EntryKey);
        // A folder listed after its contents keeps its place and takes the time the archive gives it.
        Assert.Equal(EntryTime, index.Find("/x")!.Modified);
        Assert.Equal("x/", index.Find("/x")!.EntryKey);
        Assert.Equal(7, index.Find("/x/y/z.txt")!.Size);
        Assert.Equal(0, index.RenamedOrDroppedCount);
    }

    [Fact]
    public void Duplicate_names_are_numbered()
    {
        var index = ArchiveIndex.Build(
            [File("a.txt", 1), File("a.txt", 2), File("./a.txt", 3), File("noext"), File("noext"), Dir("d/"),
             Dir("d/"), File("d"), File(".hidden"), File(".hidden")],
            ArchiveTime);

        Assert.Equal(["/.hidden", "/.hidden (2)", "/a (2).txt", "/a (3).txt", "/a.txt", "/d", "/d (2)", "/noext",
                      "/noext (2)"], Paths(index));
        Assert.Equal(1, index.Find("/a.txt")!.Size);
        Assert.Equal(2, index.Find("/a (2).txt")!.Size);
        Assert.Equal(3, index.Find("/a (3).txt")!.Size);
        // The same key twice is told apart by its occurrence, so each name reads its own entry.
        Assert.Equal(("a.txt", 0), (index.Find("/a.txt")!.EntryKey, index.Find("/a.txt")!.KeyOccurrence));
        Assert.Equal(("a.txt", 1), (index.Find("/a (2).txt")!.EntryKey, index.Find("/a (2).txt")!.KeyOccurrence));
        Assert.Equal(EntryKind.Directory, index.Find("/d")!.Kind); // a repeated folder is the same folder
        Assert.Equal(EntryKind.File, index.Find("/d (2)")!.Kind);
        Assert.Equal(5, index.RenamedOrDroppedCount);
    }

    [Fact]
    public void Nul_in_a_name_is_replaced()
    {
        var index = ArchiveIndex.Build([File("bad\0name.txt"), File("\0")], ArchiveTime);

        Assert.Equal(["/_", "/bad_name.txt"], Paths(index));
        Assert.Equal(2, index.RenamedOrDroppedCount);
    }

    [Fact]
    public void Entry_without_a_name_is_dropped()
    {
        var index = ArchiveIndex.Build([File(""), File("/"), File("./.."), Dir("./"), File("ok.txt")], ArchiveTime);

        Assert.Equal(["/ok.txt"], Paths(index));
        Assert.Equal(4, index.RenamedOrDroppedCount);
    }

    [Fact]
    public void Root_is_a_folder_with_the_archive_time()
    {
        var index = ArchiveIndex.Build([], ArchiveTime);

        var root = index.Find("/")!;
        Assert.Equal(EntryKind.Directory, root.Kind);
        Assert.Equal(ArchiveTime, root.Modified);
        Assert.Empty(index.ChildrenOf("/")!);
        Assert.Null(index.Find("/missing"));
        Assert.Null(index.ChildrenOf("/missing"));
    }

    [Fact]
    public void Entry_without_a_time_gets_the_archive_time()
    {
        var index = ArchiveIndex.Build([new RawEntry("f", false, 1, null, null, true)], ArchiveTime);

        Assert.Equal(ArchiveTime, index.Find("/f")!.Modified);
        Assert.True(index.Find("/f")!.IsEncrypted);
    }
}
