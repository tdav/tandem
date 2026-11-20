using System.Collections.Concurrent;
using Tandem.Core.Vfs;

namespace Tandem.Core.Tests.Vfs;

/// <summary>
/// Wraps any <see cref="IFileSystemProvider"/> (same id and capabilities) and counts the calls made through it, per
/// method and per path. A method is named as in <c>InMemoryFileSystem.FailOn</c>: the interface method name without
/// "Async" ("Enumerate", "OpenRead", ...). A call is counted when it is made, before it reaches the inner provider;
/// a call with two paths (Move, IsSameEntry) counts once for each. Thread-safe. <see cref="ReplaceRead"/> lets a test
/// hand out a stream of its own in place of the one the inner provider opened.
/// </summary>
public sealed class CountingProvider(IFileSystemProvider inner) : IFileSystemProvider
{
    private readonly ConcurrentDictionary<string, int> _byMethod = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string Method, VfsPath Path), int> _byPath = new();

    public IFileSystemProvider Inner => inner;

    /// <summary>When set, <see cref="OpenReadAsync"/> returns what this gives for the path and the inner provider's
    /// stream (which it then owns).</summary>
    public Func<VfsPath, Stream, Stream>? ReplaceRead { get; set; }

    /// <summary>How many times <paramref name="method"/> was called, for any path.</summary>
    public int Count(string method) => _byMethod.GetValueOrDefault(method);

    /// <summary>How many times <paramref name="method"/> was called for <paramref name="path"/>.</summary>
    public int Count(string method, VfsPath path) => _byPath.GetValueOrDefault((method, path));

    private void Hit(string method, VfsPath path)
    {
        _byMethod.AddOrUpdate(method, 1, (_, n) => n + 1);
        _byPath.AddOrUpdate((method, path), 1, (_, n) => n + 1);
    }

    public string Id => inner.Id;

    public ProviderCapabilities Capabilities => inner.Capabilities;

    public IAsyncEnumerable<FileEntry> EnumerateAsync(VfsPath directory, CancellationToken ct)
    {
        Hit("Enumerate", directory);
        return inner.EnumerateAsync(directory, ct);
    }

    public Task<FileEntry?> GetEntryAsync(VfsPath path, CancellationToken ct)
    {
        Hit("GetEntry", path);
        return inner.GetEntryAsync(path, ct);
    }

    public Task<Stream> OpenReadAsync(VfsPath path, CancellationToken ct)
    {
        Hit("OpenRead", path);
        return ReplaceRead is { } replace ? Replaced(replace) : inner.OpenReadAsync(path, ct);

        async Task<Stream> Replaced(Func<VfsPath, Stream, Stream> replace) =>
            replace(path, await inner.OpenReadAsync(path, ct).ConfigureAwait(false));
    }

    public Task<Stream> OpenWriteAsync(VfsPath path, CancellationToken ct)
    {
        Hit("OpenWrite", path);
        return inner.OpenWriteAsync(path, ct);
    }

    public Task CreateDirectoryAsync(VfsPath path, CancellationToken ct)
    {
        Hit("CreateDirectory", path);
        return inner.CreateDirectoryAsync(path, ct);
    }

    public Task DeleteAsync(VfsPath path, bool recursive, CancellationToken ct)
    {
        Hit("Delete", path);
        return inner.DeleteAsync(path, recursive, ct);
    }

    public Task MoveAsync(VfsPath source, VfsPath destination, CancellationToken ct)
    {
        Hit("Move", source);
        Hit("Move", destination);
        return inner.MoveAsync(source, destination, ct);
    }

    public Task CreateSymlinkAsync(VfsPath path, string target, CancellationToken ct)
    {
        Hit("CreateSymlink", path);
        return inner.CreateSymlinkAsync(path, target, ct);
    }

    public Task SetModifiedAsync(VfsPath path, DateTimeOffset modified, CancellationToken ct)
    {
        Hit("SetModified", path);
        return inner.SetModifiedAsync(path, modified, ct);
    }

    public Task SetModeAsync(VfsPath path, UnixFileMode mode, CancellationToken ct)
    {
        Hit("SetMode", path);
        return inner.SetModeAsync(path, mode, ct);
    }

    public Task<bool> IsSameEntryAsync(VfsPath a, VfsPath b, CancellationToken ct)
    {
        Hit("IsSameEntry", a);
        Hit("IsSameEntry", b);
        return inner.IsSameEntryAsync(a, b, ct);
    }

    public Task<string> GetVolumeKeyAsync(VfsPath path, CancellationToken ct)
    {
        Hit("GetVolumeKey", path);
        return inner.GetVolumeKeyAsync(path, ct);
    }

    public IDisposable? Watch(VfsPath directory, Action changed)
    {
        Hit("Watch", directory);
        return inner.Watch(directory, changed);
    }
}
