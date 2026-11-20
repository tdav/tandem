using Tandem.Core.Vfs;
using Tandem.Core.Vfs.InMemory;

namespace Tandem.Core.Tests.Vfs;

/// <summary>The events of one or more <see cref="BatchingProvider"/>s, in order: "{provider}:{event}" with the paths
/// after a space, e.g. "arc:Begin", "arc:Write /dst/.a.txt.tandem-part", "arc:PrepareRead /a.txt,/b.txt".</summary>
public sealed class BatchJournal
{
    private readonly Lock _lock = new();
    private readonly List<string> _events = [];

    public IReadOnlyList<string> Events
    {
        get { lock (_lock) return _events.ToArray(); }
    }

    public void Add(string entry)
    {
        lock (_lock) _events.Add(entry);
    }

    /// <summary>The position of the first event equal to <paramref name="entry"/> or starting with it and a space;
    /// -1 when there is none.</summary>
    public int IndexOf(string entry) => Events.ToList().FindIndex(e => Matches(e, entry));

    public int LastIndexOf(string entry) => Events.ToList().FindLastIndex(e => Matches(e, entry));

    public int Count(string entry) => Events.Count(e => Matches(e, entry));

    private static bool Matches(string e, string entry) =>
        e == entry || e.StartsWith(entry + " ", StringComparison.Ordinal);
}

/// <summary>
/// An <see cref="InMemoryFileSystem"/> (id <paramref name="id"/>, reachable as <see cref="Files"/>) that takes batches:
/// every call goes straight through, and <see cref="IBatchProvider.BeginBatchAsync"/>, the batch's
/// <c>PrepareRead</c>, <c>Commit</c> and <c>Dispose</c>, and every <c>Delete</c> and <c>OpenWrite</c> ("Write") are
/// written to <paramref name="journal"/>. One batch at a time: a second <see cref="BeginBatchAsync"/> waits for the
/// first batch to be disposed.
/// <para>With <see cref="HostedIn"/> it behaves like a provider that lives in a file of another provider (an archive
/// inside an archive): <see cref="BatchHost"/> names the host, and its commit writes the container file into the
/// host — through a batch scope on the host begun inside the commit, which joins the scope that holds both. As an
/// <see cref="IHostedProvider"/> its root then leads up to the container's folder; standing alone it has no host
/// location.</para>
/// </summary>
public sealed class BatchingProvider(string id, BatchJournal journal) : IFileSystemProvider, IBatchProvider,
    IHostedProvider
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private (ProviderRegistry Providers, VfsPath Container)? _host;

    public InMemoryFileSystem Files { get; } = new(id);

    public BatchJournal Journal => journal;

    /// <summary>The provider of the container file, after <see cref="HostedIn"/>; or one set directly.</summary>
    public IBatchProvider? BatchHost { get; set; }

    /// <summary>When set, a begin runs this after taking the gate (and journaling "Begin").</summary>
    public Func<CancellationToken, Task>? OnBegin { get; set; }

    /// <summary>When set, a commit runs this after its own work, before journaling "Commit".</summary>
    public Func<CancellationToken, Task>? OnCommit { get; set; }

    /// <summary>When set, a commit throws what it returns (after the commit gate, before anything is
    /// committed).</summary>
    public Func<Exception>? FailCommit { get; set; }

    /// <summary>When set, a dispose throws what it returns (after releasing the gate).</summary>
    public Func<Exception>? FailDispose { get; set; }

    /// <summary>When set, a commit waits for this task first.</summary>
    public Task? CommitGate { get; set; }

    /// <summary>Makes this provider live in <paramref name="container"/>, a file of another provider; its
    /// <see cref="BatchHost"/> is that provider when it takes batches.</summary>
    public void HostedIn(ProviderRegistry providers, VfsPath container)
    {
        _host = (providers, container);
        BatchHost = providers.For(container) as IBatchProvider;
    }

    public HostLocation? GetHostLocation(VfsPath path) =>
        path.IsRoot && _host is { } host ? new HostLocation(host.Container.Parent!.Value, host.Container.Name) : null;

    public string GetDisplayPath(VfsPath path) =>
        _host is { } host ? host.Providers.DisplayPath(host.Container) + (path.IsRoot ? "" : path.Path) : path.Path;

    public async Task<IProviderBatch> BeginBatchAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            journal.Add($"{Id}:Begin");
            if (OnBegin is { } onBegin) await onBegin(ct).ConfigureAwait(false);
            return new Batch(this);
        }
        catch
        {
            _gate.Release();
            throw;
        }
    }

    private sealed class Batch(BatchingProvider owner) : IProviderBatch
    {
        private bool _disposed;

        public Task PrepareReadAsync(IReadOnlyList<VfsPath> files, CancellationToken ct)
        {
            owner.Journal.Add($"{owner.Id}:PrepareRead {string.Join(',', files.Select(f => f.Path))}");
            return Task.CompletedTask;
        }

        public async Task CommitAsync(CancellationToken ct)
        {
            if (owner.CommitGate is { } gate) await gate.WaitAsync(ct).ConfigureAwait(false);
            if (owner.FailCommit is { } fail) throw fail();
            if (owner._host is { } host)
            {
                // Writing the container back into the host, through a batch on the host begun right here: it joins
                // the scope that holds this batch and its host's.
                await using var scope = await BatchScope.BeginAsync(host.Providers, [host.Container], ct)
                    .ConfigureAwait(false);
                await using (var stream = await host.Providers.For(host.Container).OpenWriteAsync(host.Container, ct)
                                 .ConfigureAwait(false))
                    await stream.WriteAsync(new byte[] { 1, 2, 3 }, ct).ConfigureAwait(false);
                await scope.CommitAsync(ct).ConfigureAwait(false);
            }
            if (owner.OnCommit is { } onCommit) await onCommit(ct).ConfigureAwait(false);
            owner.Journal.Add($"{owner.Id}:Commit");
        }

        public ValueTask DisposeAsync()
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
            owner.Journal.Add($"{owner.Id}:Dispose");
            owner._gate.Release();
            return owner.FailDispose is { } fail ? ValueTask.FromException(fail()) : ValueTask.CompletedTask;
        }
    }

    // ---- IFileSystemProvider: straight through ------------------------------------------------------

    public string Id => Files.Id;

    public ProviderCapabilities Capabilities => Files.Capabilities;

    public IAsyncEnumerable<FileEntry> EnumerateAsync(VfsPath directory, CancellationToken ct) =>
        Files.EnumerateAsync(directory, ct);

    public Task<FileEntry?> GetEntryAsync(VfsPath path, CancellationToken ct) => Files.GetEntryAsync(path, ct);

    public Task<Stream> OpenReadAsync(VfsPath path, CancellationToken ct) => Files.OpenReadAsync(path, ct);

    public Task<Stream> OpenWriteAsync(VfsPath path, CancellationToken ct)
    {
        journal.Add($"{Id}:Write {path.Path}");
        return Files.OpenWriteAsync(path, ct);
    }

    public Task CreateDirectoryAsync(VfsPath path, CancellationToken ct) => Files.CreateDirectoryAsync(path, ct);

    public Task DeleteAsync(VfsPath path, bool recursive, CancellationToken ct)
    {
        journal.Add($"{Id}:Delete {path.Path}");
        return Files.DeleteAsync(path, recursive, ct);
    }

    public Task MoveAsync(VfsPath source, VfsPath destination, CancellationToken ct) =>
        Files.MoveAsync(source, destination, ct);

    public Task CreateSymlinkAsync(VfsPath path, string target, CancellationToken ct) =>
        Files.CreateSymlinkAsync(path, target, ct);

    public Task SetModifiedAsync(VfsPath path, DateTimeOffset modified, CancellationToken ct) =>
        Files.SetModifiedAsync(path, modified, ct);

    public Task SetModeAsync(VfsPath path, UnixFileMode mode, CancellationToken ct) => Files.SetModeAsync(path, mode, ct);

    public Task<bool> IsSameEntryAsync(VfsPath a, VfsPath b, CancellationToken ct) => Files.IsSameEntryAsync(a, b, ct);

    public Task<string> GetVolumeKeyAsync(VfsPath path, CancellationToken ct) => Files.GetVolumeKeyAsync(path, ct);

    public IDisposable? Watch(VfsPath directory, Action changed) => Files.Watch(directory, changed);
}
