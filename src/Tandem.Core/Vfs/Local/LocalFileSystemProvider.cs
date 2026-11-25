using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Tandem.Core.Platform;

namespace Tandem.Core.Vfs.Local;

/// <summary>
/// The local disk. A <see cref="VfsPath"/> of provider "local" maps one-to-one onto an absolute OS path.
/// Every disk access runs on the thread pool inside a <see cref="IFileAccessBroker.Access"/> scope, and .NET
/// access/not-found errors are reported as <see cref="VfsAccessDeniedException"/>/<see cref="VfsNotFoundException"/>.
/// Symlinks are followed only by <see cref="EnumerateAsync"/> and <see cref="OpenReadAsync"/> (the OS resolves
/// links anywhere in a path, unlike the in-memory provider).
/// <para>Renames go through <see cref="IExclusiveRename"/> when one is given, so an existing destination is refused
/// by the system in the same call that renames; without one (or on a volume that has no exclusive rename) the
/// destination is checked first and then renamed, which leaves a moment for another process to create it.</para>
/// </summary>
public sealed class LocalFileSystemProvider(IFileAccessBroker broker, IExclusiveRename? exclusiveRename = null,
    IEntryIdentity? identity = null)
    : IFileSystemProvider, ILinkResolver
{
    private static readonly bool Windows = LocalPath.Current == LocalPathStyle.Windows;

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
    private readonly IExclusiveRename? _exclusiveRename = exclusiveRename;
    private readonly IEntryIdentity _identity =
        identity ?? (OperatingSystem.IsWindows() ? new WindowsEntryIdentity() : new UnixEntryIdentity());

    public string Id => VfsPath.LocalProviderId;

    /// <summary>On Windows without <see cref="ProviderCapabilities.UnixPermissions"/>.</summary>
    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Write | ProviderCapabilities.Trash | ProviderCapabilities.Symlinks |
        ProviderCapabilities.Watch | (Windows ? 0 : ProviderCapabilities.UnixPermissions);

    /// <summary>Yields entries in batches read on the thread pool, so a huge directory shows up progressively;
    /// the broker scope stays open until the enumeration ends.</summary>
    /// <para>On Windows the root <c>/</c> lists the ready drives as directories named <c>C:</c>.</para>
    public async IAsyncEnumerable<FileEntry> EnumerateAsync(VfsPath directory,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (Windows && directory.IsRoot && string.Equals(directory.ProviderId, Id, StringComparison.Ordinal))
        {
            foreach (var drive in await Task.Run(DriveEntries, ct))
            {
                ct.ThrowIfCancellationRequested();
                yield return drive;
            }
            yield break;
        }
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

    public Task<FileEntry?> GetEntryAsync(VfsPath path, CancellationToken ct) => Windows && path.IsRoot
        ? Task.FromResult<FileEntry?>(RootEntry(path))
        : RunAsync(path, ct, p =>
    {
        var info = new FileInfo(p);
        return Exists(info) ? ToEntry(path, info) : null;
    });

    private const int MaxLinkHops = 40;

    /// <summary>As realpath(3) does, without needing the end to exist: walks the path name by name from the root,
    /// replacing every link met by its target, so ".." always steps out of a real folder.</summary>
    public Task<VfsPath> ResolveLinksAsync(VfsPath path, CancellationToken ct) => Windows && path.IsRoot
        ? Task.FromResult(path)
        : RunAsync(path, ct, _ =>
    {
        // Walks the VFS form (on macOS the OS path itself); each prefix is converted for the system call.
        var pending = new Stack<string>(path.Path.Split('/', StringSplitOptions.RemoveEmptyEntries).Reverse());
        var current = "/";
        var hops = 0;
        while (pending.TryPop(out var name))
        {
            ct.ThrowIfCancellationRequested();
            if (name == ".") continue;
            if (name == "..")
            {
                var slash = current.LastIndexOf('/');
                current = slash <= 0 ? "/" : current[..slash];
                continue;
            }
            var next = current == "/" ? "/" + name : current + "/" + name;
            string? target;
            if (Windows && current == "/") target = null; // a drive or the UNC segment
            else
            {
                var os = LocalPath.ToOs(next, LocalPath.Current);
                using (_broker.Access(os)) target = new FileInfo(os).LinkTarget;
            }
            if (target is null)
            {
                current = next;
                continue;
            }
            if (++hops > MaxLinkHops) throw new VfsException($"Too many levels of symbolic links at '{path.Path}'.");
            if (Path.IsPathRooted(target))
            {
                target = LocalPath.FromOs(target, LocalPath.Current);
                current = "/";
            }
            var separators = Windows ? new[] { '/', '\\' } : ['/'];
            foreach (var part in target.Split(separators, StringSplitOptions.RemoveEmptyEntries).Reverse())
                pending.Push(part);
        }
        return VfsPath.Local(current);
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
        if (path.IsRoot || LocalPath.IsDriveRoot(path.Path))
            throw new VfsAccessDeniedException("Cannot delete the root.");
        if (IsRealDirectory(info))
            Directory.Delete(p, recursive); // does not follow links inside the tree
        else
            File.Delete(p); // a file or a link (the link itself, never its target)
    });

    /// <summary>A destination that is the source itself under another spelling of its name (letter case or Unicode
    /// normalization, on a volume that ignores the difference) is a plain rename; any other existing destination is
    /// refused with <see cref="VfsAlreadyExistsException"/>.</summary>
    public Task MoveAsync(VfsPath source, VfsPath destination, CancellationToken ct) => RunAsync(source, ct, src =>
    {
        var dst = ToOsPath(destination);
        Translate(dst, () =>
        {
            using var access = _broker.Access(dst);
            if (IsRespelling(src, dst))
            {
                if (_exclusiveRename is null)
                    Directory.Move(src, dst); // accepts a change of letter case only
                else
                    _exclusiveRename.RenameSameEntry(src, dst);
                return true;
            }
            if (_exclusiveRename is not null)
            {
                try
                {
                    _exclusiveRename.Rename(src, dst);
                    return true;
                }
                catch (VfsAlreadyExistsException e)
                {
                    // The system's message is only "File exists"; name the destination.
                    throw new VfsAlreadyExistsException($"'{dst}' already exists.", e);
                }
                catch (NotSupportedException)
                {
                    // The volume has no exclusive rename (nothing has changed): check, then rename.
                }
            }
            if (Exists(new FileInfo(dst))) throw new VfsAlreadyExistsException($"'{dst}' already exists.");
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
        return _identity.Get(first) is { } x && _identity.Get(second) is { } y && x == y;
    });

    /// <summary>The mount point of the longest mounted volume that contains the path (links are not resolved).</summary>
    public Task<string> GetVolumeKeyAsync(VfsPath path, CancellationToken ct) => Windows && path.IsRoot
        ? Task.FromResult("/")
        : RunAsync(path, ct, _ =>
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
        if (Windows && directory.IsRoot) return null; // the drive list: nothing on disk to watch
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
        return LocalPath.ToOs(path.Path, LocalPath.Current); // strict: "/" and made-up paths are no place on disk
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

    /// <summary>True when the entry itself exists (a broken link counts); <see cref="FileSystemInfo.Attributes"/>
    /// is -1 for a missing path.</summary>
    private static bool Exists(FileSystemInfo info) => (int)info.Attributes != -1;

    private static bool IsRealDirectory(FileSystemInfo info) =>
        info.Attributes.HasFlag(FileAttributes.Directory) && !IsLink(info);

    /// <summary>A symbolic link (on Windows also a junction). On Windows other reparse points — OneDrive's
    /// on-demand files and folders, deduplicated files — are ordinary entries: they have no link target.</summary>
    private static bool IsLink(FileSystemInfo info) =>
        info.Attributes.HasFlag(FileAttributes.ReparsePoint) && (!Windows || ReadLinkTarget(info) is not null);

    /// <summary>True when <paramref name="destination"/> names the entry <paramref name="source"/> itself (same device
    /// and inode, <c>lstat</c>) in the same directory (the parents compared the same way, but with links to them
    /// resolved) by a name that differs from
    /// its own, but only in letter case or Unicode normalization. The name and parent tests keep a hard link of the
    /// source — under another name, or under an equivalent name in another directory, where a plain <c>rename(2)</c>
    /// between two links of one file does nothing and reports success — from counting as the source, and a move onto
    /// the very same name is still refused as existing.</summary>
    private bool IsRespelling(string source, string destination)
    {
        var sourceName = Path.GetFileName(source);
        var destinationName = Path.GetFileName(destination);
        return !string.Equals(sourceName, destinationName, StringComparison.Ordinal) &&
               string.Equals(sourceName.Normalize(NormalizationForm.FormC),
                   destinationName.Normalize(NormalizationForm.FormC), StringComparison.OrdinalIgnoreCase) &&
               _identity.Get(destination) is { } target && _identity.Get(source) == target &&
               Path.GetDirectoryName(source) is { } sourceParent &&
               Path.GetDirectoryName(destination) is { } destinationParent &&
               _identity.Get(Resolved(sourceParent)) is { } parent &&
               _identity.Get(Resolved(destinationParent)) == parent;
    }

    /// <summary>The directory path with a trailing '/', so that <c>lstat</c> resolves a linked folder to the folder
    /// itself (a panel in "/tmp" names "/tmp/a" where the resolved destination is "/private/tmp/A").</summary>
    private static string Resolved(string directory) =>
        directory.EndsWith(Path.DirectorySeparatorChar) ? directory : directory + Path.DirectorySeparatorChar;

    private static FileEntry ToEntry(VfsPath path, FileSystemInfo info)
    {
        var attributes = info.Attributes;
        // The same test as DeleteAsync: a link is a link even when its target cannot be read (readlink refused):
        // it is then reported without a target, and never as the directory it may lead to.
        var isLink = IsLink(info);
        var linkTarget = isLink ? ReadLinkTarget(info) : null;
        var kind = isLink ? EntryKind.Symlink
            : attributes.HasFlag(FileAttributes.Directory) ? EntryKind.Directory
            : EntryKind.File;
        var size = kind switch
        {
            EntryKind.File => info is FileInfo file ? file.Length : 0,
            EntryKind.Symlink => linkTarget is null ? 0 : Encoding.UTF8.GetByteCount(linkTarget),
            _ => 0,
        };
        var mode = Windows ? (UnixFileMode)(-1) : info.UnixFileMode; // -1 when unknown, e.g. for a broken link
        return new FileEntry(path, kind, size,
            Modified: new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
            Created: new DateTimeOffset(info.CreationTimeUtc, TimeSpan.Zero),
            Mode: (int)mode < 0 ? null : mode,
            IsHidden: attributes.HasFlag(FileAttributes.Hidden),
            LinkTarget: linkTarget,
            LinkPointsToDirectory: kind == EntryKind.Symlink && attributes.HasFlag(FileAttributes.Directory));
    }

    /// <summary>Windows: the root itself, the virtual folder of drives.</summary>
    private static FileEntry RootEntry(VfsPath root) =>
        new(root, EntryKind.Directory, 0, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, null, false, null, false);

    /// <summary>Windows: the ready drives, as directories named <c>C:</c>.</summary>
    private static List<FileEntry> DriveEntries() =>
        DriveInfo.GetDrives()
            .Where(drive => drive.IsReady)
            .Select(drive => VfsPath.Local(drive.RootDirectory.FullName))
            .Where(path => LocalPath.IsDriveRoot(path.Path))
            .Select(path => new FileEntry(path, EntryKind.Directory, 0, DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch, null, false, null, false))
            .ToList();

    /// <summary>The link's target, or null when it cannot be read.</summary>
    private static string? ReadLinkTarget(FileSystemInfo info)
    {
        try
        {
            return info.LinkTarget;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
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
