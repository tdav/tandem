using System.Runtime.Versioning;
using Tandem.Core.Operations;
using Tandem.Core.Tests.Vfs;
using Tandem.Core.Vfs;
using Tandem.Core.Vfs.InMemory;

namespace Tandem.Core.Tests.Operations;

[SupportedOSPlatform("macos")]
public sealed class BatchOperationTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly InMemoryFileSystem _mem = new();
    private readonly BatchJournal _journal = new();
    private readonly BatchingProvider _arc;
    private readonly ProviderRegistry _providers = new();
    private readonly ScriptedInteraction _interaction = new();

    public BatchOperationTests()
    {
        _arc = new BatchingProvider("arc", _journal);
        _providers.Register(_mem);
        _providers.Register(_arc);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static VfsPath Mem(string path) => new("mem", path);

    private static VfsPath Arc(string path) => new("arc", path);

    private CopyOperation Copy(VfsPath destination, params VfsPath[] sources) =>
        new(_providers, sources, destination, new CopyOptions(), _interaction);

    private MoveOperation Move(VfsPath destination, params VfsPath[] sources) =>
        new(_providers, sources, destination, new CopyOptions(), _interaction);

    private void Arrange()
    {
        _mem.AddFile("/src/a.txt", "a");
        _mem.AddFile("/src/dir/b.txt", "b");
        _arc.Files.AddDirectory("/dst");
    }

    // ---- Copy ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Copy_into_batching_provider_commits_once_after_all_items()
    {
        Arrange();

        var result = await Copy(Arc("/dst"), Mem("/src/a.txt"), Mem("/src/dir")).RunAsync().WaitAsync(Timeout, Ct);

        Assert.Equal(OperationState.Completed, result.State);
        Assert.Equal("b", _arc.Files.ReadAllText("/dst/dir/b.txt"));
        Assert.Equal(1, _journal.Count("arc:Begin"));
        Assert.Equal(1, _journal.Count("arc:Commit"));
        Assert.Equal(2, _journal.Count("arc:Write"));
        Assert.True(_journal.IndexOf("arc:Commit") > _journal.LastIndexOf("arc:Write"));
        Assert.Equal("arc:Dispose", _journal.Events[^1]);
    }

    [Fact]
    public async Task Copy_announces_files_to_read_in_plan_order()
    {
        _arc.Files.AddFile("/src/b.txt", "b");
        _arc.Files.AddFile("/src/dir/c.txt", "c");
        _arc.Files.AddFile("/src/a.txt", "a");
        _mem.AddDirectory("/dst");

        var result = await Copy(Mem("/dst"), Arc("/src/b.txt"), Arc("/src/dir"), Arc("/src/a.txt")).RunAsync()
            .WaitAsync(Timeout, Ct);

        Assert.Equal(OperationState.Completed, result.State);
        Assert.Equal(["arc:Begin", "arc:PrepareRead /src/b.txt,/src/dir/c.txt,/src/a.txt", "arc:Commit", "arc:Dispose"],
            _journal.Events);
    }

    [Fact]
    public async Task Cancelled_copy_still_commits_completed_items()
    {
        Arrange();
        _mem.AddFile("/src/c.txt", "c");
        var op = Copy(Arc("/dst"), Mem("/src/a.txt"), Mem("/src/c.txt"));
        _arc.Files.FailOn((name, path) =>
        {
            if (name == "OpenWrite" && path.Path.Contains("c.txt", StringComparison.Ordinal)) op.Cancel();
            return false;
        }, () => new IOException("unused"));

        var result = await op.RunAsync().WaitAsync(Timeout, Ct);

        Assert.Equal(OperationState.Cancelled, result.State);
        Assert.Equal("a", _arc.Files.ReadAllText("/dst/a.txt"));
        Assert.False(_arc.Files.Exists("/dst/c.txt"));
        Assert.Equal(1, _journal.Count("arc:Commit"));
        Assert.True(_journal.IndexOf("arc:Commit") > _journal.LastIndexOf("arc:Write"));
    }

    [Fact]
    public async Task Failed_commit_fails_a_copy_that_otherwise_succeeded()
    {
        Arrange();
        _arc.FailCommit = () => new IOException("commit failed");
        var op = Copy(Arc("/dst"), Mem("/src/a.txt"));

        var error = await Assert.ThrowsAsync<IOException>(() => op.RunAsync().WaitAsync(Timeout, Ct));

        Assert.Equal("commit failed", error.Message);
        Assert.Equal(OperationState.Failed, op.State);
        Assert.Equal("arc:Dispose", _journal.Events[^1]);
    }

    [Fact]
    public async Task Failed_copy_still_commits_and_keeps_its_own_error()
    {
        Arrange();
        _mem.AddFile("/src/c.txt", "c");
        _mem.FailOn((name, path) => name == "OpenRead" && path.Path == "/src/c.txt",
            () => new VfsAccessDeniedException("denied"));
        _interaction.OnError(ErrorAction.Abort);
        var op = Copy(Arc("/dst"), Mem("/src/a.txt"), Mem("/src/c.txt"));

        var error = await Assert.ThrowsAsync<VfsAccessDeniedException>(() => op.RunAsync().WaitAsync(Timeout, Ct));

        Assert.Equal("denied", error.Message);
        Assert.Equal("a", _arc.Files.ReadAllText("/dst/a.txt"));
        Assert.Equal(1, _journal.Count("arc:Commit"));
    }

    [Fact]
    public async Task Failed_copy_keeps_its_own_error_when_the_commit_fails_too()
    {
        Arrange();
        _mem.FailOn((name, path) => name == "OpenRead" && path.Path == "/src/a.txt",
            () => new VfsAccessDeniedException("denied"));
        _interaction.OnError(ErrorAction.Abort);
        _arc.FailCommit = () => new IOException("commit failed");
        var op = Copy(Arc("/dst"), Mem("/src/a.txt"));

        var error = await Assert.ThrowsAsync<VfsAccessDeniedException>(() => op.RunAsync().WaitAsync(Timeout, Ct));

        Assert.Equal("denied", error.Message);
        Assert.Equal("arc:Dispose", _journal.Events[^1]);
    }

    [Fact]
    public async Task Copy_between_plain_providers_needs_no_batch()
    {
        _mem.AddFile("/src/a.txt", "a");
        _mem.AddDirectory("/dst");

        var result = await Copy(Mem("/dst"), Mem("/src/a.txt")).RunAsync().WaitAsync(Timeout, Ct);

        Assert.Equal(OperationState.Completed, result.State);
        Assert.Equal("a", _mem.ReadAllText("/dst/a.txt"));
        Assert.Empty(_journal.Events);
    }

    // ---- Move ----------------------------------------------------------------------------------------

    /// <summary>Records every delete on the plain source provider in the shared journal.</summary>
    private void JournalSourceDeletes() =>
        _mem.FailOn((name, path) =>
        {
            if (name == "Delete") _journal.Add($"mem:Delete {path.Path}");
            return false;
        }, () => new IOException("unused"));

    [Fact]
    public async Task Move_into_batching_provider_deletes_sources_only_after_commit()
    {
        Arrange();
        JournalSourceDeletes();

        var result = await Move(Arc("/dst"), Mem("/src/a.txt"), Mem("/src/dir")).RunAsync().WaitAsync(Timeout, Ct);

        Assert.Equal(OperationState.Completed, result.State);
        Assert.Equal(["/src"], _mem.AllPaths);
        Assert.Equal("a", _arc.Files.ReadAllText("/dst/a.txt"));
        Assert.Equal("b", _arc.Files.ReadAllText("/dst/dir/b.txt"));
        Assert.Equal(["mem:Delete /src/a.txt", "mem:Delete /src/dir/b.txt", "mem:Delete /src/dir"],
            _journal.Events.Where(e => e.StartsWith("mem:", StringComparison.Ordinal)));
        Assert.Equal(1, _journal.Count("arc:Commit"));
        Assert.True(_journal.IndexOf("mem:Delete") > _journal.IndexOf("arc:Commit"));
    }

    [Fact]
    public async Task Move_keeps_every_source_when_commit_fails()
    {
        Arrange();
        JournalSourceDeletes();
        _arc.FailCommit = () => new IOException("commit failed");
        var op = Move(Arc("/dst"), Mem("/src/a.txt"), Mem("/src/dir"));

        var error = await Assert.ThrowsAsync<IOException>(() => op.RunAsync().WaitAsync(Timeout, Ct));

        Assert.Equal("commit failed", error.Message);
        Assert.Equal(OperationState.Failed, op.State);
        Assert.Equal(["/src", "/src/a.txt", "/src/dir", "/src/dir/b.txt"], _mem.AllPaths);
        Assert.Equal(-1, _journal.IndexOf("mem:Delete"));
    }

    [Fact]
    public async Task Cancelled_move_into_batching_provider_deletes_only_the_committed_sources()
    {
        Arrange();
        _mem.AddFile("/src/c.txt", "c");
        JournalSourceDeletes();
        var op = Move(Arc("/dst"), Mem("/src/a.txt"), Mem("/src/c.txt"));
        _arc.Files.FailOn((name, path) =>
        {
            if (name == "OpenWrite" && path.Path.Contains("c.txt", StringComparison.Ordinal)) op.Cancel();
            return false;
        }, () => new IOException("unused"));

        var result = await op.RunAsync().WaitAsync(Timeout, Ct);

        Assert.Equal(OperationState.Cancelled, result.State);
        Assert.False(_mem.Exists("/src/a.txt"));
        Assert.Equal("c", _mem.ReadAllText("/src/c.txt"));
        Assert.Equal("a", _arc.Files.ReadAllText("/dst/a.txt"));
        Assert.Equal(1, _journal.Count("arc:Commit"));
        Assert.True(_journal.IndexOf("mem:Delete /src/a.txt") > _journal.IndexOf("arc:Commit"));
    }

    [Fact]
    public async Task Cancelled_move_into_batching_provider_keeps_its_sources_when_dispose_fails()
    {
        Arrange();
        _mem.AddFile("/src/c.txt", "c");
        JournalSourceDeletes();
        var op = Move(Arc("/dst"), Mem("/src/a.txt"), Mem("/src/c.txt"));
        _arc.Files.FailOn((name, path) =>
        {
            if (name == "OpenWrite" && path.Path.Contains("c.txt", StringComparison.Ordinal)) op.Cancel();
            return false;
        }, () => new IOException("unused"));
        _arc.FailDispose = () => new IOException("dispose failed");

        var result = await op.RunAsync().WaitAsync(Timeout, Ct);

        Assert.Equal(OperationState.Cancelled, result.State);
        Assert.Equal("a", _mem.ReadAllText("/src/a.txt"));
        Assert.Equal("c", _mem.ReadAllText("/src/c.txt"));
        Assert.Equal(1, _journal.Count("arc:Commit"));
        Assert.Equal(-1, _journal.IndexOf("mem:Delete"));
    }

    [Fact]
    public async Task Cancelled_move_deletes_committed_sources_when_a_nested_scope_marked_the_cancellation()
    {
        // A cancellation that a failed BeginAsync elsewhere already marked with its own dispose error: this
        // operation's scope disposed fine, so the committed source is still deleted.
        var marked = await MarkedCancellationAsync();
        Arrange();
        _mem.AddFile("/src/c.txt", "c");
        JournalSourceDeletes();
        var op = Move(Arc("/dst"), Mem("/src/a.txt"), Mem("/src/c.txt"));
        _arc.Files.FailOn((name, path) =>
        {
            if (name != "OpenWrite" || !path.Path.Contains("c.txt", StringComparison.Ordinal)) return false;
            op.Cancel();
            return true;
        }, () => marked);

        var result = await op.RunAsync().WaitAsync(Timeout, Ct);

        Assert.Equal(OperationState.Cancelled, result.State);
        Assert.False(_mem.Exists("/src/a.txt"));
        Assert.Equal("c", _mem.ReadAllText("/src/c.txt"));
        Assert.Equal(1, _journal.Count("arc:Commit"));
    }

    /// <summary>The cancellation thrown by a <see cref="BatchScope.BeginAsync"/> whose second begin is cancelled
    /// and whose first batch then fails to dispose: it carries <see cref="BatchScope.DisposeErrorKey"/>.</summary>
    private static async Task<OperationCanceledException> MarkedCancellationAsync()
    {
        var providers = new ProviderRegistry();
        var journal = new BatchJournal();
        var first = new BatchingProvider("p1", journal) { FailDispose = () => new IOException("dispose failed") };
        var second = new BatchingProvider("p2", journal) { OnBegin = _ => throw new OperationCanceledException() };
        providers.Register(first);
        providers.Register(second);
        var error = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            BatchScope.BeginAsync(providers, [new VfsPath("p1", "/"), new VfsPath("p2", "/")], Ct));
        Assert.True(error.Data.Contains(BatchScope.DisposeErrorKey));
        return error;
    }

    [Fact]
    public async Task Move_out_of_batching_provider_deletes_its_sources_in_the_batch()
    {
        _arc.Files.AddFile("/src/a.txt", "a");
        _mem.AddDirectory("/dst");

        var result = await Move(Mem("/dst"), Arc("/src/a.txt")).RunAsync().WaitAsync(Timeout, Ct);

        Assert.Equal(OperationState.Completed, result.State);
        Assert.Equal("a", _mem.ReadAllText("/dst/a.txt"));
        Assert.False(_arc.Files.Exists("/src/a.txt"));
        Assert.Equal(["arc:Begin", "arc:PrepareRead /src/a.txt", "arc:Delete /src/a.txt", "arc:Commit", "arc:Dispose"],
            _journal.Events);
    }

    // ---- Delete --------------------------------------------------------------------------------------

    [Fact]
    public async Task Delete_in_batching_provider_commits_once()
    {
        _arc.Files.AddFile("/a.txt", "a");
        _arc.Files.AddFile("/b.txt", "b");
        var op = new DeleteOperation(_providers, [Arc("/a.txt"), Arc("/b.txt")], toTrash: false, trash: null,
            _interaction);

        var result = await op.RunAsync().WaitAsync(Timeout, Ct);

        Assert.Equal(OperationState.Completed, result.State);
        Assert.Equal(["arc:Begin", "arc:Delete /a.txt", "arc:Delete /b.txt", "arc:Commit", "arc:Dispose"],
            _journal.Events);
    }

    // ---- Several batches -----------------------------------------------------------------------------

    [Fact]
    public async Task Two_batching_providers_begin_in_id_order()
    {
        var zeta = new BatchingProvider("zeta", _journal);
        var alpha = new BatchingProvider("alpha", _journal);
        _providers.Register(zeta);
        _providers.Register(alpha);
        zeta.Files.AddFile("/a.txt", "a");
        alpha.Files.AddDirectory("/dst");

        var result = await Copy(new VfsPath("alpha", "/dst"), new VfsPath("zeta", "/a.txt")).RunAsync()
            .WaitAsync(Timeout, Ct);

        Assert.Equal(OperationState.Completed, result.State);
        Assert.Equal(["alpha:Begin", "zeta:Begin"],
            _journal.Events.Where(e => e.EndsWith(":Begin", StringComparison.Ordinal)));
        Assert.Equal(1, _journal.Count("alpha:Commit"));
        Assert.Equal(1, _journal.Count("zeta:Commit"));
    }

    [Fact]
    public async Task Second_batch_waits_for_the_first()
    {
        Arrange();
        var commit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _arc.CommitGate = commit.Task;
        var first = Copy(Arc("/dst"), Mem("/src/a.txt")).RunAsync();
        await WaitUntilAsync(() => _journal.Count("arc:Write") == 1);
        var second = Copy(Arc("/dst"), Mem("/src/dir")).RunAsync();
        await Task.Delay(200, Ct);

        Assert.Equal(1, _journal.Count("arc:Begin")); // the second waits for the first batch
        Assert.Equal(1, _journal.Count("arc:Write"));
        commit.SetResult();
        await Task.WhenAll(first, second).WaitAsync(Timeout, Ct);

        Assert.True(_journal.LastIndexOf("arc:Begin") > _journal.IndexOf("arc:Dispose"));
        Assert.Equal(2, _journal.Count("arc:Commit"));
        Assert.Equal("b", _arc.Files.ReadAllText("/dst/dir/b.txt"));
    }

    [Fact]
    public async Task Cancel_while_waiting_for_a_batch_releases_the_batches_already_begun()
    {
        var alpha = new BatchingProvider("alpha", _journal);
        _providers.Register(alpha);
        alpha.Files.AddFile("/a.txt", "a");
        _arc.Files.AddDirectory("/dst");
        var held = await _arc.BeginBatchAsync(Ct); // another operation's batch
        var op = Copy(Arc("/dst"), new VfsPath("alpha", "/a.txt"));
        var run = op.RunAsync();
        await WaitUntilAsync(() => _journal.Count("alpha:Begin") == 1);

        op.Cancel();
        var result = await run.WaitAsync(Timeout, Ct);

        Assert.Equal(OperationState.Cancelled, result.State);
        Assert.Equal(["arc:Begin", "alpha:Begin", "alpha:Dispose"], _journal.Events);
        await held.DisposeAsync();
    }

    // ---- Nested providers (a provider that lives in a file of another) -------------------------------

    /// <summary>"inner" lives in /container.bin of "host"; the operation copies from the host into the inner
    /// provider, so it holds batches on both.</summary>
    private BatchingProvider Nested(string hostId, string innerId, out BatchingProvider inner)
    {
        var host = new BatchingProvider(hostId, _journal);
        inner = new BatchingProvider(innerId, _journal);
        _providers.Register(host);
        _providers.Register(inner);
        inner.HostedIn(_providers, new VfsPath(hostId, "/container.bin"));
        host.Files.AddFile("/container.bin", "old content");
        host.Files.AddFile("/src/a.txt", "a");
        inner.Files.AddDirectory("/dst");
        return host;
    }

    [Theory]
    [InlineData("host", "inner")]
    [InlineData("outer", "host")] // the hosted provider begins first, by id
    public async Task Nested_provider_reuses_the_operation_batch_of_its_host(string hostId, string innerId)
    {
        var host = Nested(hostId, innerId, out var inner);

        var result = await Copy(new VfsPath(innerId, "/dst"), new VfsPath(hostId, "/src/a.txt")).RunAsync()
            .WaitAsync(Timeout, Ct);

        Assert.Equal(OperationState.Completed, result.State);
        Assert.Equal("a", inner.Files.ReadAllText("/dst/a.txt"));
        Assert.Equal(1, _journal.Count($"{hostId}:Begin")); // no second batch, which would wait on the first
        Assert.Equal(1, _journal.Count($"{hostId}:Commit"));
        Assert.Equal(1, _journal.Count($"{hostId}:Dispose"));
        Assert.Equal(3, host.Files.ReadAllText("/container.bin").Length); // written back by the inner commit
    }

    [Theory]
    [InlineData("host", "inner")]
    [InlineData("outer", "host")]
    public async Task Nested_provider_commits_before_its_host(string hostId, string innerId)
    {
        Nested(hostId, innerId, out _);

        var result = await Copy(new VfsPath(innerId, "/dst"), new VfsPath(hostId, "/src/a.txt")).RunAsync()
            .WaitAsync(Timeout, Ct);

        Assert.Equal(OperationState.Completed, result.State);
        var innerCommit = _journal.IndexOf($"{innerId}:Commit");
        var containerWrite = _journal.IndexOf($"{hostId}:Write /container.bin");
        var hostCommit = _journal.IndexOf($"{hostId}:Commit");
        Assert.True(containerWrite >= 0 && containerWrite < innerCommit && innerCommit < hostCommit,
            string.Join(" | ", _journal.Events));
        Assert.True(_journal.IndexOf($"{hostId}:Begin") < _journal.IndexOf($"{innerId}:Begin"));
        Assert.True(_journal.IndexOf($"{innerId}:Dispose") < _journal.IndexOf($"{hostId}:Dispose"));
    }

    // ---- Lock order: hosts before what they host, the same in every scope ---------------------------

    private BatchingProvider Batching(string id, string? hostedIn = null)
    {
        var provider = new BatchingProvider(id, _journal);
        _providers.Register(provider);
        if (hostedIn is not null)
        {
            ((BatchingProvider)_providers.Get(hostedIn)).Files.AddFile($"/{id}.bin", "old content");
            provider.HostedIn(_providers, new VfsPath(hostedIn, $"/{id}.bin"));
        }
        // Each begin holds its gate a while, so that two scopes taking batches in opposite orders would each hold
        // the batch the other one waits for.
        provider.OnBegin = ct => Task.Delay(50, ct);
        return provider;
    }

    private async Task RunScopeAsync(params string[] providerIds)
    {
        await using var scope = await BatchScope.BeginAsync(_providers, providerIds.Select(id => new VfsPath(id, "/")),
            Ct);
        await scope.CommitAsync(Ct);
    }

    [Theory]
    [InlineData("a-host", "b-inner")]
    [InlineData("b-host", "a-inner")]
    public async Task Concurrent_scopes_on_a_hosted_provider_and_on_it_with_its_host_both_complete(string hostId,
        string innerId)
    {
        Batching(hostId);
        Batching(innerId, hostedIn: hostId);

        await Task.WhenAll(RunScopeAsync(innerId), RunScopeAsync(hostId, innerId)).WaitAsync(Timeout, Ct);

        Assert.Equal(2, _journal.Count($"{innerId}:Commit"));
        Assert.Equal(2, _journal.Count($"{hostId}:Commit"));
    }

    [Fact]
    public async Task Concurrent_scopes_on_providers_in_two_hosts_both_complete()
    {
        // i1 lives in h1, i2 in h2, and h2 < i1 < h1 < i2 by id.
        Batching("h1-c");
        Batching("h2-a");
        Batching("i1-b", hostedIn: "h1-c");
        Batching("i2-d", hostedIn: "h2-a");

        await Task.WhenAll(RunScopeAsync("i1-b", "i2-d"), RunScopeAsync("h2-a", "i1-b")).WaitAsync(Timeout, Ct);

        Assert.Equal(2, _journal.Count("i1-b:Commit"));
        Assert.Equal(2, _journal.Count("h2-a:Commit"));
    }

    [Fact]
    public async Task Scope_on_a_hosted_provider_begins_its_host_first_and_commits_it_last()
    {
        Batching("z-host");
        Batching("m-inner", hostedIn: "z-host");
        Batching("a-leaf", hostedIn: "m-inner");

        await RunScopeAsync("a-leaf");

        Assert.Equal(["z-host:Begin", "m-inner:Begin", "a-leaf:Begin", "a-leaf:Commit", "m-inner:Commit",
            "z-host:Commit", "a-leaf:Dispose", "m-inner:Dispose", "z-host:Dispose"],
            _journal.Events.Where(e => !e.Contains(":Write ", StringComparison.Ordinal)));
    }

    // ---- Guards --------------------------------------------------------------------------------------

    [Fact]
    public async Task Commit_reaching_a_batch_already_committed_is_refused()
    {
        var host = Batching("host");
        Batching("inner", hostedIn: "host");
        host.OnCommit = async ct =>
        {
            await using var _ = await BatchScope.BeginAsync(_providers, [new VfsPath("inner", "/")], ct);
        };
        await using var scope = await BatchScope.BeginAsync(_providers, [new VfsPath("inner", "/")], Ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.CommitAsync(Ct));
    }

    [Fact]
    public async Task Joining_for_a_batch_the_scope_does_not_hold_is_refused()
    {
        Batching("alpha");
        _arc.OnCommit = async ct =>
        {
            await using var _ = await BatchScope.BeginAsync(_providers, [new VfsPath("alpha", "/")], ct);
        };
        await using var scope = await BatchScope.BeginAsync(_providers, [Arc("/")], Ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.CommitAsync(Ct));
        Assert.Equal(-1, _journal.IndexOf("alpha:Begin"));
    }

    [Fact]
    public async Task Hosts_in_a_cycle_are_refused_before_any_batch_begins()
    {
        var a = Batching("a");
        var b = Batching("b");
        a.BatchHost = b;
        b.BatchHost = a;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => BatchScope.BeginAsync(_providers, [new VfsPath("a", "/")], Ct));

        Assert.Empty(_journal.Events);
    }

    [Fact]
    public async Task Batch_needed_while_it_is_being_begun_is_refused()
    {
        _arc.OnBegin = async ct =>
        {
            await using var _ = await BatchScope.BeginAsync(_providers, [Arc("/")], ct);
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => BatchScope.BeginAsync(_providers, [Arc("/")], Ct));

        _arc.OnBegin = null;
        await RunScopeAsync("arc").WaitAsync(Timeout, Ct); // the gate was released
    }

    [Fact]
    public async Task Second_commit_is_refused()
    {
        await using var scope = await BatchScope.BeginAsync(_providers, [Arc("/")], Ct);
        await scope.CommitAsync(Ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.CommitAsync(Ct));
        Assert.Equal(1, _journal.Count("arc:Commit"));
    }

    [Fact]
    public async Task Failed_commit_stays_the_cause_when_dispose_fails_too()
    {
        Arrange();
        _arc.FailCommit = () => new IOException("commit failed");
        _arc.FailDispose = () => new IOException("dispose failed");
        var op = Copy(Arc("/dst"), Mem("/src/a.txt"));

        var error = await Assert.ThrowsAsync<IOException>(() => op.RunAsync().WaitAsync(Timeout, Ct));

        Assert.Equal("commit failed", error.Message);
        Assert.Equal("dispose failed",
            Assert.IsType<IOException>(error.Data[BatchScope.DisposeErrorKey]).Message);
    }

    [Fact]
    public async Task Failed_copy_stays_the_cause_when_dispose_fails()
    {
        Arrange();
        _mem.FailOn((name, path) => name == "OpenRead" && path.Path == "/src/a.txt",
            () => new VfsAccessDeniedException("denied"));
        _interaction.OnError(ErrorAction.Abort);
        _arc.FailDispose = () => new IOException("dispose failed");
        var op = Copy(Arc("/dst"), Mem("/src/a.txt"));

        var error = await Assert.ThrowsAsync<VfsAccessDeniedException>(() => op.RunAsync().WaitAsync(Timeout, Ct));

        Assert.Equal("denied", error.Message);
        Assert.IsType<IOException>(error.Data[BatchScope.DisposeErrorKey]);
    }

    [Fact]
    public async Task Failed_dispose_fails_a_copy_that_otherwise_succeeded()
    {
        Arrange();
        _arc.FailDispose = () => new IOException("dispose failed");
        var op = Copy(Arc("/dst"), Mem("/src/a.txt"));

        var error = await Assert.ThrowsAsync<IOException>(() => op.RunAsync().WaitAsync(Timeout, Ct));

        Assert.Equal("dispose failed", error.Message);
        Assert.Equal(1, _journal.Count("arc:Commit"));
    }

    [Fact]
    public async Task Nested_provider_alone_in_an_operation_brings_its_host_into_the_batch()
    {
        _mem.AddFile("/src/a.txt", "a");
        Nested("host", "inner", out var inner);

        var result = await Copy(new VfsPath("inner", "/dst"), Mem("/src/a.txt")).RunAsync().WaitAsync(Timeout, Ct);

        Assert.Equal(OperationState.Completed, result.State);
        Assert.Equal("a", inner.Files.ReadAllText("/dst/a.txt"));
        Assert.Equal(["host:Begin", "inner:Begin", "host:Write /container.bin", "inner:Commit", "host:Commit",
            "inner:Dispose", "host:Dispose"],
            _journal.Events.Where(e => !e.StartsWith("inner:Write", StringComparison.Ordinal)));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The condition was not met.");
            await Task.Delay(10, Ct);
        }
    }
}
