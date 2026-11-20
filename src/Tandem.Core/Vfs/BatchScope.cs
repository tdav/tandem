using System.Runtime.ExceptionServices;

namespace Tandem.Core.Vfs;

/// <summary>
/// The batches one operation holds: one per <see cref="IBatchProvider"/> among the providers of its paths, plus one
/// per provider on their <see cref="IBatchProvider.BatchHost"/> chains (a hosted provider's changes end up in its
/// host, so the host is batched too). Providers that take no batches are simply not covered; with none, the scope is
/// empty.
/// <para><b>Lock order.</b> Batches are begun in one total order, the same in every scope: by the sequence of ids from
/// the root host down to the provider, compared id by id (ordinal), a shorter sequence first. So every host is begun
/// before what it hosts, and two scopes never take two batches in opposite orders, which could leave each waiting for
/// the other's. Commit and disposal run in exactly the reverse order: what is hosted commits before its host, while
/// the host's batch is still open to take what it writes back.</para>
/// <para><b>Re-entrant.</b> While the scope calls into a provider's batch (begin, prepare-read, commit, dispose), a
/// <see cref="BeginAsync"/> made from that call — a hosted provider writing back into its host — joins this scope:
/// it reuses the batches this scope holds instead of beginning second ones, which would wait on the first forever.
/// A joined scope commits and disposes nothing; the owning scope does. A joined scope may only ask for batches the
/// owning scope holds and has not committed yet; anything else is refused with
/// <see cref="InvalidOperationException"/>, since beginning it then would break the lock order.</para>
/// <para>A scope belongs to one operation and is used from its flow only; it is not thread-safe.</para>
/// </summary>
public sealed class BatchScope : IAsyncDisposable
{
    /// <summary>The key in <see cref="Exception.Data"/> under which an error of disposing the batches is attached to
    /// the error that was already the outcome (a failed begin, commit or operation).</summary>
    public const string DisposeErrorKey = "Tandem.BatchDisposeError";

    /// <summary>The owning scope whose call into a provider batch is running in this flow.</summary>
    private static readonly AsyncLocal<BatchScope?> Calling = new();

    /// <summary>The scope most recently begun in this flow (see <see cref="Holds"/>).</summary>
    private static readonly AsyncLocal<Claim?> Active = new();

    private readonly BatchScope? _owner; // null: this scope owns its batches
    private readonly HashSet<string> _requested; // provider ids of this scope's own paths that take batches

    // Owner only.
    private readonly Dictionary<string, Held> _held = new(StringComparer.Ordinal);
    private readonly List<Held> _order = []; // in lock order
    private readonly HashSet<string> _beginning = new(StringComparer.Ordinal);
    private bool _committed, _disposed;

    private BatchScope(BatchScope? owner, HashSet<string> requested)
    {
        _owner = owner;
        _requested = requested;
    }

    /// <summary>Begins a batch on every <see cref="IBatchProvider"/> among the providers of <paramref name="paths"/>
    /// and on their hosts, in lock order (or joins the calling scope; see the class remarks). If a begin fails or is
    /// cancelled, the batches already begun by this call are disposed.</summary>
    /// <exception cref="KeyNotFoundException">A path's provider is not registered.</exception>
    /// <exception cref="InvalidOperationException">The hosts form a cycle, or a provider is not an
    /// <see cref="IFileSystemProvider"/>; or, joining, a batch is needed that the calling scope does not hold, has
    /// committed or is beginning.</exception>
    public static Task<BatchScope> BeginAsync(ProviderRegistry providers, IEnumerable<VfsPath> paths,
        CancellationToken ct)
    {
        // Set here, not in an async method, so that it stays in the caller's flow after this returns.
        var claim = new Claim();
        Active.Value = claim;
        return BeginCoreAsync(providers, paths, claim, ct);
    }

    private static async Task<BatchScope> BeginCoreAsync(ProviderRegistry providers, IEnumerable<VfsPath> paths,
        Claim claim, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(paths);
        var requested = paths
            .Select(path => path.ProviderId)
            .Distinct(StringComparer.Ordinal)
            .Select(providers.Get)
            .OfType<IBatchProvider>()
            .ToList();
        var plan = LockOrder(requested);

        var owner = Calling.Value;
        var scope = new BatchScope(owner, requested.Select(IdOf).ToHashSet(StringComparer.Ordinal));
        if (owner is not null)
        {
            foreach (var (id, _) in plan) owner.Reuse(id);
            claim.Scope = scope;
            return scope;
        }
        try
        {
            foreach (var (id, provider) in plan)
                await scope.BeginOneAsync(id, provider, ct).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            await scope.DisposeKeepingAsync(e).ConfigureAwait(false);
            throw;
        }
        claim.Scope = scope;
        return scope;
    }

    /// <summary>True when the calling flow holds the batch of <paramref name="provider"/>: a scope begun in this
    /// flow (by this async method or one that called it), or the scope it joined, holds that batch and has neither
    /// committed it nor been disposed. Work the flow starts (a task, an awaited call) is the same flow; a flow that
    /// was running before, or that does not carry the execution context, is not. A provider tells by this whether a
    /// change comes from the operation its batch belongs to.</summary>
    public static bool Holds(IBatchProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (Active.Value?.Scope is not { } scope || provider is not IFileSystemProvider { Id: var id }) return false;
        var owner = scope._owner ?? scope;
        return !owner._disposed && owner._held.TryGetValue(id, out var held) && !held.Committed;
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

    /// <summary>Commits every batch in the reverse of the lock order (hosted before host), stopping at the first
    /// failure; what is left uncommitted is discarded on disposal. A joined scope commits nothing: its owner
    /// does.</summary>
    /// <exception cref="InvalidOperationException">The scope was committed already.</exception>
    public async Task CommitAsync(CancellationToken ct)
    {
        if (_owner is not null) return;
        ThrowIfDisposed();
        if (_committed) throw new InvalidOperationException("The batch scope was committed already.");
        _committed = true;
        for (var i = _order.Count - 1; i >= 0; i--)
        {
            var held = _order[i];
            held.Committed = true; // a commit reaching back into its own batch is refused
            await CallAsync(() => held.Batch.CommitAsync(ct)).ConfigureAwait(false);
        }
    }

    /// <summary>Disposes every batch in the reverse of the lock order; uncommitted changes are discarded. All batches
    /// are disposed even if one fails; the first failure is then rethrown. A joined scope disposes nothing.</summary>
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

    /// <summary>Disposes the scope after <paramref name="outcome"/> has already gone wrong: an error of the disposal
    /// does not replace it, but is attached to it under <see cref="DisposeErrorKey"/>. Returns whether this disposal
    /// failed (the outcome may carry a key set by another scope).</summary>
    internal async ValueTask<bool> DisposeKeepingAsync(Exception outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        try
        {
            await DisposeAsync().ConfigureAwait(false);
            return false;
        }
        catch (Exception e)
        {
            outcome.Data[DisposeErrorKey] = e;
            return true;
        }
    }

    /// <summary>The providers to batch — the requested ones and their hosts — in lock order: by the ids from the root
    /// host down to the provider, id by id, a prefix (a host) first.</summary>
    private static List<(string Id, IBatchProvider Provider)> LockOrder(IEnumerable<IBatchProvider> requested)
    {
        var chains = new Dictionary<string, (IBatchProvider Provider, string[] Chain)>(StringComparer.Ordinal);
        foreach (var provider in requested)
        {
            var line = new List<IBatchProvider>(); // the provider, then its host, its host's host...
            for (var p = provider; p is not null; p = p.BatchHost)
            {
                if (line.Contains(p))
                    throw new InvalidOperationException($"The batch hosts of '{IdOf(provider)}' form a cycle.");
                line.Add(p);
            }
            line.Reverse(); // root host first
            var ids = line.Select(IdOf).ToArray();
            for (var depth = 0; depth < line.Count; depth++)
                chains.TryAdd(ids[depth], (line[depth], ids[..(depth + 1)]));
        }
        return chains
            .OrderBy(kv => kv.Value.Chain, ChainComparer.Instance)
            .Select(kv => (kv.Key, kv.Value.Provider))
            .ToList();
    }

    private static string IdOf(IBatchProvider provider) =>
        (provider as IFileSystemProvider)?.Id ??
        throw new InvalidOperationException($"'{provider}' takes batches but is not a file-system provider.");

    private async Task BeginOneAsync(string id, IBatchProvider provider, CancellationToken ct)
    {
        _beginning.Add(id);
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

    /// <summary>A joined scope's request for the batch of <paramref name="id"/>: it must be held and still
    /// open.</summary>
    private void Reuse(string id)
    {
        ThrowIfDisposed();
        if (_beginning.Contains(id))
            throw new InvalidOperationException($"The batch of '{id}' is needed while it is being begun.");
        if (!_held.TryGetValue(id, out var held))
            throw new InvalidOperationException(
                $"The batch of '{id}' is not held by the scope being called; a provider whose changes reach another " +
                "names it as its batch host.");
        if (held.Committed)
            throw new InvalidOperationException($"The batch of '{id}' was committed already.");
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

    /// <summary>The scope a <see cref="BeginAsync"/> made, once it has begun.</summary>
    private sealed class Claim
    {
        public volatile BatchScope? Scope;
    }

    private sealed class Held(IProviderBatch batch)
    {
        public IProviderBatch Batch { get; } = batch;

        public bool Committed { get; set; }
    }

    /// <summary>Id by id (ordinal); when one sequence is a prefix of the other, the shorter first.</summary>
    private sealed class ChainComparer : IComparer<string[]>
    {
        public static readonly ChainComparer Instance = new();

        public int Compare(string[]? x, string[]? y)
        {
            ArgumentNullException.ThrowIfNull(x);
            ArgumentNullException.ThrowIfNull(y);
            for (var i = 0; i < Math.Min(x.Length, y.Length); i++)
            {
                var order = string.CompareOrdinal(x[i], y[i]);
                if (order != 0) return order;
            }
            return x.Length.CompareTo(y.Length);
        }
    }
}
