namespace Tandem.Core.Platform;

/// <summary>The identity of a directory entry on disk: two paths with the same identity name one entry (a link is
/// itself, never its target). On macOS (device, inode) from <c>lstat</c>; on Windows (volume serial, file index).</summary>
public interface IEntryIdentity
{
    /// <summary>The identity of the entry at the OS path, or null when nothing is there.</summary>
    /// <exception cref="Vfs.VfsAccessDeniedException">The entry cannot be examined.</exception>
    /// <exception cref="Vfs.VfsException">Any other refusal of the system.</exception>
    (ulong Volume, ulong FileId)? Get(string osPath);
}
