namespace Tandem.Core.Platform;

/// <summary>The names of the user and group that own an item.</summary>
public sealed record OwnerInfo(string User, string Group);

/// <summary>
/// Metadata the file system itself does not show in a listing: the user's tags (Finder tags on macOS) and the
/// owner. Every path must be absolute; implementations throw <see cref="ArgumentException"/> for a relative path.
/// </summary>
public interface IFileMetadataService
{
    /// <summary>The item's tag names, in their stored order; empty when it has none.</summary>
    /// <exception cref="IOException">The tags could not be read (for example, the item does not exist).</exception>
    IReadOnlyList<string> GetTags(string path);

    /// <summary>Replaces the item's tags with <paramref name="tags"/>; an empty list removes them all.</summary>
    /// <exception cref="ArgumentException">A tag name is null or empty.</exception>
    /// <exception cref="IOException">The tags could not be written (for example, the item does not exist).</exception>
    void SetTags(string path, IReadOnlyList<string> tags);

    /// <summary>The user and group that own the item (a symbolic link itself, not its target).</summary>
    /// <exception cref="IOException">The owner could not be read (for example, the item does not exist).</exception>
    OwnerInfo GetOwner(string path);
}
