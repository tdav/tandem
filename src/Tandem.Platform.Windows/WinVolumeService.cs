using Tandem.Core.Platform;

namespace Tandem.Platform.Windows;

/// <summary>
/// The ready drives from <see cref="DriveInfo.GetDrives"/>: named "{label} (C:)", or "C:" without a label, mounted at
/// <c>C:\</c>; removable for removable and optical drives. The system drive comes first, the rest by letter.
/// <see cref="Changed"/> comes from a poll every 2 seconds that compares the drive letters and their ready flags
/// (inserting a disc or a stick changes them).
/// </summary>
public sealed class WinVolumeService : IVolumeService, IDisposable
{
    private readonly Func<IReadOnlyList<(string Root, bool Ready)>> _snapshot;
    private readonly Timer _timer;
    private readonly Lock _lock = new();
    private string _last;
    private int _polling;
    private bool _disposed;

    public WinVolumeService() : this(DriveSnapshot, TimeSpan.FromSeconds(2)) { }

    /// <summary>For tests: another source of (root, ready) pairs, polled every <paramref name="interval"/>.</summary>
    internal WinVolumeService(Func<IReadOnlyList<(string Root, bool Ready)>> snapshot, TimeSpan interval)
    {
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        _last = Key(snapshot());
        _timer = new Timer(_ => Poll(), null, interval, interval);
    }

    /// <inheritdoc/>
    /// <remarks>Raised on a thread-pool thread.</remarks>
    public event EventHandler? Changed;

    public IReadOnlyList<VolumeInfo> GetVolumes()
    {
        var system = Path.GetPathRoot(Environment.SystemDirectory) ?? "";
        var volumes = new List<VolumeInfo>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady) continue;
                var label = Try(() => drive.VolumeLabel, "");
                var (total, free) = Try(() => (drive.TotalSize, drive.AvailableFreeSpace), (0L, 0L));
                volumes.Add(new VolumeInfo(DisplayName(label, drive.Name), drive.Name, total, free,
                    drive.DriveType is DriveType.Removable or DriveType.CDRom));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Ejected while being listed.
            }
        }
        return
        [
            .. volumes
                .OrderBy(v => string.Equals(v.MountPoint, system, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(v => v.MountPoint, StringComparer.OrdinalIgnoreCase),
        ];
    }

    /// <summary>"{label} (C:)", or "C:" when the label is empty.</summary>
    internal static string DisplayName(string label, string root)
    {
        var letter = root.TrimEnd('\\', '/');
        return string.IsNullOrWhiteSpace(label) ? letter : $"{label} ({letter})";
    }

    /// <summary>Stops the poll. Does not wait for a <see cref="Changed"/> being raised; one late event may still
    /// arrive after it returns.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _timer.Dispose();
    }

    private void Poll()
    {
        // A slow drive (a sleeping network mapping) may outlast the interval: skip the ticks that overlap.
        if (Interlocked.Exchange(ref _polling, 1) == 1) return;
        try
        {
            if (Volatile.Read(ref _disposed)) return;
            string key;
            try
            {
                key = Key(_snapshot());
            }
            catch (Exception)
            {
                return; // try again on the next tick
            }
            if (key == _last) return;
            _last = key;
            RaiseChanged();
        }
        finally
        {
            Volatile.Write(ref _polling, 0);
        }
    }

    private void RaiseChanged()
    {
        if (Changed is not { } changed) return;
        foreach (var handler in changed.GetInvocationList().Cast<EventHandler>())
        {
            if (Volatile.Read(ref _disposed)) return;
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch (Exception)
            {
                // A throwing handler must not stop the others, the poll, or the process (this is a timer thread).
            }
        }
    }

    private static IReadOnlyList<(string Root, bool Ready)> DriveSnapshot() =>
        [.. DriveInfo.GetDrives().Select(d => (d.Name, Try(() => d.IsReady, false)))];

    private static string Key(IReadOnlyList<(string Root, bool Ready)> drives) =>
        string.Join('|', drives
            .Select(d => $"{d.Root.ToUpperInvariant()}{(d.Ready ? '+' : '-')}")
            .Order(StringComparer.Ordinal));

    private static T Try<T>(Func<T> read, T fallback)
    {
        try
        {
            return read();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return fallback;
        }
    }
}
