namespace Tandem.Core.Vfs;

public enum EntryKind { File, Directory, Symlink }

/// <summary>A snapshot of one directory entry as reported by a file-system provider.</summary>
public sealed record FileEntry(VfsPath Path, EntryKind Kind, long Size,
    DateTimeOffset Modified, DateTimeOffset Created, UnixFileMode? Mode,
    bool IsHidden, string? LinkTarget = null, bool LinkPointsToDirectory = false)
{
    public string Name => Path.Name;

    /// <summary>The part after the last dot, without the dot; "" for directory-like entries and names like ".gitignore".</summary>
    public string Extension
    {
        get
        {
            if (IsDirectoryLike) return "";
            var name = Name;
            var dot = name.LastIndexOf('.');
            return dot <= 0 ? "" : name[(dot + 1)..];
        }
    }

    /// <summary>A directory, or a symlink that points to a directory.</summary>
    public bool IsDirectoryLike => Kind == EntryKind.Directory || (Kind == EntryKind.Symlink && LinkPointsToDirectory);
}
