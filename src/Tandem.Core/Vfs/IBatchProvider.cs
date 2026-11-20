namespace Tandem.Core.Vfs;

/// <summary>
/// A provider for which every small change is expensive (an archive rewrites its whole file for each), so it collects
/// the changes of an operation and applies them at once. Operations reach it only through <see cref="BatchScope"/>,
/// which also decides the order in which batches are begun, committed and disposed.
/// </summary>
public interface IBatchProvider
{
    /// <summary>Begins a batch: until it is committed, changes are collected, and reads of the provider see the
    /// collected changes. A second batch of the same provider waits until the first one is disposed.
    /// <para>It does not begin batches of other providers: a host is batched by the scope, before this provider
    /// (see <see cref="BatchHost"/>).</para></summary>
    Task<IProviderBatch> BeginBatchAsync(CancellationToken ct);

    /// <summary>The batching provider this provider's changes end up in — the one holding the file it lives in (an
    /// archive inside an archive) — or null for a provider that stands on its own. A <see cref="BatchScope"/> that
    /// batches this provider batches its host too, begins the host first and commits it last; the commit of this
    /// provider may write into the host (through <see cref="BatchScope.BeginAsync"/>, which then joins the scope that
    /// holds both). The chain of hosts must not form a cycle.
    /// <para>It is fixed for the provider's life, and a non-null value is the instance registered in the
    /// <see cref="ProviderRegistry"/> under that host's id (not a wrapper or a copy).</para>
    /// <para>For an <see cref="IHostedProvider"/> it is the provider of its root's host location when that provider
    /// takes batches, and null otherwise; <see cref="ProviderRegistry.Register"/> refuses anything else.</para>
    /// </summary>
    IBatchProvider? BatchHost { get; }
}

/// <summary>A batch of <see cref="IBatchProvider"/>. <see cref="IAsyncDisposable.DisposeAsync"/> without
/// <see cref="CommitAsync"/> discards the collected changes, and in any case lets the provider's next batch
/// begin.
/// <para>Work started from <see cref="IBatchProvider.BeginBatchAsync"/> or from these calls must not outlive the call
/// and begin batches later: such work inherits the calling scope (it flows with the execution context) and would join
/// it after the call has ended.</para></summary>
public interface IProviderBatch : IAsyncDisposable
{
    /// <summary>A hint: these files will be read during the batch, in this order.</summary>
    Task PrepareReadAsync(IReadOnlyList<VfsPath> files, CancellationToken ct);

    /// <summary>Applies the collected changes. On failure nothing of the batch has been applied.</summary>
    Task CommitAsync(CancellationToken ct);
}
