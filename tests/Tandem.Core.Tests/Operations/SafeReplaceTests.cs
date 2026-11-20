using System.Text;
using Tandem.Core.Operations;
using Tandem.Core.Vfs;
using Tandem.Core.Vfs.InMemory;

namespace Tandem.Core.Tests.Operations;

public sealed class SafeReplaceTests
{
    private readonly InMemoryFileSystem _mem = new();

    private static VfsPath Mem(string path) => new("mem", path);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Func<VfsPath, Task> WriteText(string content) => async part =>
    {
        await using var stream = await _mem.OpenWriteAsync(part, Ct);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(content), Ct);
    };

    private IEnumerable<string> PathsUnder(string directory) =>
        _mem.AllPaths.Where(p => p.StartsWith(directory, StringComparison.Ordinal));

    [Fact]
    public async Task Write_puts_content_under_real_name_and_leaves_no_part()
    {
        _mem.AddDirectory("/a");

        await SafeReplace.WriteAsync(_mem, Mem("/a/b.txt"), replace: false, WriteText("new"));

        Assert.Equal("new", _mem.ReadAllText("/a/b.txt"));
        Assert.Equal(["/a", "/a/b.txt"], PathsUnder("/a"));
    }

    [Fact]
    public async Task Write_failure_removes_part_and_keeps_missing_destination_missing()
    {
        _mem.AddDirectory("/a");
        VfsPath? written = null;

        var error = await Assert.ThrowsAsync<IOException>(() =>
            SafeReplace.WriteAsync(_mem, Mem("/a/b.txt"), replace: false, async part =>
            {
                written = part;
                await WriteText("half")(part);
                throw new IOException("disk full");
            }));

        Assert.Equal("disk full", error.Message);
        Assert.Equal(Mem("/a/.b.txt.tandem-part"), written);
        Assert.Equal(["/a"], PathsUnder("/a"));
    }

    [Fact]
    public async Task Replace_swaps_content_and_removes_backup()
    {
        _mem.AddFile("/a/b.txt", "old");

        await SafeReplace.WriteAsync(_mem, Mem("/a/b.txt"), replace: true, WriteText("new"));

        Assert.Equal("new", _mem.ReadAllText("/a/b.txt"));
        Assert.False(_mem.Exists("/a/.b.txt.tandem-old"));
        Assert.Equal(["/a", "/a/b.txt"], PathsUnder("/a"));
    }

    [Fact]
    public async Task Replace_keeps_old_file_when_write_fails()
    {
        _mem.AddFile("/a/b.txt", "old");
        _mem.FailWriteAfter("/a/.b.txt.tandem-part", 2, () => new IOException("disk full"));

        await Assert.ThrowsAsync<IOException>(() =>
            SafeReplace.WriteAsync(_mem, Mem("/a/b.txt"), replace: true, WriteText("the new content")));

        Assert.Equal("old", _mem.ReadAllText("/a/b.txt"));
        Assert.Equal(["/a", "/a/b.txt"], PathsUnder("/a"));
    }

    [Fact]
    public async Task Failed_move_into_place_restores_backup()
    {
        _mem.AddFile("/a/b.txt", "old");
        _mem.FailOn((op, p) => op == "Move" && p.Path == "/a/.b.txt.tandem-part",
            () => new IOException("rename failed"));

        var error = await Assert.ThrowsAsync<IOException>(() =>
            SafeReplace.WriteAsync(_mem, Mem("/a/b.txt"), replace: true, WriteText("new")));

        Assert.Equal("rename failed", error.Message);
        Assert.Equal("old", _mem.ReadAllText("/a/b.txt"));
        Assert.Equal(["/a", "/a/b.txt"], PathsUnder("/a"));
    }

    [Fact]
    public async Task Stale_backup_from_earlier_run_is_cleared()
    {
        _mem.AddFile("/a/b.txt", "old");
        _mem.AddFile("/a/.b.txt.tandem-old", "left by an earlier crash");

        await SafeReplace.WriteAsync(_mem, Mem("/a/b.txt"), replace: true, WriteText("new"));

        Assert.Equal("new", _mem.ReadAllText("/a/b.txt"));
        Assert.Equal(["/a", "/a/b.txt"], PathsUnder("/a"));
    }

    [Fact]
    public async Task Move_into_place_replaces_and_keeps_nothing_else()
    {
        _mem.AddFile("/a/b.txt", "old");
        _mem.AddFile("/a/new.txt", "new");

        await SafeReplace.MoveIntoPlaceAsync(_mem, Mem("/a/new.txt"), Mem("/a/b.txt"), replace: true);

        Assert.Equal("new", _mem.ReadAllText("/a/b.txt"));
        Assert.Equal(["/a", "/a/b.txt"], PathsUnder("/a"));
    }

    [Fact]
    public void Part_path_is_hidden_sibling()
    {
        Assert.Equal(Mem("/a/.b.txt.tandem-part"), SafeReplace.PartPathFor(Mem("/a/b.txt")));
        Assert.Equal(".tandem-part", SafeReplace.PartSuffix);
        Assert.Equal(".tandem-old", SafeReplace.BackupSuffix);
    }
}
