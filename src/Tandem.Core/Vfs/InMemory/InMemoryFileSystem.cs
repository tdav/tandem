using System.Runtime.CompilerServices;
using System.Text;

namespace Tandem.Core.Vfs.InMemory;

/// <summary>
/// A thread-safe, in-memory <see cref="IFileSystemProvider"/> for tests, with helpers to arrange content and
/// inject faults. Symlinks are followed only by <see cref="EnumerateAsync"/> and <see cref="OpenReadAsync"/>
/// when the link is the last path segment; links in the middle of a path are not resolved.
/// </summary>
public sealed class InMemoryFileSystem : IFileSystemProvider
{
    private const UnixFileMode DefaultFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
    private const UnixFileMode DefaultDirectoryMode = DefaultFileMode |
        UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
    private const int MaxLinkHops = 32;

    private readonly Lock _lock = new();
    private readonly Dictionary<string, Node> _nodes = new(StringComparer.Ordinal);
    private readonly List<(VfsPath Prefix, string Key)> _volumes = [];
    private readonly List<(Func<string, VfsPath, bool> When, Func<Exception> Error)> _failures = [];
    private readonly Dictionary<string, (long Bytes, Func<Exception> Error)> _writeFailures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int Reads, int NthRead, byte[] Content)> _readChanges = new(StringComparer.Ordinal);
    private readonly List<Watcher> _watchers = [];

    public InMemoryFileSystem(string id = "mem")
    {
        Id = id;
        _nodes["/"] = Node.Directory(DateTimeOffset.UtcNow);
    }

    public string Id { get; }

    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Write | ProviderCapabilities.UnixPermissions |
        ProviderCapabilities.Symlinks | ProviderCapabilities.Watch;

    // ---- Arrange / inspect helpers -------------------------------------------------------------------

    /// <summary>Adds (or replaces) a UTF-8 text file, creating missing parent directories.</summary>
    public void AddFile(string path, string content = "", DateTimeOffset? modified = null) =>
        AddFile(path, Encoding.UTF8.GetBytes(content), modified);

    /// <summary>Adds (or replaces) a file, creating missing parent directories.</summary>
    public void AddFile(string path, byte[] content, DateTimeOffset? modified = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        var p = ToPath(path);
        lock (_lock)
        {
            EnsureDirectories(p.Parent!.Value);
            if (_nodes.TryGetValue(p.Path, out var existing) && existing.Kind == EntryKind.Directory)
                throw new VfsAlreadyExistsException($"'{p.Path}' is a directory.");
            var node = Node.File(modified ?? DateTimeOffset.UtcNow);
            node.Data!.Write(content);
            _nodes[p.Path] = node;
        }
    }

    /// <summary>Adds a directory and any missing parents.</summary>
    public void AddDirectory(string path)
    {
        var p = ToPath(path);
        lock (_lock) EnsureDirectories(p);
    }

    public bool Exists(string path)
    {
        var p = ToPath(path);
        lock (_lock) return _nodes.ContainsKey(p.Path);
    }

    public string ReadAllText(string path)
    {
        var p = ToPath(path);
        lock (_lock)
        {
            var node = Find(p);
            if (node.Kind != EntryKind.File) throw new VfsException($"'{p.Path}' is not a file.");
            return Encoding.UTF8.GetString(node.Data!.ToArray());
        }
    }

    /// <summary>Every path except the root, sorted ordinally.</summary>
    public IReadOnlyList<string> AllPaths
    {
        get
        {
            lock (_lock)
                return _nodes.Keys.Where(k => k != "/").Order(StringComparer.Ordinal).ToList();
        }
    }

    /// <summary>Paths at or under <paramref name="pathPrefix"/> report <paramref name="key"/> as volume key
    /// (the longest matching prefix wins). Everything else is on volume "mem".</summary>
    public void SetVolumeKey(string pathPrefix, string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var p = ToPath(pathPrefix);
        lock (_lock) _volumes.Add((p, key));
    }

    /// <summary>Makes an operation throw <paramref name="error"/>() when <paramref name="when"/> returns true.
    /// The operation name is the interface method name without "Async" (e.g. "OpenRead"); Move checks
    /// the source and then the destination.</summary>
    public void FailOn(Func<string, VfsPath, bool> when, Func<Exception> error)
    {
        ArgumentNullException.ThrowIfNull(when);
        ArgumentNullException.ThrowIfNull(error);
        lock (_lock) _failures.Add((when, error));
    }

    /// <summary>Streams opened for writing <paramref name="path"/> accept <paramref name="bytes"/> bytes,
    /// then throw <paramref name="error"/>(); the accepted bytes stay in the file.</summary>
    public void FailWriteAfter(string path, long bytes, Func<Exception> error)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        ArgumentNullException.ThrowIfNull(error);
        var p = ToPath(path);
        lock (_lock) _writeFailures[p.Path] = (bytes, error);
    }

    /// <summary>The <paramref name="nthRead"/>-th <see cref="OpenReadAsync"/> of <paramref name="path"/> from now on
    /// (counting from 1) returns <paramref name="content"/> instead of the file's bytes; the stored file and every
    /// other read are unchanged. Simulates a source that changes between reads.</summary>
    public void ChangeContentOnRead(string path, int nthRead, byte[] content)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(nthRead, 1);
        ArgumentNullException.ThrowIfNull(content);
        var p = ToPath(path);
        lock (_lock) _readChanges[p.Path] = (0, nthRead, content.ToArray());
    }

    /// <summary>Notifies the watchers of <paramref name="directory"/>.</summary>
    public void RaiseChanged(string directory)
    {
        var p = ToPath(directory);
        Watcher[] targets;
        lock (_lock) targets = _watchers.Where(w => w.Directory == p).ToArray();
        foreach (var watcher in targets) watcher.Changed();
    }

    // ---- IFileSystemProvider ------------------------------------------------------------------------

    public async IAsyncEnumerable<FileEntry> EnumerateAsync(VfsPath directory,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Check("Enumerate", directory);
        List<FileEntry> entries;
        lock (_lock)
        {
            var target = ResolveLinks(directory);
            if (Find(target).Kind != EntryKind.Directory)
                throw new VfsException($"'{directory.Path}' is not a directory.");
            entries = _nodes
                .Where(kv => kv.Key != "/" && ParentOf(kv.Key) == target.Path)
                .Select(kv => ToEntry(directory.Combine(NameOf(kv.Key)), kv.Value))
                .ToList();
        }

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            yield return entry;
        }
        await Task.CompletedTask;
    }

    public Task<FileEntry?> GetEntryAsync(VfsPath path, CancellationToken ct) => Run(ct, () =>
    {
        Check("GetEntry", path);
        lock (_lock)
            return _nodes.TryGetValue(path.Path, out var node) ? ToEntry(path, node) : null;
    });

    public Task<Stream> OpenReadAsync(VfsPath path, CancellationToken ct) => Run<Stream>(ct, () =>
    {
        Check("OpenRead", path);
        lock (_lock)
        {
            var node = Find(ResolveLinks(path));
            if (node.Kind != EntryKind.File) throw new VfsException($"'{path.Path}' is not a file.");
            if (_readChanges.TryGetValue(path.Path, out var change))
            {
                _readChanges[path.Path] = change with { Reads = change.Reads + 1 };
                if (change.Reads + 1 == change.NthRead) return new MemoryStream(change.Content.ToArray(), writable: false);
            }
            return new MemoryStream(node.Data!.ToArray(), writable: false);
        }
    });

    public Task<Stream> OpenWriteAsync(VfsPath path, CancellationToken ct) => Run<Stream>(ct, () =>
    {
        Check("OpenWrite", path);
        lock (_lock)
        {
            RequireDirectory(path.Parent ?? throw new VfsException("Cannot write to the root."));
            if (_nodes.TryGetValue(path.Path, out var existing) && existing.Kind == EntryKind.Directory)
                throw new VfsException($"'{path.Path}' is a directory.");
            var node = Node.File(DateTimeOffset.UtcNow);
            _nodes[path.Path] = node;
            var limit = _writeFailures.TryGetValue(path.Path, out var failure) ? failure : ((long, Func<Exception>)?)null;
            return new WriteStream(this, node, limit);
        }
    });

    public Task CreateDirectoryAsync(VfsPath path, CancellationToken ct) => Run(ct, () =>
    {
        Check("CreateDirectory", path);
        lock (_lock) EnsureDirectories(path);
    });

    public Task DeleteAsync(VfsPath path, bool recursive, CancellationToken ct) => Run(ct, () =>
    {
        Check("Delete", path);
        lock (_lock)
        {
            if (path.IsRoot) throw new VfsAccessDeniedException("Cannot delete the root.");
            var node = Find(path);
            var descendants = DescendantKeys(path.Path);
            if (node.Kind == EntryKind.Directory && descendants.Count > 0 && !recursive)
                throw new VfsException($"Directory '{path.Path}' is not empty.");
            foreach (var key in descendants) _nodes.Remove(key);
            _nodes.Remove(path.Path);
        }
    });

    public Task MoveAsync(VfsPath source, VfsPath destination, CancellationToken ct) => Run(ct, () =>
    {
        Check("Move", source);
        Check("Move", destination);
        lock (_lock)
        {
            var node = Find(source);
            if (_nodes.ContainsKey(destination.Path))
                throw new VfsAlreadyExistsException($"'{destination.Path}' already exists.");
            if (destination.IsSameOrDescendantOf(source))
                throw new VfsException($"Cannot move '{source.Path}' into itself.");
            RequireDirectory(destination.Parent!.Value);

            foreach (var key in DescendantKeys(source.Path))
            {
                var moved = _nodes[key];
                _nodes.Remove(key);
                _nodes[destination.Path + key[source.Path.Length..]] = moved;
            }
            _nodes.Remove(source.Path);
            _nodes[destination.Path] = node;
        }
    });

    public Task CreateSymlinkAsync(VfsPath path, string target, CancellationToken ct) => Run(ct, () =>
    {
        ArgumentException.ThrowIfNullOrEmpty(target);
        Check("CreateSymlink", path);
        lock (_lock)
        {
            if (_nodes.ContainsKey(path.Path)) throw new VfsAlreadyExistsException($"'{path.Path}' already exists.");
            RequireDirectory(path.Parent ?? throw new VfsAlreadyExistsException("The root already exists."));
            _nodes[path.Path] = Node.Symlink(target, DateTimeOffset.UtcNow);
        }
    });

    public Task SetModifiedAsync(VfsPath path, DateTimeOffset modified, CancellationToken ct) => Run(ct, () =>
    {
        Check("SetModified", path);
        lock (_lock) Find(path).Modified = modified;
    });

    public Task SetModeAsync(VfsPath path, UnixFileMode mode, CancellationToken ct) => Run(ct, () =>
    {
        Check("SetMode", path);
        lock (_lock) Find(path).Mode = mode;
    });

    public Task<string> GetVolumeKeyAsync(VfsPath path, CancellationToken ct) => Run(ct, () =>
    {
        Check("GetVolumeKey", path);
        lock (_lock)
        {
            return _volumes
                .Where(v => path.IsSameOrDescendantOf(v.Prefix))
                .OrderByDescending(v => v.Prefix.Path.Length)
                .Select(v => v.Key)
                .FirstOrDefault() ?? "mem";
        }
    });

    public IDisposable? Watch(VfsPath directory, Action changed)
    {
        ArgumentNullException.ThrowIfNull(changed);
        Check("Watch", directory);
        var watcher = new Watcher(this, directory, changed);
        lock (_lock) _watchers.Add(watcher);
        return watcher;
    }

    // ---- Internals (callers of the lock-free helpers below hold _lock) ------------------------------

    private VfsPath ToPath(string path) => new(Id, path);

    private void Check(string operation, VfsPath path)
    {
        if (!string.Equals(path.ProviderId, Id, StringComparison.Ordinal))
            throw new ArgumentException($"Path '{path}' does not belong to provider '{Id}'.", nameof(path));

        (Func<string, VfsPath, bool> When, Func<Exception> Error)[] failures;
        lock (_lock) failures = _failures.ToArray();
        foreach (var (when, error) in failures)
            if (when(operation, path))
                throw error();
    }

    private static Task<T> Run<T>(CancellationToken ct, Func<T> body)
    {
        if (ct.IsCancellationRequested) return Task.FromCanceled<T>(ct);
        try
        {
            return Task.FromResult(body());
        }
        catch (Exception e)
        {
            return Task.FromException<T>(e);
        }
    }

    private static Task Run(CancellationToken ct, Action body) => Run<object?>(ct, () =>
    {
        body();
        return null;
    });

    private Node Find(VfsPath path) =>
        _nodes.TryGetValue(path.Path, out var node) ? node : throw new VfsNotFoundException($"'{path.Path}' does not exist.");

    private void RequireDirectory(VfsPath path)
    {
        if (Find(path).Kind != EntryKind.Directory)
            throw new VfsException($"'{path.Path}' is not a directory.");
    }

    private void EnsureDirectories(VfsPath path)
    {
        if (_nodes.TryGetValue(path.Path, out var node))
        {
            if (node.Kind != EntryKind.Directory)
                throw new VfsAlreadyExistsException($"'{path.Path}' exists and is not a directory.");
            return;
        }
        EnsureDirectories(path.Parent!.Value);
        _nodes[path.Path] = Node.Directory(DateTimeOffset.UtcNow);
    }

    /// <summary>Follows a symlink at <paramref name="path"/> (and links it points to) to the final path.</summary>
    private VfsPath ResolveLinks(VfsPath path)
    {
        for (var hop = 0; hop < MaxLinkHops; hop++)
        {
            if (!_nodes.TryGetValue(path.Path, out var node) || node.Kind != EntryKind.Symlink) return path;
            path = LinkDestination(path, node.LinkTarget!) ?? throw new VfsNotFoundException($"Broken link '{path.Path}'.");
        }
        throw new VfsException($"Too many levels of symbolic links at '{path.Path}'.");
    }

    private VfsPath? LinkDestination(VfsPath link, string target)
    {
        try
        {
            return target.StartsWith('/') ? ToPath(target) : ToPath(link.Parent!.Value.Path + "/" + target);
        }
        catch (ArgumentException)
        {
            return null; // the target climbs above the root
        }
    }

    private List<string> DescendantKeys(string path)
    {
        var prefix = path == "/" ? "/" : path + "/";
        return _nodes.Keys.Where(k => k != "/" && k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
    }

    private FileEntry ToEntry(VfsPath path, Node node)
    {
        var size = node.Kind switch
        {
            EntryKind.File => node.Data!.Length,
            EntryKind.Symlink => Encoding.UTF8.GetByteCount(node.LinkTarget!),
            _ => 0,
        };
        var pointsToDirectory = false;
        if (node.Kind == EntryKind.Symlink)
        {
            try
            {
                pointsToDirectory = _nodes.TryGetValue(ResolveLinks(path).Path, out var target) &&
                                    target.Kind == EntryKind.Directory;
            }
            catch (VfsException)
            {
                // broken or looping link: reported as not pointing to a directory
            }
        }
        return new FileEntry(path, node.Kind, size, node.Modified, node.Created, node.Mode,
            IsHidden: path.Name.StartsWith('.'), node.LinkTarget, pointsToDirectory);
    }

    private static string ParentOf(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash == 0 ? "/" : path[..slash];
    }

    private static string NameOf(string path) => path[(path.LastIndexOf('/') + 1)..];

    private sealed class Node
    {
        private Node(EntryKind kind, DateTimeOffset created, UnixFileMode mode)
        {
            Kind = kind;
            Created = created;
            Modified = created;
            Mode = mode;
        }

        public EntryKind Kind { get; }
        public MemoryStream? Data { get; private init; }
        public string? LinkTarget { get; private init; }
        public DateTimeOffset Created { get; }
        public DateTimeOffset Modified { get; set; }
        public UnixFileMode Mode { get; set; }

        public static Node File(DateTimeOffset modified) =>
            new(EntryKind.File, modified, DefaultFileMode) { Data = new MemoryStream() };

        public static Node Directory(DateTimeOffset created) => new(EntryKind.Directory, created, DefaultDirectoryMode);

        public static Node Symlink(string target, DateTimeOffset created) =>
            new(EntryKind.Symlink, created, DefaultDirectoryMode) { LinkTarget = target };
    }

    /// <summary>Appends straight into the file's node, so written bytes are visible immediately.</summary>
    private sealed class WriteStream(InMemoryFileSystem owner, Node node, (long Bytes, Func<Exception> Error)? limit)
        : Stream
    {
        private long _written;
        private bool _disposed;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => !_disposed;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => _written;
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var allowed = limit is { } l ? Math.Min(buffer.Length, Math.Max(0, l.Bytes - _written)) : buffer.Length;
            lock (owner._lock)
            {
                node.Data!.Seek(0, SeekOrigin.End);
                node.Data.Write(buffer[..(int)allowed]);
                node.Modified = DateTimeOffset.UtcNow;
            }
            _written += allowed;
            if (allowed < buffer.Length) throw limit!.Value.Error();
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                Write(buffer.Span);
                return ValueTask.CompletedTask;
            }
            catch (Exception e)
            {
                return ValueTask.FromException(e);
            }
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            _disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class Watcher(InMemoryFileSystem owner, VfsPath directory, Action changed) : IDisposable
    {
        public VfsPath Directory { get; } = directory;
        public Action Changed { get; } = changed;

        public void Dispose()
        {
            lock (owner._lock) owner._watchers.Remove(this);
        }
    }
}
