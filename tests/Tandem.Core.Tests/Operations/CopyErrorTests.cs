using Tandem.Core.Operations;
using Tandem.Core.Vfs;
using Tandem.Core.Vfs.InMemory;

namespace Tandem.Core.Tests.Operations;

public sealed class CopyErrorTests
{
    private readonly InMemoryFileSystem _mem = new();
    private readonly ProviderRegistry _providers = new();
    private readonly ScriptedInteraction _interaction = new();

    public CopyErrorTests() => _providers.Register(_mem);

    private static VfsPath Mem(string path) => new("mem", path);

    private CopyOperation Copy(params string[] sources) =>
        new(_providers, sources.Select(Mem).ToArray(), Mem("/dst"), new CopyOptions(), _interaction);

    private void AssertNoPartFiles() =>
        Assert.DoesNotContain(_mem.AllPaths, p => p.EndsWith(".tandem-part", StringComparison.Ordinal));

    /// <summary>Makes <paramref name="operation"/> on <paramref name="path"/> fail the first
    /// <paramref name="times"/> times (every time when null).</summary>
    private void Fail(string operation, string path, Func<Exception> error, int? times = null)
    {
        var left = times ?? int.MaxValue;
        _mem.FailOn((op, p) => op == operation && p.Path == path && Interlocked.Decrement(ref left) >= 0, error);
    }

    [Fact]
    public async Task Failed_overwrite_keeps_old_file()
    {
        _mem.AddFile("/src/a.txt", "the new content");
        _mem.AddFile("/dst/a.txt", "old");
        _mem.FailWriteAfter("/dst/.a.txt.tandem-part", 3, () => new IOException("disk full"));
        _interaction.OnConflict(ConflictAction.Overwrite).OnError(ErrorAction.Skip);

        var result = await Copy("/src/a.txt").RunAsync();

        Assert.Equal(OperationState.Completed, result.State);
        Assert.Equal("old", _mem.ReadAllText("/dst/a.txt"));
        Assert.Equal(new SkippedItem(Mem("/src/a.txt"), SkipReason.Error, "disk full"), Assert.Single(result.Skipped));
        AssertNoPartFiles();
    }

    [Fact]
    public async Task Failed_move_into_place_restores_the_old_file()
    {
        _mem.AddFile("/src/a.txt", "the new content");
        _mem.AddFile("/dst/a.txt", "old");
        // The old file is set aside first; moving the finished part into place then fails.
        Fail("Move", "/dst/.a.txt.tandem-part", () => new IOException("rename failed"));
        _interaction.OnConflict(ConflictAction.Overwrite).OnError(ErrorAction.Skip);

        var result = await Copy("/src/a.txt").RunAsync();

        Assert.Equal(OperationState.Completed, result.State);
        Assert.Equal("old", _mem.ReadAllText("/dst/a.txt"));
        Assert.Equal(["/dst", "/dst/a.txt"], _mem.AllPaths.Where(p => p.StartsWith("/dst", StringComparison.Ordinal)));
        Assert.Equal("rename failed", Assert.Single(_interaction.Errors).Error.Message);
        Assert.Equal(new SkippedItem(Mem("/src/a.txt"), SkipReason.Error, "rename failed"),
            Assert.Single(result.Skipped));
    }

    [Fact]
    public async Task Error_retry_then_succeeds()
    {
        _mem.AddFile("/src/a.txt", "content");
        _mem.AddDirectory("/dst");
        Fail("OpenRead", "/src/a.txt", () => new VfsAccessDeniedException("denied"), times: 1);
        _interaction.OnError(ErrorAction.Retry);

        var result = await Copy("/src/a.txt").RunAsync();

        Assert.Equal(OperationState.Completed, result.State);
        Assert.Empty(result.Skipped);
        Assert.Equal("content", _mem.ReadAllText("/dst/a.txt"));
        var (item, error) = Assert.Single(_interaction.Errors);
        Assert.Equal(Mem("/src/a.txt"), item);
        Assert.Equal("denied", error.Message);
    }

    [Fact]
    public async Task Retry_after_a_late_failure_does_not_count_bytes_twice()
    {
        _mem.AddFile("/src/a.txt", "0123456789");
        _mem.AddDirectory("/dst");
        Fail("Move", "/dst/.a.txt.tandem-part", () => new IOException("rename failed"), times: 1);
        _interaction.OnError(ErrorAction.Retry);
        var op = Copy("/src/a.txt");

        await op.RunAsync();

        Assert.Equal("0123456789", _mem.ReadAllText("/dst/a.txt"));
        Assert.Equal(10, op.Progress.BytesTotal);
        Assert.Equal(10, op.Progress.BytesDone);
        Assert.Equal(1, op.Progress.FilesDone);
        AssertNoPartFiles();
    }

    [Fact]
    public async Task Error_skip_is_reported()
    {
        _mem.AddFile("/src/a.txt", "aaaa");
        _mem.AddFile("/src/b.txt", "bb");
        _mem.AddDirectory("/dst");
        Fail("OpenRead", "/src/a.txt", () => new VfsAccessDeniedException("denied"));
        _interaction.OnError(ErrorAction.Skip);
        var op = Copy("/src/a.txt", "/src/b.txt");

        var result = await op.RunAsync();

        Assert.Equal(OperationState.Completed, result.State);
        Assert.Equal(new SkippedItem(Mem("/src/a.txt"), SkipReason.Error, "denied"), Assert.Single(result.Skipped));
        Assert.False(_mem.Exists("/dst/a.txt"));
        Assert.Equal("bb", _mem.ReadAllText("/dst/b.txt"));
        Assert.Equal(op.Progress.FilesTotal, op.Progress.FilesDone);
        Assert.Equal(op.Progress.BytesTotal, op.Progress.BytesDone);
        Assert.Equal(2, op.Progress.BytesDone); // the skipped file's bytes leave the total
    }

    [Fact]
    public async Task Skipping_a_failed_write_removes_the_part_file()
    {
        _mem.AddFile("/src/a.txt", "0123456789");
        _mem.AddDirectory("/dst");
        _mem.FailWriteAfter("/dst/.a.txt.tandem-part", 4, () => new IOException("disk full"));
        _interaction.OnError(ErrorAction.Skip);
        var op = Copy("/src/a.txt");

        await op.RunAsync();

        Assert.Equal(["/dst"], _mem.AllPaths.Where(p => p.StartsWith("/dst", StringComparison.Ordinal)));
        Assert.Equal(op.Progress.BytesTotal, op.Progress.BytesDone);
    }

    [Fact]
    public async Task Cleanup_failure_does_not_mask_the_error()
    {
        _mem.AddFile("/src/a.txt", "0123456789");
        _mem.AddDirectory("/dst");
        _mem.FailWriteAfter("/dst/.a.txt.tandem-part", 4, () => new IOException("disk full"));
        Fail("Delete", "/dst/.a.txt.tandem-part", () => new IOException("cannot delete"));
        _interaction.OnError(ErrorAction.Skip);

        var result = await Copy("/src/a.txt").RunAsync();

        Assert.Equal("disk full", Assert.Single(_interaction.Errors).Error.Message);
        Assert.Equal("disk full", Assert.Single(result.Skipped).Detail);
        Assert.False(_mem.Exists("/dst/a.txt"));
    }

    [Fact]
    public async Task SkipAll_asks_once()
    {
        foreach (var name in new[] { "a", "b", "c" })
        {
            _mem.AddFile($"/src/{name}.txt", name);
            Fail("OpenRead", $"/src/{name}.txt", () => new VfsAccessDeniedException("denied " + name));
        }
        _mem.AddFile("/src/ok.txt", "ok");
        _mem.AddDirectory("/dst");
        _interaction.OnError(ErrorAction.SkipAll);

        var result = await Copy("/src/a.txt", "/src/b.txt", "/src/ok.txt", "/src/c.txt").RunAsync();

        Assert.Equal(OperationState.Completed, result.State);
        Assert.Single(_interaction.Errors);
        Assert.Equal(
            [
                new SkippedItem(Mem("/src/a.txt"), SkipReason.Error, "denied a"),
                new SkippedItem(Mem("/src/b.txt"), SkipReason.Error, "denied b"),
                new SkippedItem(Mem("/src/c.txt"), SkipReason.Error, "denied c"),
            ],
            result.Skipped);
        Assert.Equal("ok", _mem.ReadAllText("/dst/ok.txt"));
    }

    [Fact]
    public async Task Abort_ends_as_failed()
    {
        _mem.AddFile("/src/a.txt", "a");
        _mem.AddFile("/src/b.txt", "b");
        _mem.AddDirectory("/dst");
        Fail("OpenRead", "/src/a.txt", () => new VfsAccessDeniedException("denied"));
        _interaction.OnError(ErrorAction.Abort);
        var op = Copy("/src/a.txt", "/src/b.txt");

        var error = await Assert.ThrowsAsync<VfsAccessDeniedException>(() => op.RunAsync());

        Assert.Equal("denied", error.Message);
        Assert.Equal(OperationState.Failed, op.State);
        Assert.True(op.Completion.IsFaulted);
        Assert.False(_mem.Exists("/dst/b.txt"));
        AssertNoPartFiles();
    }

    [Fact]
    public async Task Unreadable_directory_skips_subtree_as_one_item()
    {
        _mem.AddFile("/src/tree/ok.txt", "ok");
        _mem.AddFile("/src/tree/locked/inner.txt", "inner");
        _mem.AddFile("/src/tree/locked/deeper/x.txt", "x");
        _mem.AddDirectory("/dst");
        Fail("Enumerate", "/src/tree/locked", () => new VfsAccessDeniedException("no access"));
        _interaction.OnError(ErrorAction.Skip);
        var op = Copy("/src/tree");

        var result = await op.RunAsync();

        Assert.Equal(OperationState.Completed, result.State);
        Assert.Equal(Mem("/src/tree/locked"), Assert.Single(_interaction.Errors).Item);
        Assert.Equal(new SkippedItem(Mem("/src/tree/locked"), SkipReason.Error, "no access"),
            Assert.Single(result.Skipped));
        Assert.Equal("ok", _mem.ReadAllText("/dst/tree/ok.txt"));
        Assert.False(_mem.Exists("/dst/tree/locked"));
        Assert.Equal(op.Progress.FilesTotal, op.Progress.FilesDone);
    }

    [Fact]
    public async Task Unreadable_directory_can_be_retried()
    {
        _mem.AddFile("/src/tree/inner.txt", "inner");
        _mem.AddDirectory("/dst");
        Fail("Enumerate", "/src/tree", () => new VfsAccessDeniedException("no access"), times: 1);
        _interaction.OnError(ErrorAction.Retry);

        var result = await Copy("/src/tree").RunAsync();

        Assert.Empty(result.Skipped);
        Assert.Equal("inner", _mem.ReadAllText("/dst/tree/inner.txt"));
    }

    [Fact]
    public async Task SkipAll_covers_scan_and_copy_errors()
    {
        _mem.AddFile("/src/tree/locked/inner.txt", "inner");
        _mem.AddFile("/src/tree/bad.txt", "bad");
        _mem.AddDirectory("/dst");
        Fail("Enumerate", "/src/tree/locked", () => new VfsAccessDeniedException("no access"));
        Fail("OpenRead", "/src/tree/bad.txt", () => new VfsAccessDeniedException("denied"));
        _interaction.OnError(ErrorAction.SkipAll);

        var result = await Copy("/src/tree").RunAsync();

        Assert.Single(_interaction.Errors);
        Assert.Equal(2, result.Skipped.Count);
    }
}
