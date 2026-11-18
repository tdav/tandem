using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Tandem.Core.Vfs.Local;

/// <summary>
/// The local disk. A <see cref="VfsPath"/> of provider "local" maps one-to-one onto an absolute OS path.
/// Every disk access runs on the thread pool inside a <see cref="IFileAccessBroker.Access"/> scope, and .NET
/// access/not-found errors are reported as <see cref="VfsAccessDeniedException"/>/<see cref="VfsNotFoundException"/>.
/// Symlinks are followed only by <see cref="EnumerateAsync"/> and <see cref="OpenReadAsync"/> (the OS resolves
/// links anywhere in a path, unlike the in-memory provider).
/// </summary>
public sealed class LocalFileSystemProvider(IFileAccessBroker broker) : IFileSystemProvider
{
    private const int EnumerationBatchSize = 256;
    private const int StreamBufferSize = 81920;
    private static readonly TimeSpan WatchCoalesceDelay = TimeSpan.FromMilliseconds(300);

    private static readonly EnumerationOptions ListingOptions = new()
    {
        AttributesToSkip = 0, // the default skips hidden and system entries
        IgnoreInaccessible = false,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    private readonly IFileAccessBroker _broker = broker ?? throw new ArgumentNullException(nameof(broker));

    public string Id => VfsPath.LocalProviderId;

    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Write | ProviderCapabilities.Trash | ProviderCapabilities.UnixPermissions |
        ProviderCapabilities.Symlinks | ProviderCapabilities.Watch;

    /// <summary>Yields entries in batches read on the thread pool, so a huge directory shows up progressively;
    /// the broker scope stays open until the enumeration ends.</summary>
    public async IAsyncEnumerable<FileEntry> EnumerateAsync(VfsPath directory,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var listing = await Task.Run(() => Listing.Open(_broker, ToOsPath(directory)), ct);
        try
        {
            while (true)
            {
                var batch = await Task.Run(() => listing.Next(directory, EnumerationBatchSize), ct);
                foreach (var entry in batch)
                {
                    ct.ThrowIfCancellationRequested();
                    yield return entry;
                }
                if (batch.Count < EnumerationBatchSize) yield break;
            }
        }
        finally
        {
            listing.Dispose();
        }
    }

    public Task<FileEntry?> GetEntryAsync(VfsPath path, CancellationToken ct) => RunAsync(path, ct, p =>
    {
        var info = new FileInfo(p);
        return Exists(info) ? ToEntry(path, info) : null;
    });

    public Task<Stream> OpenReadAsync(VfsPath path, CancellationToken ct) => RunAsync<Stream>(path, ct, p =>
    {
        if (Directory.Exists(p)) throw new VfsException($"'{p}' is a directory.");
        return new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.Read, StreamBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
    });

    public Task<Stream> OpenWriteAsync(VfsPath path, CancellationToken ct) => RunAsync<Stream>(path, ct, p =>
    {
        if (Directory.Exists(p)) throw new VfsException($"'{p}' is a directory.");
        return new FileStream(p, FileMode.Create, FileAccess.Write, FileShare.Read, StreamBufferSize,
            FileOptions.Asynchronous);
    });

    public Task CreateDirectoryAsync(VfsPath path, CancellationToken ct) =>
        RunAsync(path, ct, p => Directory.CreateDirectory(p));

    public Task DeleteAsync(VfsPath path, bool recursive, CancellationToken ct) => RunAsync(path, ct, p =>
    {
        var info = new FileInfo(p);
        if (!Exists(info)) throw new VfsNotFoundException($"'{p}' does not exist.");
        if (path.IsRoot) throw new VfsAccessDeniedException("Cannot delete the root.");
        if (IsRealDirectory(info.Attributes))
            Directory.Delete(p, recursive); // does not follow links inside the tree
        else
            File.Delete(p); // a file or a link (the link itself, never its target)
    });

    public Task MoveAsync(VfsPath source, VfsPath destination, CancellationToken ct) => RunAsync(source, ct, src =>
    {
        var dst = ToOsPath(destination);
        Translate(dst, () =>
        {
            using var access = _broker.Access(dst);
            if (Exists(new FileInfo(dst)) && !IsCaseOnlyRename(src, dst))
                throw new VfsAlreadyExistsException($"'{dst}' already exists.");
            Directory.Move(src, dst); // rename(2): works for files, directories and links
            return true;
        });
    });

    public Task CreateSymlinkAsync(VfsPath path, string target, CancellationToken ct) => RunAsync(path, ct, p =>
    {
        ArgumentException.ThrowIfNullOrEmpty(target);
        if (Exists(new FileInfo(p))) throw new VfsAlreadyExistsException($"'{p}' already exists.");
        File.CreateSymbolicLink(p, target);
    });

    public Task SetModifiedAsync(VfsPath path, DateTimeOffset modified, CancellationToken ct) => RunAsync(path, ct, p =>
    {
        if (Directory.Exists(p))
            Directory.SetLastWriteTimeUtc(p, modified.UtcDateTime);
        else
            File.SetLastWriteTimeUtc(p, modified.UtcDateTime);
    });

    public Task SetModeAsync(VfsPath path, UnixFileMode mode, CancellationToken ct) => RunAsync(path, ct, p =>
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Unix modes are not supported on Windows.");
        File.SetUnixFileMode(p, mode);
    });

    /// <summary>Compares device and inode numbers (<c>lstat</c>, so a link is itself, not its target); the OS
    /// resolves the rest of each path, including letter case and linked folders.</summary>
    public Task<bool> IsSameEntryAsync(VfsPath a, VfsPath b, CancellationToken ct) => RunAsync(a, ct, first =>
    {
        var second = ToOsPath(b);
        using var access = _broker.Access(second);
        return Identity(first) is { } x && Identity(second) is { } y && x == y;
    });

    /// <summary>The mount point of the longest mounted volume that contains the path (links are not resolved).</summary>
    public Task<string> GetVolumeKeyAsync(VfsPath path, CancellationToken ct) => RunAsync(path, ct, _ =>
        DriveInfo.GetDrives()
            .Select(drive => VfsPath.Local(drive.RootDirectory.FullName))
            .Where(path.IsSameOrDescendantOf)
            .OrderByDescending(mount => mount.Path.Length)
            .Select(mount => mount.Path)
            .FirstOrDefault() ?? "/");

    /// <summary>Watches the directory itself (not subdirectories); bursts of changes are coalesced into one call
    /// <see cref="WatchCoalesceDelay"/> after the first change. The broker scope lives as long as the watch.</summary>
    public IDisposable? Watch(VfsPath directory, Action changed)
    {
        ArgumentNullException.ThrowIfNull(changed);
        var p = ToOsPath(directory);
        return Translate(p, () =>
        {
            var access = _broker.Access(p);
            try
            {
                if (!Directory.Exists(p)) throw new VfsNotFoundException($"Directory '{p}' does not exist.");
                return new DirectoryWatch(p, access, changed);
            }
            catch
            {
                access.Dispose();
                throw;
            }
        });
    }

    // ---- Internals -----------------------------------------------------------------------------------

    private string ToOsPath(VfsPath path)
    {
        if (!string.Equals(path.ProviderId, Id, StringComparison.Ordinal))
            throw new ArgumentException($"Path '{path}' does not belong to provider '{Id}'.", nameof(path));
        return path.Path;
    }

    private Task<T> RunAsync<T>(VfsPath path, CancellationToken ct, Func<string, T> body) => Task.Run(() =>
    {
        var p = ToOsPath(path);
        return Translate(p, () =>
        {
            using var access = _broker.Access(p);
            return body(p);
        });
    }, ct);

    private Task RunAsync(VfsPath path, CancellationToken ct, Action<string> body) => RunAsync<object?>(path, ct, p =>
    {
        body(p);
        return null;
    });

    private static T Translate<T>(string path, Func<T> body)
    {
        try
        {
            return body();
        }
        catch (UnauthorizedAccessException e)
        {
            throw new VfsAccessDeniedException($"Access to '{path}' is denied.", e);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new VfsNotFoundException($"'{path}' does not exist.", e);
        }
    }

    private const int ENOENT = 2, EACCES = 13, ENOTDIR = 20;
    private const int StatBufferSize = 256; // struct stat is 144 bytes on macOS

    /// <summary>(device, inode) of the entry itself, or null when it does not exist.</summary>
    private static (int Device, ulong Inode)? Identity(string path)
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("Entry identity is implemented for macOS only.");
        var buffer = new byte[StatBufferSize];
        if (LStat(path, buffer) != 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            if (errno is ENOENT or ENOTDIR) return null;
            if (errno == EACCES) throw new VfsAccessDeniedException($"Access to '{path}' is denied.");
            throw new VfsException($"Cannot read '{path}' (errno {errno}).");
        }
        // macOS (64-bit inodes): dev_t st_dev at 0 (int32), ino_t st_ino at 8 (uint64).
        return (BitConverter.ToInt32(buffer, 0), BitConverter.ToUInt64(buffer, 8));
    }

    [DllImport("libc", EntryPoint = "lstat", SetLastError = true)]
    private static extern int LStat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [Out] byte[] buffer);

    /// <summary>True when the entry itself exists (a broken link counts); <see cref="FileSystemInfo.Attributes"/>
    /// is -1 for a missing path.</summary>
    private static bool Exists(FileSystemInfo info) => (int)info.Attributes != -1;

    private static bool IsRealDirectory(FileAttributes attributes) =>
        attributes.HasFlag(FileAttributes.Directory) && !attributes.HasFlag(FileAttributes.ReparsePoint);

    /// <summary>A rename that changes only letter case: on a case-insensitive volume the destination "exists"
    /// because it is the source itself. A distinct entry spelled exactly like the destination still blocks it.</summary>
    private static bool IsCaseOnlyRename(string source, string destination)
    {
        var parent = Path.GetDirectoryName(source);
        if (parent is null || !string.Equals(parent, Path.GetDirectoryName(destination), StringComparison.Ordinal))
            return false;
        var sourceName = Path.GetFileName(source);
        var destinationName = Path.GetFileName(destination);
        if (sourceName == destinationName || !sourceName.Equals(destinationName, StringComparison.OrdinalIgnoreCase))
            return false;
        return !Directory.EnumerateFileSystemEntries(parent, "*", ListingOptions)
            .Any(entry => string.Equals(Path.GetFileName(entry), destinationName, StringComparison.Ordinal));
    }

    private static FileEntry ToEntry(VfsPath path, FileSystemInfo info)
    {
        var attributes = info.Attributes;
        var linkTarget = attributes.HasFlag(FileAttributes.ReparsePoint) ? info.LinkTarget : null;
        var kind = linkTarget is not null ? EntryKind.Symlink
            : attributes.HasFlag(FileAttributes.Directory) ? EntryKind.Directory
            : EntryKind.File;
        var size = kind switch
        {
            EntryKind.File => info is FileInfo file ? file.Length : 0,
            EntryKind.Symlink => Encoding.UTF8.GetByteCount(linkTarget!),
            _ => 0,
        };
        var mode = info.UnixFileMode; // -1 when unknown, e.g. for a broken link
        return new FileEntry(path, kind, size,
            Modified: new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
            Created: new DateTimeOffset(info.CreationTimeUtc, TimeSpan.Zero),
            Mode: (int)mode < 0 ? null : mode,
            IsHidden: attributes.HasFlag(FileAttributes.Hidden),
            LinkTarget: linkTarget,
            LinkPointsToDirectory: kind == EntryKind.Symlink && attributes.HasFlag(FileAttributes.Directory));
    }

    /// <summary>An open directory listing plus the broker scope that covers it.</summary>
    private sealed class Listing : IDisposable
    {
        private readonly string _path;
        private readonly IDisposable _access;
        private readonly IEnumerator<FileSystemInfo> _entries;

        private Listing(string path, IDisposable access, IEnumerator<FileSystemInfo> entries)
        {
            _path = path;
            _access = access;
            _entries = entries;
        }

        public static Listing Open(IFileAccessBroker broker, string path) => Translate(path, () =>
        {
            var access = broker.Access(path);
            try
            {
                var entries = new DirectoryInfo(path).EnumerateFileSystemInfos("*", ListingOptions).GetEnumerator();
                return new Listing(path, access, entries);
            }
            catch
            {
                access.Dispose();
                throw;
            }
        });

        /// <summary>Reads up to <paramref name="max"/> entries; fewer means the listing is exhausted.</summary>
        public List<FileEntry> Next(VfsPath directory, int max) => Translate(_path, () =>
        {
            var batch = new List<FileEntry>(max);
            while (batch.Count < max && _entries.MoveNext())
            {
                var info = _entries.Current;
                batch.Add(ToEntry(directory.Combine(info.Name), info));
            }
            return batch;
        });

        public void Dispose()
        {
            _entries.Dispose();
            _access.Dispose();
        }
    }

    private sealed class DirectoryWatch : IDisposable
    {
        private readonly Lock _lock = new();
        private readonly IDisposable _access;
        private readonly Action _changed;
        private readonly FileSystemWatcher _watcher;
        private readonly Timer _timer;
        private bool _pending;
        private bool _disposed;

        public DirectoryWatch(string path, IDisposable access, Action changed)
        {
            _access = access;
            _changed = changed;
            _timer = new Timer(_ => Fire());
            _watcher = new FileSystemWatcher(path)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Attributes |
                               NotifyFilters.Size | NotifyFilters.LastWrite | NotifyFilters.CreationTime |
                               NotifyFilters.Security,
            };
            _watcher.Created += (_, _) => Schedule();
            _watcher.Deleted += (_, _) => Schedule();
            _watcher.Changed += (_, _) => Schedule();
            _watcher.Renamed += (_, _) => Schedule();
            _watcher.Error += (_, _) => Schedule(); // e.g. overflow: the listing must be re-read anyway
            _watcher.EnableRaisingEvents = true;
        }

        private void Schedule()
        {
            lock (_lock)
            {
                if (_disposed || _pending) return;
                _pending = true;
                _timer.Change(WatchCoalesceDelay, Timeout.InfiniteTimeSpan);
            }
        }

        private void Fire()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _pending = false;
            }
            _changed();
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
            }
            _watcher.Dispose();
            _timer.Dispose();
            _access.Dispose();
        }
    }
}
