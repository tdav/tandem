using System.Runtime.Versioning;
using Tandem.Core.Platform;
using Tandem.Platform.Mac.Interop;

namespace Tandem.Platform.Mac;

/// <summary>
/// Volumes from <c>-[NSFileManager mountedVolumeURLsIncludingResourceValuesForKeys:options:]</c> (hidden ones
/// skipped), sizes from <see cref="DriveInfo"/>. <see cref="Changed"/> follows <c>/Volumes</c>, where macOS places
/// every mount point but the root, and fires once a burst of changes there has been quiet for 500 ms.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacVolumeService : IVolumeService, IDisposable
{
    /// <summary><c>NSVolumeEnumerationSkipHiddenVolumes</c> (NSFileManager.h).</summary>
    private const nuint SkipHiddenVolumes = 1 << 1;

    private const string Root = "/";

    private static readonly Lazy<IntPtr> NameKey = new(() => ObjC.FoundationConstant("NSURLVolumeLocalizedNameKey"));
    private static readonly Lazy<IntPtr> RemovableKey = new(() => ObjC.FoundationConstant("NSURLVolumeIsRemovableKey"));
    private static readonly Lazy<IntPtr> EjectableKey = new(() => ObjC.FoundationConstant("NSURLVolumeIsEjectableKey"));

    private readonly FileSystemWatcher _watcher;
    private readonly Timer _debounceTimer;
    private readonly TimeSpan _debounce;
    private readonly Lock _lock = new();
    private bool _disposed;

    /// <summary>The managed id of the thread raising <see cref="Changed"/>, or 0.</summary>
    private int _raisingThread;

    public MacVolumeService() : this("/Volumes", TimeSpan.FromMilliseconds(500))
    {
    }

    /// <summary>Watches <paramref name="watchedDirectory"/> instead of <c>/Volumes</c> (for tests).</summary>
    internal MacVolumeService(string watchedDirectory, TimeSpan debounce)
    {
        _debounce = debounce;
        _debounceTimer = new Timer(_ => RaiseChanged(), null, Timeout.Infinite, Timeout.Infinite);
        _watcher = new FileSystemWatcher(watchedDirectory)
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.DirectoryName | NotifyFilters.FileName,
        };
        _watcher.Created += (_, _) => ScheduleChanged();
        _watcher.Deleted += (_, _) => ScheduleChanged();
        _watcher.Renamed += (_, _) => ScheduleChanged();
        // Events were lost (or the watch broke): the volumes may have changed.
        _watcher.Error += (_, _) => ScheduleChanged();
        _watcher.EnableRaisingEvents = true;
    }

    /// <inheritdoc/>
    /// <remarks>Raised on a thread-pool thread.</remarks>
    public event EventHandler? Changed;

    public IReadOnlyList<VolumeInfo> GetVolumes()
    {
        var volumes = new List<(string Name, string MountPoint, bool IsRemovable)>();
        using (new ObjC.AutoreleasePool())
        {
            var (nameKey, removableKey, ejectableKey) = (NameKey.Value, RemovableKey.Value, EjectableKey.Value);
            var manager = ObjC.Send(ObjC.Class("NSFileManager"), ObjC.Selector("defaultManager"));
            var urls = ObjC.SendObjectOptions(manager,
                ObjC.Selector("mountedVolumeURLsIncludingResourceValuesForKeys:options:"),
                KeyArray(nameKey, removableKey, ejectableKey), SkipHiddenVolumes);
            if (urls == IntPtr.Zero) return [];

            var count = ObjC.SendNUInt(urls, ObjC.Selector("count"));
            for (nuint i = 0; i < count; i++)
            {
                var url = ObjC.SendIndex(urls, ObjC.Selector("objectAtIndex:"), i);
                if (ObjC.PathOf(url) is not { Length: > 0 } path) continue;
                var mountPoint = path == Root ? Root : path.TrimEnd('/');
                try
                {
                    var name = ObjC.ToManagedString(ObjC.GetResourceValue(url, nameKey));
                    var isRemovable = ObjC.ToBool(ObjC.GetResourceValue(url, removableKey)) ||
                                      ObjC.ToBool(ObjC.GetResourceValue(url, ejectableKey));
                    volumes.Add((string.IsNullOrEmpty(name) ? FallbackName(mountPoint) : name, mountPoint, isRemovable));
                }
                catch (IOException)
                {
                    // Unmounted while being listed.
                }
            }
        }

        return
        [
            .. volumes
                .Select(v =>
                {
                    var (total, free) = Space(v.MountPoint);
                    return new VolumeInfo(v.Name, v.MountPoint, total, free, v.IsRemovable);
                })
                .OrderBy(v => v.MountPoint == Root ? 0 : 1)
                .ThenBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(v => v.MountPoint, StringComparer.Ordinal),
        ];
    }

    /// <summary>Stops watching. Once it returns, <see cref="Changed"/> is not raised any more: it waits for a
    /// <see cref="Changed"/> already being raised, unless called from one of its handlers.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
        }

        _watcher.Dispose();
        if (Volatile.Read(ref _raisingThread) == Environment.CurrentManagedThreadId)
        {
            // Called from a Changed handler: waiting for that callback to finish would wait for ourselves.
            _debounceTimer.Dispose();
            return;
        }

        using var callbacksDone = new ManualResetEvent(false);
        if (_debounceTimer.Dispose(callbacksDone)) callbacksDone.WaitOne();
    }

    private static unsafe IntPtr KeyArray(params ReadOnlySpan<IntPtr> keys)
    {
        fixed (IntPtr* first = keys)
            return ObjC.SendObjectsCount(ObjC.Class("NSArray"), ObjC.Selector("arrayWithObjects:count:"), first,
                (nuint)keys.Length);
    }

    private static string FallbackName(string mountPoint) =>
        mountPoint == Root ? Root : Path.GetFileName(mountPoint);

    /// <summary>Total and free bytes; zeros when the volume cannot be asked (gone, or not readable).</summary>
    private static (long Total, long Free) Space(string mountPoint)
    {
        try
        {
            var drive = new DriveInfo(mountPoint);
            return (drive.TotalSize, drive.AvailableFreeSpace);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (0, 0);
        }
    }

    /// <summary>(Re)starts the quiet period; <see cref="Changed"/> fires when it passes without another change.</summary>
    private void ScheduleChanged()
    {
        lock (_lock)
        {
            if (!_disposed) _debounceTimer.Change(_debounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void RaiseChanged()
    {
        lock (_lock)
        {
            if (_disposed) return;
        }

        if (Changed is not { } changed) return;
        Volatile.Write(ref _raisingThread, Environment.CurrentManagedThreadId);
        try
        {
            foreach (var handler in changed.GetInvocationList().Cast<EventHandler>())
            {
                if (Volatile.Read(ref _disposed)) return;
                try
                {
                    handler(this, EventArgs.Empty);
                }
                catch (Exception)
                {
                    // A throwing handler must not stop the others, the watch, or the process (this is a timer thread).
                }
            }
        }
        finally
        {
            Volatile.Write(ref _raisingThread, 0);
        }
    }
}
