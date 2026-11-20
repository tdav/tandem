using System.Runtime.ExceptionServices;

namespace Tandem.Core.Vfs;

/// <summary>
/// The batches one operation holds: one per <see cref="IBatchProvider"/> among the providers of its paths, begun in
/// <see cref="IFileSystemProvider.Id"/> order (ordinal), the same order in every operation, so that two operations do
/// not take each other's batches in opposite orders. Providers that take no batches are simply not covered; with none,
/// the scope is empty.
/// <para>Re-entrant and nested: while the scope calls into a provider's batch (begin, prepare-read, commit, dispose),
/// a <see cref="BeginAsync"/> made from that call — a hosted provider reaching its host — joins this scope instead of
/// beginning a second batch, which would wait on the first forever. Batches it needs that the scope does not hold yet
/// are begun into this scope. A joined scope commits and disposes nothing; the scope that owns the batches does.</para>
/// <para>Commit runs from the last begun batch to the first. A hosted provider begins its host's batch while
/// beginning its own, so its own begin completes later and it commits first, while its host's batch is still open to
/// take what it writes back; batches begun during a commit commit after it. Disposal runs in the same order.</para>
/// <para>A scope belongs to one operation and is used from its flow only; it is not thread-safe.</para>
/// </summary>
public sealed class BatchScope : IAsyncDisposable
{
    /// <summary>The owning scope whose call into a provider batch is running in this flow.</summary>
    private static readonly AsyncLocal<BatchScope?> Calling = new();

    private readonly BatchScope? _owner; // null: this scope owns its batches
    private readonly HashSet<string> _requested; // provider ids of this scope's own paths that take batches

    // Owner only.
    private readonly Dictionary<string, Held> _held = new(StringComparer.Ordinal);
    private readonly List<Held> _order = []; // by completed begin
    private readonly HashSet<string> _beginning = new(StringComparer.Ordinal);
    private bool _committed, _disposed;

    private BatchScope(BatchScope? owner, HashSet<string> requested)
    {
        _owner = owner;
        _requested = requested;
    }

    /// <summary>Begins one batch on every <see cref="IBatchProvider"/> among the providers of
    /// <paramref name="paths"/>, in id order (or joins the calling scope, see the class remarks). If a begin fails or
    /// is cancelled, the batches already begun by this call are disposed.</summary>
    /// <exception cref="KeyNotFoundException">A path's provider is not registered.</exception>
    public static async Task<BatchScope> BeginAsync(ProviderRegistry providers, IEnumerable<VfsPath> paths,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(paths);
        var batching = paths
            .Select(path => path.ProviderId)
            .Distinct(StringComparer.Ordinal)
            .Select(providers.Get)
            .OfType<IBatchProvider>()
            .Select(provider => (Id: ((IFileSystemProvider)provider).Id, Provider: provider))
            .OrderBy(p => p.Id, StringComparer.Ordinal)
            .ToList();

        var owner = Calling.Value;
        var scope = new BatchScope(owner, batching.Select(p => p.Id).ToHashSet(StringComparer.Ordinal));
        var holder = owner ?? scope;
        try
        {
            foreach (var (id, provider) in batching)
                await holder.HoldAsync(id, provider, ct).ConfigureAwait(false);
        }
        catch
        {
            if (owner is null) await scope.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        return scope;
    }

    /// <summary>True when the provider of <paramref name="path"/> takes part in the batch: its changes are applied
    /// only by <see cref="CommitAsync"/>.</summary>
    public bool Covers(VfsPath path) =>
        _owner is null ? _held.ContainsKey(path.ProviderId) : _requested.Contains(path.ProviderId);

    /// <summary>Passes the read hint to the batches it concerns: to each covered provider, its own files of
    /// <paramref name="files"/> in their order. Files of other providers are left out.</summary>
    public async Task PrepareReadAsync(IReadOnlyList<VfsPath> files, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(files);
        var owner = _owner ?? this;
        owner.ThrowIfDisposed();
        foreach (var group in files.Where(Covers).GroupBy(f => f.ProviderId, StringComparer.Ordinal)
                     .OrderBy(g => g.Key, StringComparer.Ordinal))
            await owner.CallAsync(() => owner._held[group.Key].Batch.PrepareReadAsync(group.ToList(), ct))
                .ConfigureAwait(false);
    }

    /// <summary>Commits every batch, the last begun first (see the class remarks), stopping at the first failure;
    /// what is left uncommitted is discarded on disposal. A joined scope commits nothing: its owner does.</summary>
    /// <exception cref="InvalidOperationException">The scope was committed already.</exception>
    public async Task CommitAsync(CancellationToken ct)
    {
        if (_owner is not null) return;
        ThrowIfDisposed();
        if (_committed) throw new InvalidOperationException("The batch scope was committed already.");
        _committed = true;
        // A commit may begin further batches (a hosted provider reaching a host not held yet): they commit next.
        while (_order.LastOrDefault(h => !h.Committed) is { } held)
        {
            held.Committed = true;
            await CallAsync(() => held.Batch.CommitAsync(ct)).ConfigureAwait(false);
        }
    }

    /// <summary>Disposes every batch, the last begun first; uncommitted changes are discarded. All batches are
    /// disposed even if one fails; the first failure is then rethrown. A joined scope disposes nothing.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_owner is not null || _disposed) return;
        _disposed = true;
        ExceptionDispatchInfo? failure = null;
        for (var i = _order.Count - 1; i >= 0; i--)
        {
            try
            {
                var batch = _order[i].Batch;
                await CallAsync(async () => await batch.DisposeAsync().ConfigureAwait(false)).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                failure ??= ExceptionDispatchInfo.Capture(e);
            }
        }
        failure?.Throw();
    }

    /// <summary>Makes sure this (owning) scope holds a batch of <paramref name="provider"/>, beginning one if it
    /// does not.</summary>
    private async Task HoldAsync(string id, IBatchProvider provider, CancellationToken ct)
    {
        ThrowIfDisposed();
        if (_held.TryGetValue(id, out var existing))
        {
            if (existing.Committed)
                throw new InvalidOperationException(
                    $"The batch of '{id}' was committed already; a hosted provider must begin its host's batch while " +
                    "beginning its own.");
            return;
        }
        if (!_beginning.Add(id))
            throw new InvalidOperationException($"The batch of '{id}' is needed while it is being begun.");
        try
        {
            var batch = await CallAsync(() => provider.BeginBatchAsync(ct)).ConfigureAwait(false);
            var held = new Held(batch);
            _held[id] = held;
            _order.Add(held);
        }
        finally
        {
            _beginning.Remove(id);
        }
    }

    /// <summary>Runs a call into a provider batch with this scope as the calling scope of the flow, so that a
    /// <see cref="BeginAsync"/> made from inside the call joins it. The setting ends with the call.</summary>
    private async Task<T> CallAsync<T>(Func<Task<T>> call)
    {
        Calling.Value = this; // an async method restores the caller's value when it returns
        return await call().ConfigureAwait(false);
    }

    private Task CallAsync(Func<Task> call) => CallAsync<object?>(async () =>
    {
        await call().ConfigureAwait(false);
        return null;
    });

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed class Held(IProviderBatch batch)
    {
        public IProviderBatch Batch { get; } = batch;

        public bool Committed { get; set; }
    }
}
