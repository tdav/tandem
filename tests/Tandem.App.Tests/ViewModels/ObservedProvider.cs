using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Tandem.Core.Vfs;

namespace Tandem.App.Tests.ViewModels;

/// <summary>Wraps a provider for panel tests: counts folder reads, can hold a read after its entries have been
/// produced, keeps the watch callbacks it handed out (to replay late ones) and counts live subscriptions.</summary>
internal sealed class ObservedProvider(IFileSystemProvider inner) : IFileSystemProvider
{
    private readonly ConcurrentDictionary<string, int> _reads = new(StringComparer.Ordinal);
    private int _activeWatches;

    public string Id => inner.Id;
    public ProviderCapabilities Capabilities => inner.Capabilities;

    /// <summary>Awaited after a folder's entries have been read, before the read ends.</summary>
    public Func<VfsPath, Task>? AfterRead { get; set; }

    public ConcurrentBag<(VfsPath Directory, Action Changed)> WatchCallbacks { get; } = [];

    public int ActiveWatches => Volatile.Read(ref _activeWatches);

    public int ReadsOf(string path) => _reads.GetValueOrDefault(path);

    public async IAsyncEnumerable<FileEntry> EnumerateAsync(VfsPath directory,
        [EnumeratorCancellation] CancellationToken ct)
    {
        _reads.AddOrUpdate(directory.Path, 1, (_, n) => n + 1);
        var entries = new List<FileEntry>();
        await foreach (var entry in inner.EnumerateAsync(directory, ct)) entries.Add(entry);
        if (AfterRead is { } afterRead) await afterRead(directory);
        foreach (var entry in entries) yield return entry;
    }

    public IDisposable? Watch(VfsPath directory, Action changed)
    {
        var handle = inner.Watch(directory, changed);
        if (handle is null) return null;
        WatchCallbacks.Add((directory, changed));
        Interlocked.Increment(ref _activeWatches);
        return new Subscription(this, handle);
    }

    private sealed class Subscription(ObservedProvider owner, IDisposable handle) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            handle.Dispose();
            Interlocked.Decrement(ref owner._activeWatches);
        }
    }

    public Task<FileEntry?> GetEntryAsync(VfsPath path, CancellationToken ct) => inner.GetEntryAsync(path, ct);
    public Task<Stream> OpenReadAsync(VfsPath path, CancellationToken ct) => inner.OpenReadAsync(path, ct);
    public Task<Stream> OpenWriteAsync(VfsPath path, CancellationToken ct) => inner.OpenWriteAsync(path, ct);
    public Task CreateDirectoryAsync(VfsPath path, CancellationToken ct) => inner.CreateDirectoryAsync(path, ct);

    public Task DeleteAsync(VfsPath path, bool recursive, CancellationToken ct) =>
        inner.DeleteAsync(path, recursive, ct);

    public Task MoveAsync(VfsPath source, VfsPath destination, CancellationToken ct) =>
        inner.MoveAsync(source, destination, ct);

    public Task CreateSymlinkAsync(VfsPath path, string target, CancellationToken ct) =>
        inner.CreateSymlinkAsync(path, target, ct);

    public Task SetModifiedAsync(VfsPath path, DateTimeOffset modified, CancellationToken ct) =>
        inner.SetModifiedAsync(path, modified, ct);

    public Task SetModeAsync(VfsPath path, UnixFileMode mode, CancellationToken ct) => inner.SetModeAsync(path, mode, ct);
    public Task<bool> IsSameEntryAsync(VfsPath a, VfsPath b, CancellationToken ct) => inner.IsSameEntryAsync(a, b, ct);
    public Task<string> GetVolumeKeyAsync(VfsPath path, CancellationToken ct) => inner.GetVolumeKeyAsync(path, ct);
}
