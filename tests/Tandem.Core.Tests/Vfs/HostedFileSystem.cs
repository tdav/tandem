using Tandem.Core.Vfs;
using Tandem.Core.Vfs.InMemory;

namespace Tandem.Core.Tests.Vfs;

/// <summary>
/// An <see cref="InMemoryFileSystem"/> (id <paramref name="id"/>, reachable as <see cref="Files"/>) that is an
/// <see cref="IHostedProvider"/>: the host locations are the ones set with <see cref="SetHostLocation"/>, the display
/// path is <see cref="Display"/>'s (the path itself when unset). Every other call goes straight through.
/// </summary>
public sealed class HostedFileSystem(string id) : IFileSystemProvider, IHostedProvider
{
    private readonly Dictionary<string, HostLocation> _hostLocations = new(StringComparer.Ordinal);

    public InMemoryFileSystem Files { get; } = new(id);

    public Func<VfsPath, string>? Display { get; set; }

    /// <summary>A provider living in <paramref name="container"/>, a file of another provider: its root leads up to
    /// the container's folder with the container under the cursor, and it displays as the container's display path
    /// followed by its own path.</summary>
    public static HostedFileSystem InFile(string id, ProviderRegistry providers, VfsPath container)
    {
        var hosted = new HostedFileSystem(id);
        hosted.SetHostLocation("/", new HostLocation(container.Parent!.Value, container.Name));
        hosted.Display = path => providers.DisplayPath(container) + (path.IsRoot ? "" : path.Path);
        return hosted;
    }

    public void SetHostLocation(string path, HostLocation location) =>
        _hostLocations[new VfsPath(Id, path).Path] = location;

    public HostLocation? GetHostLocation(VfsPath path) =>
        _hostLocations.TryGetValue(path.Path, out var location) ? location : null;

    public string GetDisplayPath(VfsPath path) => Display?.Invoke(path) ?? path.Path;

    // ---- IFileSystemProvider: straight through ------------------------------------------------------

    public string Id => Files.Id;

    public ProviderCapabilities Capabilities => Files.Capabilities;

    public IAsyncEnumerable<FileEntry> EnumerateAsync(VfsPath directory, CancellationToken ct) =>
        Files.EnumerateAsync(directory, ct);

    public Task<FileEntry?> GetEntryAsync(VfsPath path, CancellationToken ct) => Files.GetEntryAsync(path, ct);

    public Task<Stream> OpenReadAsync(VfsPath path, CancellationToken ct) => Files.OpenReadAsync(path, ct);

    public Task<Stream> OpenWriteAsync(VfsPath path, CancellationToken ct) => Files.OpenWriteAsync(path, ct);

    public Task CreateDirectoryAsync(VfsPath path, CancellationToken ct) => Files.CreateDirectoryAsync(path, ct);

    public Task DeleteAsync(VfsPath path, bool recursive, CancellationToken ct) =>
        Files.DeleteAsync(path, recursive, ct);

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
