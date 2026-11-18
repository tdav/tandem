using Tandem.Core.Operations;
using Tandem.Core.Vfs;
using Tandem.Core.Vfs.InMemory;

namespace Tandem.Core.Tests.Operations;

public sealed class CopyConflictTests
{
    private static readonly DateTimeOffset Older = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Newer = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly InMemoryFileSystem _mem = new();
    private readonly ProviderRegistry _providers = new();
    private readonly ScriptedInteraction _interaction = new();

    public CopyConflictTests() => _providers.Register(_mem);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static VfsPath Mem(string path) => new("mem", path);

    private CopyOperation Copy(params string[] sources) =>
        new(_providers, sources.Select(Mem).ToArray(), Mem("/dst"), new CopyOptions(), _interaction);

    private void AssertNoPartFiles() =>
        Assert.DoesNotContain(_mem.AllPaths, p => p.EndsWith(".tandem-part", StringComparison.Ordinal));

    [Fact]
    public async Task Conflict_skip_keeps_existing()
    {
        _mem.AddFile("/src/a.txt", "new");
        _mem.AddFile("/dst/a.txt", "old");
        _interaction.OnConflict(ConflictAction.Skip);
        var op = Copy("/src/a.txt");

        var result = await op.RunAsync();

        Assert.Equal(OperationState.Completed, result.State);
        Assert.Equal("old", _mem.ReadAllText("/dst/a.txt"));
        var (source, existing) = Assert.Single(_interaction.Conflicts);
        Assert.Equal(Mem("/src/a.txt"), source.Path);
        Assert.Equal(Mem("/dst/a.txt"), existing.Path);
        Assert.Equal(new SkippedItem(Mem("/src/a.txt"), SkipReason.ConflictSkipped), Assert.Single(result.Skipped));
        Assert.Equal(op.Progress.FilesTotal, op.Progress.FilesDone);
        Assert.Equal(op.Progress.BytesTotal, op.Progress.BytesDone);
        AssertNoPartFiles();
    }

    [Fact]
    public async Task Conflict_overwrite_replaces()
    {
        _mem.AddFile("/src/a.txt", "new content", Older);
        _mem.AddFile("/dst/a.txt", "old", Newer);
        _interaction.OnConflict(ConflictAction.Overwrite);

        var result = await Copy("/src/a.txt").RunAsync();

        Assert.Equal(OperationState.Completed, result.State);
        Assert.Empty(result.Skipped);
        Assert.Equal("new content", _mem.ReadAllText("/dst/a.txt"));
        Assert.Equal(Older, (await _mem.GetEntryAsync(Mem("/dst/a.txt"), Ct))!.Modified);
        AssertNoPartFiles();
    }

    [Fact]
    public async Task OverwriteOlder_replaces_only_when_source_newer()
    {
        _mem.AddFile("/src/fresh.txt", "new fresh", Newer);
        _mem.AddFile("/dst/fresh.txt", "old fresh", Older);
        _mem.AddFile("/src/stale.txt", "new stale", Older);
        _mem.AddFile("/dst/stale.txt", "old stale", Newer);
        _interaction.OnConflict(ConflictAction.OverwriteOlder).OnConflict(ConflictAction.OverwriteOlder);

        var result = await Copy("/src/fresh.txt", "/src/stale.txt").RunAsync();

        Assert.Equal(OperationState.Completed, result.State);
        Assert.Equal("new fresh", _mem.ReadAllText("/dst/fresh.txt"));
        Assert.Equal("old stale", _mem.ReadAllText("/dst/stale.txt"));
        Assert.Equal(new SkippedItem(Mem("/src/stale.txt"), SkipReason.OlderThanDestination),
            Assert.Single(result.Skipped));
        AssertNoPartFiles();
    }

    [Fact]
    public async Task OverwriteOlder_skips_when_times_are_equal()
    {
        _mem.AddFile("/src/a.txt", "new", Older);
        _mem.AddFile("/dst/a.txt", "old", Older);
        _interaction.OnConflict(ConflictAction.OverwriteOlder);

        var result = await Copy("/src/a.txt").RunAsync();

        Assert.Equal("old", _mem.ReadAllText("/dst/a.txt"));
        Assert.Equal(SkipReason.OlderThanDestination, Assert.Single(result.Skipped).Reason);
    }

    [Fact]
    public async Task Rename_picks_first_free_suffix()
    {
        _mem.AddFile("/src/a.txt", "new");
        _mem.AddFile("/dst/a.txt", "old");
        _mem.AddFile("/dst/a (2).txt", "two");
        _interaction.OnConflict(ConflictAction.Rename);

        var result = await Copy("/src/a.txt").RunAsync();

        Assert.Equal(OperationState.Completed, result.State);
        Assert.Empty(result.Skipped);
        Assert.Equal("new", _mem.ReadAllText("/dst/a (3).txt"));
        Assert.Equal("old", _mem.ReadAllText("/dst/a.txt"));
        Assert.Equal("two", _mem.ReadAllText("/dst/a (2).txt"));
        AssertNoPartFiles();
    }

    [Theory]
    [InlineData("README", "README (2)")]
    [InlineData(".profile", ".profile (2)")]
    [InlineData("archive.tar.gz", "archive.tar (2).gz")]
    public async Task Rename_suffix_goes_before_the_extension(string name, string renamed)
    {
        _mem.AddFile("/src/" + name, "new");
        _mem.AddFile("/dst/" + name, "old");
        _interaction.OnConflict(ConflictAction.Rename);

        await Copy("/src/" + name).RunAsync();

        Assert.Equal("new", _mem.ReadAllText("/dst/" + renamed));
        Assert.Equal("old", _mem.ReadAllText("/dst/" + name));
    }

    [Fact]
    public async Task ApplyToAll_asks_once()
    {
        foreach (var name in new[] { "a", "b", "c" })
        {
            _mem.AddFile($"/src/{name}.txt", "new " + name);
            _mem.AddFile($"/dst/{name}.txt", "old");
        }
        _interaction.OnConflict(ConflictAction.Overwrite, applyToAll: true);

        var result = await Copy("/src/a.txt", "/src/b.txt", "/src/c.txt").RunAsync();

        Assert.Equal(OperationState.Completed, result.State);
        Assert.Single(_interaction.Conflicts);
        Assert.Equal("new a", _mem.ReadAllText("/dst/a.txt"));
        Assert.Equal("new b", _mem.ReadAllText("/dst/b.txt"));
        Assert.Equal("new c", _mem.ReadAllText("/dst/c.txt"));
    }

    [Fact]
    public async Task Answer_without_ApplyToAll_is_asked_again()
    {
        _mem.AddFile("/src/a.txt", "new a");
        _mem.AddFile("/dst/a.txt", "old");
        _mem.AddFile("/src/b.txt", "new b");
        _mem.AddFile("/dst/b.txt", "old");
        _interaction.OnConflict(ConflictAction.Skip).OnConflict(ConflictAction.Overwrite);

        await Copy("/src/a.txt", "/src/b.txt").RunAsync();

        Assert.Equal(2, _interaction.Conflicts.Count);
        Assert.Equal("old", _mem.ReadAllText("/dst/a.txt"));
        Assert.Equal("new b", _mem.ReadAllText("/dst/b.txt"));
    }

    [Fact]
    public async Task Conflict_cancel_stops_operation()
    {
        _mem.AddFile("/src/a.txt", "a");
        _mem.AddFile("/src/b.txt", "new b");
        _mem.AddFile("/src/c.txt", "c");
        _mem.AddFile("/dst/b.txt", "old b");
        _interaction.OnConflict(ConflictAction.Cancel);
        var op = Copy("/src/a.txt", "/src/b.txt", "/src/c.txt");

        var result = await op.RunAsync();

        Assert.Equal(OperationState.Cancelled, result.State);
        Assert.Equal(OperationState.Cancelled, op.State);
        Assert.Equal("a", _mem.ReadAllText("/dst/a.txt"));
        Assert.Equal("old b", _mem.ReadAllText("/dst/b.txt"));
        Assert.False(_mem.Exists("/dst/c.txt"));
        AssertNoPartFiles();
    }

    [Fact]
    public async Task Directories_merge_without_prompt()
    {
        _mem.AddFile("/src/d/new.txt", "n");
        _mem.AddFile("/src/d/sub/deep.txt", "deep");
        _mem.AddFile("/dst/d/old.txt", "o");
        _mem.AddDirectory("/dst/d/sub");

        var result = await Copy("/src/d").RunAsync();

        Assert.Equal(OperationState.Completed, result.State);
        Assert.Empty(_interaction.Conflicts);
        Assert.Equal("n", _mem.ReadAllText("/dst/d/new.txt"));
        Assert.Equal("deep", _mem.ReadAllText("/dst/d/sub/deep.txt"));
        Assert.Equal("o", _mem.ReadAllText("/dst/d/old.txt"));
    }

    [Fact]
    public async Task Type_mismatch_goes_to_error_prompt()
    {
        _mem.AddFile("/src/a.txt", "file");
        _mem.AddFile("/dst/a.txt/inside.txt", "dir content");
        _interaction.OnError(ErrorAction.Skip);

        var result = await Copy("/src/a.txt").RunAsync();

        Assert.Equal(OperationState.Completed, result.State);
        Assert.Empty(_interaction.Conflicts);
        Assert.Equal(Mem("/src/a.txt"), Assert.Single(_interaction.Errors).Item);
        var skipped = Assert.Single(result.Skipped);
        Assert.Equal(Mem("/src/a.txt"), skipped.Path);
        Assert.Equal(SkipReason.Error, skipped.Reason);
        Assert.False(string.IsNullOrWhiteSpace(skipped.Detail));
        Assert.Equal("dir content", _mem.ReadAllText("/dst/a.txt/inside.txt"));
    }

    [Fact]
    public async Task Directory_onto_file_is_an_error_and_skipping_it_skips_its_contents()
    {
        _mem.AddFile("/src/d/x.txt", "x");
        _mem.AddFile("/src/d/sub/y.txt", "y");
        _mem.AddFile("/src/after.txt", "after");
        _mem.AddFile("/dst/d", "a file");
        _interaction.OnError(ErrorAction.Skip);
        var op = Copy("/src/d", "/src/after.txt");

        var result = await op.RunAsync();

        Assert.Equal(OperationState.Completed, result.State);
        Assert.Empty(_interaction.Conflicts);
        Assert.Equal(Mem("/src/d"), Assert.Single(_interaction.Errors).Item);
        var skipped = Assert.Single(result.Skipped);
        Assert.Equal((Mem("/src/d"), SkipReason.Error), (skipped.Path, skipped.Reason));
        Assert.Equal("a file", _mem.ReadAllText("/dst/d"));
        Assert.Equal("after", _mem.ReadAllText("/dst/after.txt"));
        Assert.Equal(op.Progress.FilesTotal, op.Progress.FilesDone);
        Assert.Equal(op.Progress.BytesTotal, op.Progress.BytesDone);
    }
}
