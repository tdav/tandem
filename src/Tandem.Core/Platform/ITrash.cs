namespace Tandem.Core.Platform;

/// <summary>The system trash (the Finder's Trash on macOS), for local items only.</summary>
public interface ITrash
{
    /// <summary>Moves the item at <paramref name="localPath"/> — a file, a whole directory, or a symbolic link
    /// itself (never the item it points to) — to the trash.</summary>
    Task MoveToTrashAsync(string localPath, CancellationToken ct);
}
