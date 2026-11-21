using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Tandem.Core.Vfs;

namespace Tandem.Net.Remote;

/// <summary>
/// A server as a source of files, over connections of one protocol (<see cref="IRemoteSession"/>). Everything FTP and
/// SFTP share lives here: the pool of connections, reconnecting, refusing to replace a name on a rename, deleting a
/// tree, and the rest of the source contract.
/// <para><b>Connections.</b> Up to <see cref="MaxSessions"/>; a call takes a free one or opens a new one, and waits
/// (cancellably) when all are busy. An open stream keeps its connection until it is closed. A server that multiplexes
/// gets a single connection for everything.</para>
/// <para><b>Reconnecting.</b> A connection found dead at the start of a call (the server closed it while it sat
/// idle) is thrown away, a new one is opened, and the call is made once more: the user sees nothing. If the new one
/// cannot be opened either, or the connection breaks in the middle of a stream, the call throws
/// <see cref="VfsConnectionLostException"/>; the next call tries to connect again, so "reconnect and retry" is just
/// making the call again.</para>
/// <para><b>Host.</b> "Up" from the server's root leads to the folder the connection was opened from (with no
/// name to focus), so a saved tab on a server reopens there after a restart, without connecting.</para>
/// <para>No trash, no watching. Thread-safe.</para>
/// </summary>
public sealed class RemoteProvider : IFileSystemProvider, IHostedProvider, IAsyncDisposable
{
    public const int MaxSessions = 4;

    private readonly SessionPool _pool;
    private readonly HostLocation _origin;

    /// <param name="id">The provider id the paths carry.</param>
    /// <param name="newSession">Makes a connection that is not connected yet.</param>
    /// <param name="origin">The folder the connection was opened from: where "up" from the root leads. Its focus name
    /// is not kept.</param>
    public RemoteProvider(string id, ConnectionProfile profile, Func<IRemoteSession> newSession, HostLocation origin)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(newSession);
        Id = id;
        Profile = profile;
        _origin = origin with { FocusName = null };
        _pool = new SessionPool(newSession, MaxSessions);
        Root = new VfsPath(id, "/");
    }

    public string Id { get; }

    public ConnectionProfile Profile { get; }

    public VfsPath Root { get; }

    /// <summary>Whether any connection is open (in use or free).</summary>
    public bool HasOpenConnections => _pool.HasOpenConnections;

    internal bool SupportsModifiedTime => _pool.Features?.HasFlag(RemoteFeatures.SetModified) == true;

    /// <summary><see cref="ProviderCapabilities.Write"/> and <see cref="ProviderCapabilities.LocalCopyEdit"/>, plus
    /// <see cref="ProviderCapabilities.Symlinks"/> and <see cref="ProviderCapabilities.UnixPermissions"/> when the
    /// last connection declared <see cref="RemoteFeatures.Symlinks"/> and <see cref="RemoteFeatures.Modes"/>
    /// (unknown before the first connection).</summary>
    public ProviderCapabilities Capabilities
    {
        get
        {
            var capabilities = ProviderCapabilities.Write | ProviderCapabilities.LocalCopyEdit;
            if (_pool.Features is not { } features) return capabilities;
            if (features.HasFlag(RemoteFeatures.Symlinks)) capabilities |= ProviderCapabilities.Symlinks;
            if (features.HasFlag(RemoteFeatures.Modes)) capabilities |= ProviderCapabilities.UnixPermissions;
            return capabilities;
        }
    }

    /// <summary>Opens the first connection (unless one is open): questions about the password and the key are asked
    /// here.</summary>
    public async Task ConnectAsync(CancellationToken ct)
    {
        var lease = await _pool.AcquireAsync(ct).ConfigureAwait(false);
        lease.Release();
    }

    /// <summary>Closes the free connections now and those in use when they are given back. The next call
    /// connects again.</summary>
    public Task CloseConnectionsAsync() => _pool.CloseAllAsync();

    public ValueTask DisposeAsync() => _pool.DisposeAsync();

    // ---- IHostedProvider ----------------------------------------------------------------------------------------

    public HostLocation? GetHostLocation(VfsPath path) => path.IsRoot ? _origin : null;

    /// <summary><see cref="ConnectionProfile.Address"/> followed by the path, e.g. "sftp://user@host/var/www".</summary>
    public string GetDisplayPath(VfsPath path) => Profile.Address + path.Path;

    // ---- IFileSystemProvider ------------------------------------------------------------------------------------

    /// <summary>The whole listing is read first, then handed out, so that the connection is free again while the
    /// caller goes through it (and may make other calls).</summary>
    public async IAsyncEnumerable<FileEntry> EnumerateAsync(VfsPath directory,
        [EnumeratorCancellation] CancellationToken ct)
    {
        Require(directory);
        ct.ThrowIfCancellationRequested();
        var entries = await CallAsync(async (session, token) =>
        {
            var list = new List<RemoteEntry>();
            await foreach (var entry in session.ListAsync(directory.Path, token).ConfigureAwait(false))
                list.Add(entry);
            return list;
        }, ct).ConfigureAwait(false);

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            // A name that cannot be a path segment cannot be addressed either.
            if (entry.Name.Length == 0 || entry.Name is "." or ".." || entry.Name.Contains('/')) continue;
            yield return ToEntry(directory.Combine(entry.Name), entry);
        }
    }

    public async Task<FileEntry?> GetEntryAsync(VfsPath path, CancellationToken ct)
    {
        Require(path);
        ct.ThrowIfCancellationRequested();
        var entry = await StatAsync(path, ct).ConfigureAwait(false);
        return entry is null ? null : ToEntry(path, entry);
    }

    public async Task<Stream> OpenReadAsync(VfsPath path, CancellationToken ct)
    {
        Require(path);
        ct.ThrowIfCancellationRequested();
        return await OpenStreamAsync((session, token) => session.OpenReadAsync(path.Path, token), reading: true, ct)
            .ConfigureAwait(false);
    }

    /// <summary>A missing parent folder is <see cref="VfsNotFoundException"/>: when the server refuses the upload, the
    /// parent is looked up, and the refusal reported as "not found" if it is not there.</summary>
    public async Task<Stream> OpenWriteAsync(VfsPath path, CancellationToken ct)
    {
        Require(path);
        ct.ThrowIfCancellationRequested();
        var parent = path.Parent ?? throw new VfsAccessDeniedException("The root of a server cannot be written to.");
        try
        {
            return await OpenStreamAsync((session, token) => session.OpenWriteAsync(path.Path, token), reading: false,
                ct).ConfigureAwait(false);
        }
        catch (VfsException e) when (e is not (VfsNotFoundException or VfsConnectionLostException
                                         or NetAuthenticationException or NetTrustException))
        {
            RemoteEntry? parentEntry;
            try
            {
                parentEntry = await StatAsync(parent, ct).ConfigureAwait(false);
            }
            catch (Exception lookup) when (lookup is not OperationCanceledException)
            {
                ExceptionDispatchInfo.Throw(e); // the lookup did not tell: the server's own answer stands
                throw;
            }
            if (parentEntry is null)
                throw new VfsNotFoundException($"'{GetDisplayPath(parent)}' does not exist.", e);
            throw;
        }
    }

    /// <summary>Creates the missing ancestors too. An existing folder (or a link to one) is not an error; an existing
    /// file is <see cref="VfsAlreadyExistsException"/>.</summary>
    public async Task CreateDirectoryAsync(VfsPath path, CancellationToken ct)
    {
        Require(path);
        ct.ThrowIfCancellationRequested();
        if (await StatAsync(path, ct).ConfigureAwait(false) is { } existing)
        {
            RequireDirectory(path, existing);
            return;
        }
        if (path.Parent is { } parent) await CreateDirectoryAsync(parent, ct).ConfigureAwait(false);
        try
        {
            await CallAsync((session, token) => session.CreateDirectoryAsync(path.Path, token), ct)
                .ConfigureAwait(false);
        }
        catch (VfsAlreadyExistsException)
        {
            // Made by someone else in the meantime: fine if it is a folder.
            RequireDirectory(path, await StatAsync(path, ct).ConfigureAwait(false) ?? throw new VfsException(
                $"'{GetDisplayPath(path)}' was reported to exist, but is not there."));
        }
    }

    /// <summary>A file or link is deleted as it is (a link, not its target). A folder needs
    /// <paramref name="recursive"/> unless it is empty; with it, the tree is deleted bottom-up, links inside it
    /// deleted as files and not entered.</summary>
    public async Task DeleteAsync(VfsPath path, bool recursive, CancellationToken ct)
    {
        Require(path);
        ct.ThrowIfCancellationRequested();
        if (path.IsRoot) throw new VfsAccessDeniedException("The root of a server cannot be deleted.");
        var entry = await StatAsync(path, ct).ConfigureAwait(false)
                    ?? throw new VfsNotFoundException($"'{GetDisplayPath(path)}' does not exist.");
        if (entry.Kind != EntryKind.Directory)
        {
            await CallAsync((session, token) => session.DeleteFileAsync(path.Path, token), ct).ConfigureAwait(false);
            return;
        }
        if (recursive)
        {
            await DeleteTreeAsync(path, ct).ConfigureAwait(false);
            return;
        }
        if ((await ListAsync(path, ct).ConfigureAwait(false)).Count > 0)
            throw new VfsException($"'{GetDisplayPath(path)}' is not empty.");
        await CallAsync((session, token) => session.DeleteDirectoryAsync(path.Path, token), ct).ConfigureAwait(false);
    }

    /// <summary>Renames on the server. The destination is looked up first: if it exists the move is refused with
    /// <see cref="VfsAlreadyExistsException"/>, because many servers (FTP <c>RNTO</c>) would silently replace it.
    /// Only a lookup that says for certain "not there" lets the rename go ahead; a lookup that fails fails the move.
    /// Moving a path onto itself does nothing.</summary>
    public async Task MoveAsync(VfsPath source, VfsPath destination, CancellationToken ct)
    {
        Require(source);
        Require(destination);
        ct.ThrowIfCancellationRequested();
        if (source.IsRoot) throw new VfsAccessDeniedException("The root of a server cannot be moved.");
        if (source == destination)
        {
            if (await StatAsync(source, ct).ConfigureAwait(false) is null)
                throw new VfsNotFoundException($"'{GetDisplayPath(source)}' does not exist.");
            return;
        }
        if (destination.IsSameOrDescendantOf(source))
            throw new VfsException($"'{GetDisplayPath(source)}' cannot be moved into itself.");
        if (await StatAsync(destination, ct).ConfigureAwait(false) is not null)
            throw new VfsAlreadyExistsException($"'{GetDisplayPath(destination)}' already exists.");
        // Between this check and the rename another client can still create the destination, and the server may then
        // replace it: no protocol offers a rename that refuses an existing name, so this gap cannot be closed here.
        await CallAsync((session, token) => session.RenameAsync(source.Path, destination.Path, token), ct)
            .ConfigureAwait(false);
    }

    public async Task CreateSymlinkAsync(VfsPath path, string target, CancellationToken ct)
    {
        Require(path);
        ArgumentException.ThrowIfNullOrEmpty(target);
        ct.ThrowIfCancellationRequested();
        await CallAsync((session, token) => session.CreateSymlinkAsync(path.Path, target, token), ct)
            .ConfigureAwait(false);
    }

    /// <summary>Does nothing on a server that cannot set times.</summary>
    public async Task SetModifiedAsync(VfsPath path, DateTimeOffset modified, CancellationToken ct)
    {
        Require(path);
        ct.ThrowIfCancellationRequested();
        await CallAsync((session, token) => session.SetModifiedAsync(path.Path, modified, token), ct)
            .ConfigureAwait(false);
    }

    public async Task SetModeAsync(VfsPath path, UnixFileMode mode, CancellationToken ct)
    {
        Require(path);
        ct.ThrowIfCancellationRequested();
        await CallAsync((session, token) => session.SetModeAsync(path.Path, mode, token), ct).ConfigureAwait(false);
    }

    /// <summary>Both exist and the paths are equal: names on a server are taken as they are, and links are not
    /// followed.</summary>
    public async Task<bool> IsSameEntryAsync(VfsPath a, VfsPath b, CancellationToken ct)
    {
        Require(a);
        Require(b);
        ct.ThrowIfCancellationRequested();
        return a == b && await StatAsync(a, ct).ConfigureAwait(false) is not null;
    }

    /// <summary><see cref="ConnectionProfile.VolumeKey"/>, without the network: operations on one server queue up
    /// behind each other, and a server that cannot be reached does not hold up the queue.</summary>
    public Task<string> GetVolumeKeyAsync(VfsPath path, CancellationToken ct)
    {
        Require(path);
        return ct.IsCancellationRequested ? Task.FromCanceled<string>(ct) : Task.FromResult(Profile.VolumeKey);
    }

    public IDisposable? Watch(VfsPath directory, Action changed)
    {
        Require(directory);
        return null;
    }

    // ---- Internals ----------------------------------------------------------------------------------------------

    private void Require(VfsPath path)
    {
        if (!string.Equals(path.ProviderId, Id, StringComparison.Ordinal))
            throw new ArgumentException($"Path '{path}' does not belong to provider '{Id}'.", nameof(path));
    }

    private void RequireDirectory(VfsPath path, RemoteEntry entry)
    {
        if (entry.Kind == EntryKind.Directory || (entry.Kind == EntryKind.Symlink && entry.LinkPointsToDirectory))
            return;
        throw new VfsAlreadyExistsException($"'{GetDisplayPath(path)}' exists and is not a folder.");
    }

    private FileEntry ToEntry(VfsPath path, RemoteEntry entry) =>
        new(path, entry.Kind, entry.Size, entry.Modified, entry.Modified, entry.Mode,
            IsHidden: path.Name.StartsWith('.'), entry.LinkTarget, entry.LinkPointsToDirectory);

    /// <summary>The root is a folder on every server and is not asked about: some servers cannot describe it.</summary>
    private static readonly RemoteEntry RootEntry =
        new("", EntryKind.Directory, 0, DateTimeOffset.UnixEpoch, Mode: null);

    private Task<RemoteEntry?> StatAsync(VfsPath path, CancellationToken ct) => path.IsRoot
        ? Task.FromResult<RemoteEntry?>(RootEntry)
        : CallAsync((session, token) => session.StatAsync(path.Path, token), ct);

    private Task<List<RemoteEntry>> ListAsync(VfsPath directory, CancellationToken ct) =>
        CallAsync(async (session, token) =>
        {
            var list = new List<RemoteEntry>();
            await foreach (var entry in session.ListAsync(directory.Path, token).ConfigureAwait(false))
                list.Add(entry);
            return list;
        }, ct);

    /// <summary>Children first; folders are entered, everything else (files, links) deleted as it is.</summary>
    private async Task DeleteTreeAsync(VfsPath directory, CancellationToken ct)
    {
        foreach (var child in await ListAsync(directory, ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            if (child.Name.Length == 0 || child.Name is "." or ".." || child.Name.Contains('/'))
                throw new VfsException($"'{GetDisplayPath(directory)}' holds a name that cannot be deleted.");
            var path = directory.Combine(child.Name);
            if (child.Kind == EntryKind.Directory)
                await DeleteTreeAsync(path, ct).ConfigureAwait(false);
            else
                await CallAsync((session, token) => session.DeleteFileAsync(path.Path, token), ct)
                    .ConfigureAwait(false);
        }
        ct.ThrowIfCancellationRequested();
        await CallAsync((session, token) => session.DeleteDirectoryAsync(directory.Path, token), ct)
            .ConfigureAwait(false);
    }

    private Task CallAsync(Func<IRemoteSession, CancellationToken, Task> call, CancellationToken ct) =>
        CallAsync<bool>(async (session, token) =>
        {
            await call(session, token).ConfigureAwait(false);
            return true;
        }, ct);

    /// <summary>Makes one call on a connection from the pool. A lost connection that had been sitting in the pool is
    /// thrown away (with the other free ones, which waited as long) and the call made once more.</summary>
    private async Task<T> CallAsync<T>(Func<IRemoteSession, CancellationToken, Task<T>> call, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var lease = await _pool.AcquireAsync(ct).ConfigureAwait(false);
            T result;
            try
            {
                result = await call(lease.Session, ct).ConfigureAwait(false);
            }
            catch (VfsConnectionLostException e) when (lease.Reused && attempt == 1)
            {
                lease.Release(e);
                await _pool.CloseIdleAsync().ConfigureAwait(false);
                continue;
            }
            catch (Exception e)
            {
                lease.Release(e);
                throw;
            }
            lease.Release();
            return result;
        }
    }

    /// <summary>As <see cref="CallAsync{T}"/>, but the connection stays with the stream until it is closed. A read
    /// stream closed before its end leaves a connection (not multiplexed) in a state this class cannot know — an FTP
    /// transfer cut short — so that connection is closed rather than lent again.</summary>
    private async Task<Stream> OpenStreamAsync(Func<IRemoteSession, CancellationToken, Task<Stream>> open,
        bool reading, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var lease = await _pool.AcquireAsync(ct).ConfigureAwait(false);
            try
            {
                return new LeasedStream(await open(lease.Session, ct).ConfigureAwait(false), lease, reading);
            }
            catch (VfsConnectionLostException e) when (lease.Reused && attempt == 1)
            {
                lease.Release(e);
                await _pool.CloseIdleAsync().ConfigureAwait(false);
            }
            catch (Exception e)
            {
                lease.Release(e);
                throw;
            }
        }
    }

    /// <summary>A stream of a connection: closing it closes the inner stream first — its error (the server's answer to
    /// an upload) is thrown, never swallowed — and then gives the connection back (see
    /// <see cref="OpenStreamAsync"/> for a read stream closed early).</summary>
    private sealed class LeasedStream(Stream inner, SessionPool.Lease lease, bool reading) : Stream
    {
        private Exception? _failure;
        private bool _disposed;
        private bool _atEnd;

        private bool ClosedEarly => reading && !_atEnd;

        private int Seen(int read, int asked)
        {
            if (read == 0 && asked > 0) _atEnd = true;
            return read;
        }

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Watch(() => Seen(inner.Read(buffer, offset, count), count));

        public override int Read(Span<byte> buffer)
        {
            try
            {
                return Seen(inner.Read(buffer), buffer.Length);
            }
            catch (Exception e)
            {
                _failure ??= e;
                throw;
            }
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            try
            {
                return Seen(await inner.ReadAsync(buffer, ct).ConfigureAwait(false), buffer.Length);
            }
            catch (Exception e)
            {
                _failure ??= e;
                throw;
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override void Write(byte[] buffer, int offset, int count) =>
            Watch(() =>
            {
                inner.Write(buffer, offset, count);
                return 0;
            });

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            try
            {
                inner.Write(buffer);
            }
            catch (Exception e)
            {
                _failure ??= e;
                throw;
            }
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            try
            {
                await inner.WriteAsync(buffer, ct).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _failure ??= e;
                throw;
            }
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override void Flush() => Watch(() =>
        {
            inner.Flush();
            return 0;
        });

        public override async Task FlushAsync(CancellationToken ct)
        {
            try
            {
                await inner.FlushAsync(ct).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _failure ??= e;
                throw;
            }
        }

        public override long Seek(long offset, SeekOrigin origin) => Watch(() => inner.Seek(offset, origin));

        public override void SetLength(long value) => Watch(() =>
        {
            inner.SetLength(value);
            return 0;
        });

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                try
                {
                    inner.Dispose();
                }
                catch (Exception e)
                {
                    _failure ??= e;
                    throw;
                }
                finally
                {
                    lease.Release(_failure, ClosedEarly);
                }
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                _disposed = true;
                try
                {
                    await inner.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    _failure ??= e;
                    throw;
                }
                finally
                {
                    lease.Release(_failure, ClosedEarly);
                }
            }
            await base.DisposeAsync().ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }

        private T Watch<T>(Func<T> action)
        {
            try
            {
                return action();
            }
            catch (Exception e)
            {
                _failure ??= e;
                throw;
            }
        }
    }
}
