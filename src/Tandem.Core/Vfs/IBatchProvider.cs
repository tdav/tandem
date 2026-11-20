namespace Tandem.Core.Vfs;

/// <summary>
/// A provider for which every small change is expensive (an archive rewrites its whole file for each), so it collects
/// the changes of an operation and applies them at once. Operations reach it only through <see cref="BatchScope"/>.
/// </summary>
public interface IBatchProvider
{
    /// <summary>Begins a batch: until it is committed, changes are collected, and reads of the provider see the
    /// collected changes. A second batch of the same provider waits until the first one is disposed.
    /// <para>A provider that lives in a file of another provider (a hosted provider: an archive inside an archive)
    /// gets the batch it needs on its host with <see cref="BatchScope.BeginAsync"/> called from this method, holds it
    /// until its own batch is disposed, and may write into the host from <see cref="IProviderBatch.CommitAsync"/>.
    /// Inside an operation's scope that reuses the operation's batch on the host, which commits after this one.</para>
    /// </summary>
    Task<IProviderBatch> BeginBatchAsync(CancellationToken ct);
}

/// <summary>A batch of <see cref="IBatchProvider"/>. <see cref="IAsyncDisposable.DisposeAsync"/> without
/// <see cref="CommitAsync"/> discards the collected changes, and in any case lets the provider's next batch
/// begin.</summary>
public interface IProviderBatch : IAsyncDisposable
{
    /// <summary>A hint: these files will be read during the batch, in this order.</summary>
    Task PrepareReadAsync(IReadOnlyList<VfsPath> files, CancellationToken ct);

    /// <summary>Applies the collected changes. On failure nothing of the batch has been applied.</summary>
    Task CommitAsync(CancellationToken ct);
}
