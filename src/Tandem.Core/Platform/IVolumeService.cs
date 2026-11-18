namespace Tandem.Core.Platform;

/// <summary>A mounted volume: its display name, where it is mounted, its size and free space in bytes (0 when they
/// cannot be read), and whether it is removable or ejectable media.</summary>
public sealed record VolumeInfo(string Name, string MountPoint, long TotalBytes, long FreeBytes, bool IsRemovable);

/// <summary>The volumes the user sees (hidden system volumes are left out).</summary>
public interface IVolumeService
{
    /// <summary>The mounted volumes, the root volume (<c>/</c>) first and the rest by name. Safe to call from any
    /// thread.</summary>
    IReadOnlyList<VolumeInfo> GetVolumes();

    /// <summary>Raised, possibly on a background thread, after volumes were mounted or unmounted; bursts of changes
    /// are coalesced into one event. A handler that throws does not stop later events.</summary>
    event EventHandler? Changed;
}
