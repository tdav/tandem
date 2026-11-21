using Tandem.Core.Operations;
using Tandem.Core.Tests.Operations;
using Tandem.Core.Tools.Sync;
using Tandem.Core.Vfs;
using Tandem.Core.Vfs.InMemory;

namespace Tandem.Core.Tests.Tools.Sync;

public sealed class SynchronizationTests
{
    private readonly InMemoryFileSystem _fs = new();
    private readonly ProviderRegistry _providers = new();
    private VfsPath Left => new(_fs.Id, "/left");
    private VfsPath Right => new(_fs.Id, "/right");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public SynchronizationTests()
    {
        _providers.Register(_fs);
        _fs.AddDirectory(Left.Path);
        _fs.AddDirectory(Right.Path);
    }

    [Fact]
    public async Task Compares_by_content_normalized_names_and_capabilities()
    {
        var when = DateTimeOffset.UnixEpoch;
        _fs.AddFile("/left/й", "same", when);
        _fs.AddFile("/right/и\u0306", "same", when.AddDays(1));
        _fs.AddFile("/left/different", "abc", when);
        _fs.AddFile("/right/different", "xyz", when);
        _fs.AddDirectory("/left/empty");
        _fs.AddFile("/right/new.txt", "right");
        _fs.AddFile("/right/ignored.bin", "x");
        var result = await Plan(new(SyncCompareMode.Content));
        Assert.Equal(SyncState.Equal, Item("й").State);
        Assert.Equal(SyncAction.None, Item("different").DefaultAction);
        Assert.Equal(SyncState.Different, Item("different").State);
        Assert.Equal(SyncAction.CopyToRight, Item("empty").DefaultAction);
        Assert.Equal(SyncAction.CopyToLeft, Item("new.txt").DefaultAction);
        Assert.Contains(SyncAction.DeleteRight, Item("new.txt").AllowedActions);

        var filtered = await Plan(new(Mask: "*.txt"));
        Assert.DoesNotContain(filtered.Items, item => item.RelativePath == "ignored.bin");
        SyncItem Item(string path) => Assert.Single(result.Items, item => item.RelativePath == path);
    }

    [Fact]
    public async Task Copies_both_ways_with_missing_folders_and_keeps_the_source_timestamp()
    {
        var when = DateTimeOffset.UnixEpoch;
        _fs.AddFile("/left/nested/a", "a", when);
        _fs.AddFile("/right/b", "right", when);
        _fs.AddFile("/left/replace", "left", when.AddDays(1));
        _fs.AddFile("/right/replace", "old", when);
        var plan = await Plan();
        var result = await Run(plan);
        Assert.Empty(result.Skipped);
        Assert.Equal("a", _fs.ReadAllText("/right/nested/a"));
        Assert.Equal("right", _fs.ReadAllText("/left/b"));
        Assert.Equal("left", _fs.ReadAllText("/right/replace"));
        Assert.All((await Plan()).Items, item => Assert.Equal(SyncState.Equal, item.State));
    }

    [Fact]
    public async Task Changing_either_side_after_planning_skips_the_action()
    {
        _fs.AddFile("/left/source", "left");
        _fs.AddFile("/left/destination", "left");
        _fs.AddFile("/right/deleted", "original");
        var plan = await Plan();
        _fs.AddFile("/left/source", "source has changed");
        _fs.AddFile("/right/destination", "new arrival");
        _fs.AddFile("/right/deleted", "the file changed");
        var decisions = plan.Items.Select(item =>
            new SyncDecision(item, item.RelativePath == "deleted" ? SyncAction.DeleteRight : item.DefaultAction)).ToArray();

        var result = await Run(plan, decisions);
        Assert.Equal(3, result.Skipped.Count);
        Assert.All(result.Skipped, skip => Assert.Equal(SkipReason.ChangedSinceCompared, skip.Reason));
        Assert.False(_fs.Exists("/right/source"));
        Assert.Equal("new arrival", _fs.ReadAllText("/right/destination"));
        Assert.Equal("the file changed", _fs.ReadAllText("/right/deleted"));
    }

    [Fact]
    public async Task Reuses_the_destination_spelling_of_paired_ancestor_folders()
    {
        _fs.AddFile("/left/й/new", "hello");
        _fs.AddDirectory("/right/и\u0306");
        var result = await Run(await Plan());
        Assert.Empty(result.Skipped);
        Assert.True(_fs.Exists("/right/и\u0306/new"));
        Assert.False(_fs.Exists("/right/й"));
    }

    [Fact]
    public async Task Refuses_nested_roots_and_mismatched_kinds()
    {
        _fs.AddFile("/left/item", "file");
        _fs.AddDirectory("/right/item");
        _fs.AddDirectory("/left/inner");
        await Assert.ThrowsAsync<ArgumentException>(() =>
            SyncPlanner.CompareAsync(_providers, Left, Left.Combine("inner"), new(), null, Ct));
        var plan = await Plan();
        var mismatch = Assert.Single(plan.Items, item => item.RelativePath == "item");
        Assert.Equal(SyncState.KindMismatch, mismatch.State);
        Assert.Equal([SyncAction.None], mismatch.AllowedActions);
        Assert.Throws<ArgumentException>(() => new SyncOperation(_providers, plan,
            [new(mismatch, SyncAction.CopyToRight)], new ScriptedInteraction()));
    }

    [Fact]
    public async Task Unreadable_folder_excludes_both_subtrees_and_service_files_are_ignored()
    {
        _fs.AddFile("/left/blocked/left.txt", "left");
        _fs.AddFile("/right/blocked/right.txt", "right");
        _fs.AddFile("/right/.file.tandem-part", "part");
        _fs.AddFile("/left/.file.tandem-old", "old");
        _fs.AddFile("/left/ordinary", "normal");
        _fs.FailOn((method, path) => method == "Enumerate" && path.Path == "/left/blocked",
            () => new VfsAccessDeniedException("blocked"));

        var plan = await Plan();
        Assert.Equal(["blocked"], plan.UnreadableFolders);
        Assert.Equal("ordinary", Assert.Single(plan.Items).RelativePath);
    }

    [Fact]
    public async Task Interrupted_overwrite_keeps_the_old_file_and_removes_service_names()
    {
        var older = DateTimeOffset.UnixEpoch;
        _fs.AddFile("/left/copy", "the new file", older.AddDays(1));
        _fs.AddFile("/right/copy", "the old file", older);
        var plan = await Plan();
        _fs.FailWriteAfter("/right/.copy.tandem-part", 3, () => new IOException("write failed"));
        var interaction = new ScriptedInteraction();
        interaction.OnError(ErrorAction.Skip);
        var operation = new SyncOperation(_providers, plan, [new(Assert.Single(plan.Items), SyncAction.CopyToRight)],
            interaction);

        var result = await operation.RunAsync();
        Assert.Equal(SkipReason.Error, Assert.Single(result.Skipped).Reason);
        Assert.Equal("the old file", _fs.ReadAllText("/right/copy"));
        Assert.False(_fs.Exists("/right/.copy.tandem-part"));
        Assert.False(_fs.Exists("/right/.copy.tandem-old"));
    }

    private Task<SyncComparison> Plan(SyncOptions? options = null) =>
        SyncPlanner.CompareAsync(_providers, Left, Right, options ?? new(), null, Ct);

    private Task<OperationResult> Run(SyncComparison plan, IReadOnlyList<SyncDecision>? decisions = null) =>
        new SyncOperation(_providers, plan, decisions ?? plan.Items.Select(item => new SyncDecision(item, item.DefaultAction))
            .ToArray(), new ScriptedInteraction()).RunAsync();
}
