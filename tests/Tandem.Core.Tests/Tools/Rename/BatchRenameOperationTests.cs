using Tandem.Core.Operations;
using Tandem.Core.Tests.Operations;
using Tandem.Core.Tools.Rename;
using Tandem.Core.Vfs;
using Tandem.Core.Vfs.InMemory;

namespace Tandem.Core.Tests.Tools.Rename;

public sealed class BatchRenameOperationTests
{
    [Fact]
    public async Task Swap_preserves_both_contents_and_can_be_undone()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile("/a", "A");
        fs.AddFile("/b", "B");
        var registry = new ProviderRegistry();
        registry.Register(fs);
        var a = new VfsPath(fs.Id, "/a");
        var b = new VfsPath(fs.Id, "/b");
        var operation = new BatchRenameOperation(registry, [new(a, b), new(b, a)], new ScriptedInteraction());
        await operation.RunAsync();
        Assert.Equal("B", fs.ReadAllText("/a"));
        Assert.Equal("A", fs.ReadAllText("/b"));
        Assert.Equal(2, operation.Completed.Count);
        var undo = new BatchRenameOperation(registry, [new(b, a), new(a, b)], new ScriptedInteraction(), operation.Completed);
        await undo.RunAsync();
        Assert.Equal("A", fs.ReadAllText("/a"));
        Assert.Equal("B", fs.ReadAllText("/b"));
    }

    [Fact]
    public async Task Cancellation_during_staging_restores_all_original_names()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile("/a", "A");
        fs.AddFile("/b", "B");
        var registry = new ProviderRegistry();
        registry.Register(fs);
        var a = new VfsPath(fs.Id, "/a");
        var b = new VfsPath(fs.Id, "/b");
        var operation = new BatchRenameOperation(registry, [new(a, b), new(b, a)], new ScriptedInteraction());
        var temporaryMoves = 0;
        fs.FailOn((method, path) =>
        {
            if (method == "Move" && path.Name.EndsWith(SafeReplace.RenameSuffix, StringComparison.Ordinal)
                                 && ++temporaryMoves == 2)
                operation.Cancel();
            return false;
        }, () => new IOException());

        Assert.Equal(OperationState.Cancelled, (await operation.RunAsync()).State);
        Assert.Empty(operation.Completed);
        Assert.Equal(["/a", "/b"], fs.AllPaths);
        Assert.Equal("A", fs.ReadAllText("/a"));
        Assert.Equal("B", fs.ReadAllText("/b"));
    }

    [Fact]
    public async Task Undo_skips_renamed_file_if_its_content_was_edited()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile("/original", "before");
        var registry = new ProviderRegistry();
        registry.Register(fs);
        var before = new VfsPath(fs.Id, "/original");
        var after = new VfsPath(fs.Id, "/renamed");
        var operation = new BatchRenameOperation(registry, [new(before, after)], new ScriptedInteraction());
        await operation.RunAsync();
        fs.AddFile(after.Path, "edited text");

        var undo = new BatchRenameOperation(registry, [new(after, before)], new ScriptedInteraction(), operation.Completed);
        var result = await undo.RunAsync();

        Assert.Equal(SkipReason.ChangedSinceCompared, Assert.Single(result.Skipped).Reason);
        Assert.Empty(undo.Completed);
        Assert.False(fs.Exists(before.Path));
        Assert.Equal("edited text", fs.ReadAllText(after.Path));
    }

    [Fact]
    public async Task Move_into_another_folder_is_rejected_before_other_items_are_changed()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile("/a", "A");
        fs.AddFile("/b", "B");
        var registry = new ProviderRegistry();
        registry.Register(fs);
        var operation = new BatchRenameOperation(registry,
            [new(new("mem", "/a"), new("mem", "/new-a")), new(new("mem", "/b"), new("mem", "/sub/new-b"))],
            new ScriptedInteraction());
        var error = await Assert.ThrowsAsync<OperationRejectedException>(() => operation.RunAsync());
        Assert.Equal(RejectionReason.InvalidName, error.Reason);
        Assert.Equal(["/a", "/b"], fs.AllPaths);
    }

    [Fact]
    public async Task Failure_halfway_through_a_cycle_rolls_it_back_without_leaving_staged_files()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile("/a", "A");
        fs.AddFile("/b", "B");
        var registry = new ProviderRegistry();
        registry.Register(fs);
        var a = new VfsPath(fs.Id, "/a");
        var b = new VfsPath(fs.Id, "/b");
        var operation = new BatchRenameOperation(registry, [new(a, b), new(b, a)],
            new ScriptedInteraction().OnError(ErrorAction.Abort));
        var callsOnA = 0;
        fs.FailOn((method, path) => method == "Move" && path == a && ++callsOnA == 2,
            () => new IOException("Second pass failed."));

        await Assert.ThrowsAsync<IOException>(() => operation.RunAsync());
        Assert.Empty(operation.Completed);
        Assert.Equal(["/a", "/b"], fs.AllPaths);
        Assert.Equal("A", fs.ReadAllText("/a"));
        Assert.Equal("B", fs.ReadAllText("/b"));
    }
}
