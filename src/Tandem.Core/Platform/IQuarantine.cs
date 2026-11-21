namespace Tandem.Core.Platform;

/// <summary>
/// The mark the system keeps on items that came from outside (on macOS the <c>com.apple.quarantine</c> extended
/// attribute, which has Gatekeeper check an app or script before it first runs). The value is opaque: it is read
/// from one item and written to others as it is. Every path must be absolute; implementations throw
/// <see cref="ArgumentException"/> for a relative path. A symbolic link itself is read and written, not its target.
/// </summary>
public interface IQuarantine
{
    /// <summary>The item's mark; null when it has none.</summary>
    /// <exception cref="IOException">The mark could not be read (for example, the item does not exist).</exception>
    /// <exception cref="UnauthorizedAccessException">The item may not be read.</exception>
    byte[]? Get(string path);

    /// <summary>Gives the item the mark <paramref name="value"/>, replacing any it had.</summary>
    /// <exception cref="IOException">The mark could not be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The item may not be changed.</exception>
    void Set(string path, byte[] value);
}
