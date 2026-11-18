namespace Tandem.Core.Vfs.Local;

/// <summary>
/// Grants access to paths on the local disk. Every disk access of <see cref="LocalFileSystemProvider"/> runs
/// inside an <see cref="Access"/> scope, so a sandboxed build can swap in a broker backed by security-scoped
/// bookmarks without touching the provider.
/// </summary>
public interface IFileAccessBroker
{
    /// <summary>Opens an access scope for <paramref name="path"/>; dispose it when the access is over.</summary>
    /// <exception cref="VfsAccessDeniedException">The path may not be accessed.</exception>
    IDisposable Access(string path);

    /// <summary>Asks the user for access to <paramref name="path"/>; true when it was granted.</summary>
    Task<bool> RequestAccessAsync(string path, CancellationToken ct);
}
