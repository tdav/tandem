namespace Tandem.Core.Vfs;

[Flags]
public enum ProviderCapabilities { None = 0, Write = 1, Trash = 2, UnixPermissions = 4, Symlinks = 8, Watch = 16 }

/// <summary>
/// A source of files (local disk, archive, network, memory). Panels and the operations engine talk only
/// to this interface. Failures are reported as <see cref="VfsException"/> or another <see cref="IOException"/>.
/// </summary>
public interface IFileSystemProvider
{
    string Id { get; }

    ProviderCapabilities Capabilities { get; }

    /// <summary>Lists the direct children of a directory. Symlinks are reported as links, not followed.</summary>
    /// <exception cref="VfsNotFoundException">The directory does not exist.</exception>
    IAsyncEnumerable<FileEntry> EnumerateAsync(VfsPath directory, CancellationToken ct);

    /// <summary>Describes the entry itself (a symlink is reported as a symlink); null if it does not exist.</summary>
    Task<FileEntry?> GetEntryAsync(VfsPath path, CancellationToken ct);

    Task<Stream> OpenReadAsync(VfsPath path, CancellationToken ct);

    /// <summary>Creates the file, or truncates it if it exists. The parent directory must exist.</summary>
    Task<Stream> OpenWriteAsync(VfsPath path, CancellationToken ct);

    /// <summary>Creates the directory and any missing parents; an existing directory is not an error.</summary>
    Task CreateDirectoryAsync(VfsPath path, CancellationToken ct);

    /// <summary>Deletes a file, link or directory. A non-empty directory needs <paramref name="recursive"/>.</summary>
    Task DeleteAsync(VfsPath path, bool recursive, CancellationToken ct);

    /// <summary>Renames within the provider.</summary>
    /// <exception cref="VfsAlreadyExistsException">The destination exists.</exception>
    Task MoveAsync(VfsPath source, VfsPath destination, CancellationToken ct);

    Task CreateSymlinkAsync(VfsPath path, string target, CancellationToken ct);

    Task SetModifiedAsync(VfsPath path, DateTimeOffset modified, CancellationToken ct);

    Task SetModeAsync(VfsPath path, UnixFileMode mode, CancellationToken ct);

    /// <summary>A key that is equal for all paths on the same volume.</summary>
    Task<string> GetVolumeKeyAsync(VfsPath path, CancellationToken ct);

    /// <summary>Calls <paramref name="changed"/> when the directory's contents change; null if not supported.</summary>
    IDisposable? Watch(VfsPath directory, Action changed);
}
