namespace Tandem.Core.Vfs;

/// <summary>Opens a file as a folder (an archive, for example), registering whatever provider that needs. Panels
/// and commands reach mounters only through <see cref="ProviderRegistry.TryMountAsync"/>.</summary>
public interface IFileMounter
{
    /// <summary>The path of the folder the file opens as, or null if this mounter does not open the file.</summary>
    /// <param name="byContent">false: decide by the name only (cheap; Enter on a file). true: the content may be
    /// read to decide.</param>
    Task<VfsPath?> TryMountAsync(FileEntry file, bool byContent, CancellationToken ct);
}
