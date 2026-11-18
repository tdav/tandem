using Tandem.Core.Vfs;

namespace Tandem.Core.Tests.Vfs;

public class FileEntryTests
{
    private static FileEntry Entry(string name, EntryKind kind, bool linkPointsToDirectory = false) =>
        new(VfsPath.Local("/dir").Combine(name), kind, 0, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            Mode: null, IsHidden: false, LinkTarget: kind == EntryKind.Symlink ? "/target" : null,
            LinkPointsToDirectory: linkPointsToDirectory);

    private static FileEntry File(string name) => Entry(name, EntryKind.File);

    [Theory]
    [InlineData("report.pdf", "pdf")]
    [InlineData(".gitignore", "")]
    [InlineData("a.tar.gz", "gz")]
    [InlineData("noext", "")]
    public void Extension(string name, string ext) => Assert.Equal(ext, File(name).Extension);

    [Fact]
    public void Directory_has_empty_extension() => Assert.Equal("", Entry("photos.2024", EntryKind.Directory).Extension);

    [Fact]
    public void Name_comes_from_path() => Assert.Equal("report.pdf", File("report.pdf").Name);

    [Fact]
    public void Directory_likeness()
    {
        Assert.False(File("a.txt").IsDirectoryLike);
        Assert.True(Entry("d", EntryKind.Directory).IsDirectoryLike);
        Assert.True(Entry("l", EntryKind.Symlink, linkPointsToDirectory: true).IsDirectoryLike);
        Assert.False(Entry("l", EntryKind.Symlink, linkPointsToDirectory: false).IsDirectoryLike);
    }

    [Fact]
    public void Symlink_to_directory_has_empty_extension() =>
        Assert.Equal("", Entry("lib.d", EntryKind.Symlink, linkPointsToDirectory: true).Extension);
}
