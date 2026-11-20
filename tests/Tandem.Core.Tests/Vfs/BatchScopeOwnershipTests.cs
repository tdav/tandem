using Tandem.Core.Vfs;

namespace Tandem.Core.Tests.Vfs;

/// <summary><see cref="BatchScope.Holds"/>: whether the calling flow is the one that holds a provider's
/// batch.</summary>
public sealed class BatchScopeOwnershipTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly BatchJournal _journal = new();
    private readonly BatchingProvider _arc;
    private readonly BatchingProvider _other;
    private readonly ProviderRegistry _providers = new();

    public BatchScopeOwnershipTests()
    {
        _arc = new BatchingProvider("arc", _journal);
        _other = new BatchingProvider("other", _journal);
        _providers.Register(_arc);
        _providers.Register(_other);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_flow_that_began_the_scope_holds_its_batches_only()
    {
        Assert.False(BatchScope.Holds(_arc));

        await using (var scope = await BatchScope.BeginAsync(_providers, [new VfsPath("arc", "/")], Ct))
        {
            Assert.True(BatchScope.Holds(_arc)); // set before BeginAsync returned, still seen after the await
            Assert.False(BatchScope.Holds(_other));
            Assert.True(await Task.Run(() => BatchScope.Holds(_arc), Ct)); // work started by the flow is the flow
        }

        Assert.False(BatchScope.Holds(_arc));
    }

    [Fact]
    public async Task A_flow_that_was_not_started_by_the_scope_does_not_hold_it()
    {
        var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var before = Task.Run(async () =>
        {
            await go.Task;
            return BatchScope.Holds(_arc);
        }, Ct);
        Task<bool> separate;

        await using (await BatchScope.BeginAsync(_providers, [new VfsPath("arc", "/")], Ct))
        {
            using (ExecutionContext.SuppressFlow()) separate = Task.Run(() => BatchScope.Holds(_arc));
            go.SetResult();
            Assert.False(await before.WaitAsync(Timeout, Ct));
            Assert.False(await separate.WaitAsync(Timeout, Ct));
            Assert.True(BatchScope.Holds(_arc));
        }
    }

    [Fact]
    public async Task A_committed_batch_is_held_no_more()
    {
        await using var scope = await BatchScope.BeginAsync(_providers, [new VfsPath("arc", "/")], Ct);

        await scope.CommitAsync(Ct);

        Assert.False(BatchScope.Holds(_arc));
    }

    [Fact]
    public async Task A_failed_begin_holds_nothing()
    {
        _other.OnBegin = _ => throw new IOException("no");

        await Assert.ThrowsAsync<IOException>(() => BatchScope.BeginAsync(_providers,
            [new VfsPath("arc", "/"), new VfsPath("other", "/")], Ct));

        Assert.False(BatchScope.Holds(_arc));
        Assert.False(BatchScope.Holds(_other));
    }

    [Fact]
    public async Task A_joined_scope_answers_for_its_owner()
    {
        var host = new BatchingProvider("host", _journal);
        var inner = new BatchingProvider("inner", _journal);
        _providers.Register(host);
        _providers.Register(inner);
        inner.HostedIn(_providers, new VfsPath("host", "/container.bin"));
        host.Files.AddFile("/container.bin", "old");
        bool? hostHeld = null, innerHeld = null;
        inner.OnCommit = async ct =>
        {
            await using var joined = await BatchScope.BeginAsync(_providers, [new VfsPath("host", "/container.bin")],
                ct);
            hostHeld = BatchScope.Holds(host);
            innerHeld = BatchScope.Holds(inner); // committing already
        };

        await using (var scope = await BatchScope.BeginAsync(_providers, [new VfsPath("inner", "/")], Ct))
            await scope.CommitAsync(Ct);

        Assert.True(hostHeld);
        Assert.False(innerHeld);
    }
}
