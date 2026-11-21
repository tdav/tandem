using System.Runtime.CompilerServices;
using System.Text;
using Tandem.Core.Vfs;
using Tandem.Net.Remote;

namespace Tandem.Net.Tests.Remote;

/// <summary>
/// A server in memory for tests: one tree shared by every connection it hands out (<see cref="NewSession"/>,
/// <see cref="AsFactory"/>), with knobs for the failures of a real server. Written only on public types of
/// <c>Tandem.Net</c> and <c>Tandem.Core</c>, because the application's tests link this file too.
/// <para>A connection without <see cref="RemoteFeatures.Multiplexed"/> throws <see cref="InvalidOperationException"/>
/// on a second call while one is running or while a stream it opened is open: that is how tests catch a pool that
/// shares a connection it must not.</para>
/// <para>Call names (for <see cref="FailOn"/> and <see cref="Log"/>) are the <see cref="IRemoteSession"/> method names
/// without "Async": Connect, List, Stat, OpenRead, OpenWrite, CreateDirectory, DeleteFile, DeleteDirectory, Rename,
/// CreateSymlink, SetModified, SetMode.</para>
/// </summary>
public sealed class FakeRemoteServer
{
    private const int MaxLinkHops = 32;

    private readonly Lock _lock = new();
    private readonly Dictionary<string, Node> _nodes = new(StringComparer.Ordinal);
    private readonly List<Session> _open = [];
    private readonly List<(string Call, string Path, Func<Exception> Error)> _failures = [];
    private readonly List<string> _log = [];
    private int _sessionNumber;
    private int _connectCount;
    private int _maxOpenAtOnce;
    private long? _dropAfterBytes;
    private RemoteFeatures _features = RemoteFeatures.Symlinks | RemoteFeatures.Modes | RemoteFeatures.SetModified;
    private bool _offline;
    private bool _renameOverwrites;
    private bool _failCloseWrite;

    public FakeRemoteServer() => _nodes["/"] = Node.Directory();

    // ---- Knobs -------------------------------------------------------------------------------------------------

    /// <summary>What a connection declares; taken when it connects.</summary>
    public RemoteFeatures Features
    {
        get { lock (_lock) return _features; }
        set { lock (_lock) _features = value; }
    }

    /// <summary>While true, connecting fails with <see cref="VfsConnectionLostException"/>.</summary>
    public bool Offline
    {
        get { lock (_lock) return _offline; }
        set { lock (_lock) _offline = value; }
    }

    /// <summary>A rename onto an existing file replaces it (as FTP <c>RNTO</c> on many servers); when false it is
    /// refused with <see cref="VfsAlreadyExistsException"/>.</summary>
    public bool RenameOverwrites
    {
        get { lock (_lock) return _renameOverwrites; }
        set { lock (_lock) _renameOverwrites = value; }
    }

    /// <summary>Closing a write stream throws an <see cref="IOException"/> (a full disk); the bytes written stay in
    /// the file.</summary>
    public bool FailCloseWrite
    {
        get { lock (_lock) return _failCloseWrite; }
        set { lock (_lock) _failCloseWrite = value; }
    }

    /// <summary>One-shot: the next stream opened (read or write) passes this many bytes, then its connection dies and
    /// the stream throws <see cref="VfsConnectionLostException"/>. Written bytes stay in the file.</summary>
    public long? DropAfterBytes
    {
        get { lock (_lock) return _dropAfterBytes; }
        set { lock (_lock) _dropAfterBytes = value; }
    }

    /// <summary>The server closes every connection it has now (as after an idle time-out). The clients do not know
    /// yet: their next call (or stream read or write) throws <see cref="VfsConnectionLostException"/>. New
    /// connections open as usual.</summary>
    public void DropNow()
    {
        lock (_lock)
        {
            foreach (var session in _open) session.Dropped = true;
            _open.Clear();
            _log.Add("DropNow");
        }
    }

    /// <summary>Makes <paramref name="call"/> throw <paramref name="error"/>() (by default a
    /// <see cref="VfsAccessDeniedException"/>) when one of its paths is <paramref name="path"/>; for Rename both the
    /// old and the new path count.</summary>
    public void FailOn(string call, string path, Func<Exception>? error = null)
    {
        ArgumentNullException.ThrowIfNull(call);
        var normalized = Normalize(path);
        lock (_lock)
            _failures.Add((call, normalized,
                error ?? (() => new VfsAccessDeniedException($"{call} of '{normalized}' refused by the test."))));
    }

    public void ClearFailures()
    {
        lock (_lock) _failures.Clear();
    }

    /// <summary>Connections that connected successfully, in all.</summary>
    public int ConnectCount
    {
        get { lock (_lock) return _connectCount; }
    }

    /// <summary>Connections open now (connected, not closed by either side).</summary>
    public int OpenNow
    {
        get { lock (_lock) return _open.Count; }
    }

    /// <summary>The most connections that were open at the same time.</summary>
    public int MaxOpenAtOnce
    {
        get { lock (_lock) return _maxOpenAtOnce; }
    }

    /// <summary>Every call, as "#{connection} {call} {path}[ -> {path}]", plus "DropNow".</summary>
    public IReadOnlyList<string> Log
    {
        get { lock (_lock) return _log.ToList(); }
    }

    public IRemoteSession NewSession()
    {
        lock (_lock) return new Session(this, ++_sessionNumber);
    }

    public IRemoteSessionFactory AsFactory() => new Factory(this);

    // ---- Arrange / inspect ------------------------------------------------------------------------------------

    public void AddFile(string path, string content = "", DateTimeOffset? modified = null) =>
        AddFile(path, Encoding.UTF8.GetBytes(content), modified);

    public void AddFile(string path, byte[] content, DateTimeOffset? modified = null)
    {
        var p = Normalize(path);
        lock (_lock)
        {
            EnsureDirectories(ParentOf(p));
            var node = Node.File(modified ?? DateTimeOffset.UtcNow);
            node.Data!.Write(content);
            _nodes[p] = node;
        }
    }

    public void AddDirectory(string path)
    {
        var p = Normalize(path);
        lock (_lock) EnsureDirectories(p);
    }

    public void AddSymlink(string path, string target)
    {
        var p = Normalize(path);
        lock (_lock)
        {
            EnsureDirectories(ParentOf(p));
            _nodes[p] = Node.Symlink(target, DateTimeOffset.UtcNow);
        }
    }

    public bool Exists(string path)
    {
        var p = Normalize(path);
        lock (_lock) return _nodes.ContainsKey(p);
    }

    public string ReadAllText(string path)
    {
        var p = Normalize(path);
        lock (_lock)
        {
            if (!_nodes.TryGetValue(p, out var node) || node.Kind != EntryKind.File)
                throw new InvalidOperationException($"'{p}' is not a file on the fake server.");
            return Encoding.UTF8.GetString(node.Data!.ToArray());
        }
    }

    /// <summary>Every path except the root, sorted ordinally.</summary>
    public IReadOnlyList<string> AllPaths
    {
        get { lock (_lock) return _nodes.Keys.Where(k => k != "/").Order(StringComparer.Ordinal).ToList(); }
    }

    // ---- Tree (callers hold _lock) ---------------------------------------------------------------------------

    private static string Normalize(string path) => new VfsPath("fake", path).Path;

    private static string ParentOf(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash <= 0 ? "/" : path[..slash];
    }

    private static string NameOf(string path) => path == "/" ? "" : path[(path.LastIndexOf('/') + 1)..];

    private void EnsureDirectories(string path)
    {
        if (_nodes.TryGetValue(path, out var node))
        {
            if (node.Kind != EntryKind.Directory) throw new InvalidOperationException($"'{path}' is not a directory.");
            return;
        }
        EnsureDirectories(ParentOf(path));
        _nodes[path] = Node.Directory();
    }

    private Node Find(string path) =>
        _nodes.TryGetValue(path, out var node) ? node : throw new VfsNotFoundException($"'{path}' does not exist.");

    private void RequireDirectory(string path)
    {
        if (Find(path).Kind != EntryKind.Directory) throw new VfsException($"'{path}' is not a directory.");
    }

    private List<string> DescendantKeys(string path)
    {
        var prefix = path == "/" ? "/" : path + "/";
        return _nodes.Keys.Where(k => k != "/" && k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
    }

    private string? Resolve(string path)
    {
        for (var hop = 0; hop < MaxLinkHops; hop++)
        {
            if (!_nodes.TryGetValue(path, out var node)) return null;
            if (node.Kind != EntryKind.Symlink) return path;
            var target = node.LinkTarget!;
            try
            {
                path = Normalize(target.StartsWith('/') ? target : ParentOf(path) + "/" + target);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }
        return null;
    }

    private RemoteEntry ToEntry(string path, Node node, RemoteFeatures features)
    {
        var pointsToDirectory = node.Kind == EntryKind.Symlink && Resolve(path) is { } target &&
                                _nodes[target].Kind == EntryKind.Directory;
        return new RemoteEntry(NameOf(path), node.Kind, node.Kind == EntryKind.File ? node.Data!.Length : 0,
            node.Modified, features.HasFlag(RemoteFeatures.Modes) ? node.Mode : null, node.LinkTarget,
            pointsToDirectory);
    }

    private sealed class Node
    {
        private const UnixFileMode FileMode =
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

        public required EntryKind Kind { get; init; }
        public MemoryStream? Data { get; init; }
        public string? LinkTarget { get; init; }
        public DateTimeOffset Modified { get; set; }
        public UnixFileMode Mode { get; set; }

        public static Node File(DateTimeOffset modified) =>
            new() { Kind = EntryKind.File, Data = new MemoryStream(), Modified = modified, Mode = FileMode };

        public static Node Directory() => new()
        {
            Kind = EntryKind.Directory, Modified = DateTimeOffset.UtcNow,
            Mode = FileMode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute,
        };

        public static Node Symlink(string target, DateTimeOffset modified) =>
            new() { Kind = EntryKind.Symlink, LinkTarget = target, Modified = modified, Mode = FileMode };
    }

    private sealed class Factory(FakeRemoteServer server) : IRemoteSessionFactory
    {
        public IRemoteSession Create(ConnectionProfile profile, ICredentialSource credentials, ITrustDecisions trust) =>
            server.NewSession();
    }

    // ---- One connection ---------------------------------------------------------------------------------------

    private sealed class Session(FakeRemoteServer server, int number) : IRemoteSession
    {
        private readonly Lock _lock = server._lock; // one lock for the server and its connections
        private bool _connected;
        private bool _disposed;
        private int _busy;

        /// <summary>The server closed this connection; the client finds out at its next call.</summary>
        public bool Dropped { get; set; }

        public RemoteFeatures Features { get; private set; }

        public bool IsConnected
        {
            get { lock (_lock) return _connected; }
        }

        public async Task ConnectAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Yield();
            lock (_lock)
            {
                if (_disposed || _connected) throw new InvalidOperationException("Connect on a used connection.");
                server._log.Add($"#{number} Connect");
                if (server._offline) throw new VfsConnectionLostException("The fake server is offline.");
                _connected = true;
                Features = server._features;
                server._connectCount++;
                server._open.Add(this);
                server._maxOpenAtOnce = Math.Max(server._maxOpenAtOnce, server._open.Count);
            }
        }

        public async IAsyncEnumerable<RemoteEntry> ListAsync(string directory,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await EnterAsync("List", ct, directory);
            try
            {
                List<RemoteEntry> entries;
                lock (_lock)
                {
                    var path = Normalize(directory);
                    var target = server.Resolve(path) ?? throw new VfsNotFoundException($"'{path}' does not exist.");
                    server.RequireDirectory(target);
                    entries = server._nodes
                        .Where(kv => kv.Key != "/" && ParentOf(kv.Key) == target)
                        .Select(kv => server.ToEntry(kv.Key, kv.Value, Features))
                        .ToList();
                }
                foreach (var entry in entries)
                {
                    ct.ThrowIfCancellationRequested();
                    yield return entry;
                }
            }
            finally
            {
                Exit();
            }
        }

        public Task<RemoteEntry?> StatAsync(string path, CancellationToken ct) => RunAsync("Stat", ct, [path], () =>
        {
            var p = Normalize(path);
            return server._nodes.TryGetValue(p, out var node) ? server.ToEntry(p, node, Features) : null;
        });

        public async Task<Stream> OpenReadAsync(string path, CancellationToken ct)
        {
            await EnterAsync("OpenRead", ct, path);
            try
            {
                lock (_lock)
                {
                    var p = Normalize(path);
                    var target = server.Resolve(p) ?? throw new VfsNotFoundException($"'{p}' does not exist.");
                    var node = server.Find(target);
                    if (node.Kind != EntryKind.File) throw new VfsException($"'{p}' is not a file.");
                    var limit = server._dropAfterBytes;
                    server._dropAfterBytes = null;
                    return new ReadStream(this, node.Data!.ToArray(), limit); // stays busy until disposed
                }
            }
            catch
            {
                Exit();
                throw;
            }
        }

        public async Task<Stream> OpenWriteAsync(string path, CancellationToken ct)
        {
            await EnterAsync("OpenWrite", ct, path);
            try
            {
                lock (_lock)
                {
                    var p = Normalize(path);
                    server.RequireDirectory(ParentOf(p));
                    if (server._nodes.TryGetValue(p, out var existing) && existing.Kind == EntryKind.Directory)
                        throw new VfsException($"'{p}' is a directory.");
                    var node = Node.File(DateTimeOffset.UtcNow);
                    server._nodes[p] = node;
                    var limit = server._dropAfterBytes;
                    server._dropAfterBytes = null;
                    return new WriteStream(this, node, limit); // stays busy until disposed
                }
            }
            catch
            {
                Exit();
                throw;
            }
        }

        public Task CreateDirectoryAsync(string path, CancellationToken ct) => RunAsync("CreateDirectory", ct, [path],
            () =>
            {
                var p = Normalize(path);
                if (server._nodes.ContainsKey(p)) throw new VfsAlreadyExistsException($"'{p}' already exists.");
                server.RequireDirectory(ParentOf(p));
                server._nodes[p] = Node.Directory();
                return true;
            });

        public Task DeleteFileAsync(string path, CancellationToken ct) => RunAsync("DeleteFile", ct, [path], () =>
        {
            var p = Normalize(path);
            if (server.Find(p).Kind == EntryKind.Directory) throw new VfsException($"'{p}' is a directory.");
            server._nodes.Remove(p);
            return true;
        });

        public Task DeleteDirectoryAsync(string path, CancellationToken ct) => RunAsync("DeleteDirectory", ct, [path],
            () =>
            {
                var p = Normalize(path);
                if (p == "/") throw new VfsAccessDeniedException("The root cannot be deleted.");
                if (server.Find(p).Kind != EntryKind.Directory) throw new VfsException($"'{p}' is not a directory.");
                if (server.DescendantKeys(p).Count > 0) throw new VfsException($"'{p}' is not empty.");
                server._nodes.Remove(p);
                return true;
            });

        public Task RenameAsync(string from, string to, CancellationToken ct) => RunAsync("Rename", ct, [from, to], () =>
        {
            var source = Normalize(from);
            var destination = Normalize(to);
            var node = server.Find(source);
            server.RequireDirectory(ParentOf(destination));
            if (destination == source || destination.StartsWith(source + "/", StringComparison.Ordinal))
                throw new VfsException($"Cannot rename '{source}' into itself.");
            if (server._nodes.TryGetValue(destination, out var existing))
            {
                if (!server._renameOverwrites)
                    throw new VfsAlreadyExistsException($"'{destination}' already exists.");
                if (existing.Kind == EntryKind.Directory || node.Kind == EntryKind.Directory)
                    throw new VfsException($"Cannot replace '{destination}'.");
                server._nodes.Remove(destination);
            }
            foreach (var key in server.DescendantKeys(source))
            {
                var moved = server._nodes[key];
                server._nodes.Remove(key);
                server._nodes[destination + key[source.Length..]] = moved;
            }
            server._nodes.Remove(source);
            server._nodes[destination] = node;
            return true;
        });

        public Task CreateSymlinkAsync(string path, string target, CancellationToken ct) => RunAsync("CreateSymlink",
            ct, [path], () =>
            {
                if (!Features.HasFlag(RemoteFeatures.Symlinks))
                    throw new NotSupportedException("The fake server was set up without links.");
                var p = Normalize(path);
                if (server._nodes.ContainsKey(p)) throw new VfsAlreadyExistsException($"'{p}' already exists.");
                server.RequireDirectory(ParentOf(p));
                server._nodes[p] = Node.Symlink(target, DateTimeOffset.UtcNow);
                return true;
            });

        public Task SetModifiedAsync(string path, DateTimeOffset modified, CancellationToken ct) => RunAsync(
            "SetModified", ct, [path], () =>
            {
                var node = server.Find(Normalize(path));
                if (Features.HasFlag(RemoteFeatures.SetModified)) node.Modified = modified;
                return true;
            });

        public Task SetModeAsync(string path, UnixFileMode mode, CancellationToken ct) => RunAsync("SetMode", ct,
            [path], () =>
            {
                if (!Features.HasFlag(RemoteFeatures.Modes))
                    throw new NotSupportedException("The fake server was set up without modes.");
                server.Find(Normalize(path)).Mode = mode;
                return true;
            });

        public ValueTask DisposeAsync()
        {
            lock (_lock)
            {
                if (_disposed) return ValueTask.CompletedTask;
                _disposed = true;
                if (_connected) server._log.Add($"#{number} Close");
                _connected = false;
                server._open.Remove(this);
            }
            return ValueTask.CompletedTask;
        }

        // ---- Call bookkeeping --------------------------------------------------------------------------------

        private async Task<T> RunAsync<T>(string call, CancellationToken ct, string[] paths, Func<T> body)
        {
            await EnterAsync(call, ct, paths);
            try
            {
                lock (_lock) return body();
            }
            finally
            {
                Exit();
            }
        }

        /// <summary>Starts a call: checks the connection, the one-call rule, the failures to inject; then yields, so
        /// that the call is really in flight while others may start.</summary>
        private async Task EnterAsync(string call, CancellationToken ct, params string[] paths)
        {
            ct.ThrowIfCancellationRequested();
            lock (_lock)
            {
                if (_disposed || (!_connected && !Dropped))
                    throw new InvalidOperationException($"{call} on a connection that is not connected.");
                var normalized = paths.Select(Normalize).ToArray();
                server._log.Add($"#{number} {call} {string.Join(" -> ", normalized)}");
                CheckAliveLocked();
                if (_busy > 0 && !Features.HasFlag(RemoteFeatures.Multiplexed))
                    throw new InvalidOperationException(
                        $"{call} while connection #{number} is busy: it handles one call at a time.");
                foreach (var (failCall, failPath, error) in server._failures)
                    if (failCall == call && normalized.Contains(failPath))
                        throw error();
                _busy++;
            }
            try
            {
                await Task.Yield();
                ct.ThrowIfCancellationRequested();
            }
            catch
            {
                Exit();
                throw;
            }
        }

        public void Exit()
        {
            lock (_lock) _busy--;
        }

        /// <summary>Caller holds the lock.</summary>
        public void CheckAliveLocked()
        {
            if (!Dropped) return;
            _connected = false;
            throw new VfsConnectionLostException($"Connection #{number} was closed by the server.");
        }

        /// <summary>Caller holds the lock.</summary>
        public void DieLocked()
        {
            Dropped = true;
            server._open.Remove(this);
            server._log.Add($"#{number} Dropped");
        }

        public Lock SyncRoot => _lock;

        public bool FailCloseWrite => server._failCloseWrite;
    }

    private sealed class ReadStream(Session session, byte[] content, long? dropAfter) : Stream
    {
        private long _position;
        private bool _closed;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            lock (session.SyncRoot)
            {
                ObjectDisposedException.ThrowIf(_closed, this);
                session.CheckAliveLocked();
                var allowed = content.Length - _position;
                if (dropAfter is { } limit)
                {
                    if (_position >= limit)
                    {
                        session.DieLocked();
                        session.CheckAliveLocked();
                    }
                    allowed = Math.Min(allowed, limit - _position);
                }
                var n = (int)Math.Min(buffer.Length, allowed);
                content.AsSpan((int)_position, n).CopyTo(buffer);
                _position += n;
                return n;
            }
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Yield();
            return Read(buffer.Span);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (!_closed)
            {
                _closed = true;
                session.Exit();
            }
            base.Dispose(disposing);
        }
    }

    private sealed class WriteStream(Session session, Node node, long? dropAfter) : Stream
    {
        private long _written;
        private bool _closed;
        private bool _failed;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _written; set => throw new NotSupportedException(); }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            lock (session.SyncRoot)
            {
                ObjectDisposedException.ThrowIf(_closed, this);
                try
                {
                    session.CheckAliveLocked();
                    var take = buffer.Length;
                    if (dropAfter is { } limit && _written + take > limit)
                    {
                        take = (int)Math.Max(0, limit - _written);
                        node.Data!.Write(buffer[..take]);
                        _written += take;
                        session.DieLocked();
                        session.CheckAliveLocked();
                    }
                    node.Data!.Write(buffer);
                    _written += take;
                    node.Modified = DateTimeOffset.UtcNow;
                }
                catch
                {
                    _failed = true;
                    throw;
                }
            }
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Yield();
            Write(buffer.Span);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override void Flush() { }
        public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        /// <summary>Ends the transfer; with <see cref="FakeRemoteServer.FailCloseWrite"/> the server's answer is an
        /// error, thrown here (after the connection is free again).</summary>
        protected override void Dispose(bool disposing)
        {
            if (_closed)
            {
                base.Dispose(disposing);
                return;
            }
            _closed = true;
            bool fail;
            lock (session.SyncRoot) fail = !_failed && !session.Dropped && session.FailCloseWrite;
            session.Exit();
            base.Dispose(disposing);
            if (fail) throw new VfsException("The server refused to finish the upload: the disk is full.");
        }

        public override ValueTask DisposeAsync()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
            return ValueTask.CompletedTask;
        }
    }
}
