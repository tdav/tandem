using Tandem.Core.Vfs;
using Tandem.Core.Vfs.InMemory;

namespace Tandem.Core.Tests.Vfs;

/// <summary>
/// A remote source whose connection can be lost: wraps an <see cref="InMemoryFileSystem"/> under its own id (not
/// "mem", so it registers beside the default one). While <see cref="Connected"/> is false every call throws
/// <see cref="VfsConnectionLostException"/>, unless <see cref="ReconnectOnNextCall"/> brings the connection back
/// first. <see cref="DropAfterBytes"/> breaks the next stream that reads or writes that many bytes: the stream
/// throws, and the connection is lost. Thread-safe.
/// </summary>
public sealed class DroppingProvider : IFileSystemProvider
{
    private readonly Lock _lock = new();
    private readonly List<VfsPath> _reads = [];
    private bool _connected = true;
    private bool _reconnectOnNextCall;
    private long? _dropAfterBytes;
    private ProviderCapabilities? _capabilities;

    public DroppingProvider(string id = "drop") : this(new InMemoryFileSystem(id)) { }

    public DroppingProvider(InMemoryFileSystem inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        Inner = inner;
    }

    /// <summary>The files behind the connection; arrange and inspect them here, whatever the connection.</summary>
    public InMemoryFileSystem Inner { get; }

    public bool Connected
    {
        get { lock (_lock) return _connected; }
        set { lock (_lock) _connected = value; }
    }

    /// <summary>While disconnected: the next call connects again (and this resets), instead of failing.</summary>
    public bool ReconnectOnNextCall
    {
        get { lock (_lock) return _reconnectOnNextCall; }
        set { lock (_lock) _reconnectOnNextCall = value; }
    }

    /// <summary>The next stream opened for reading or writing breaks after this many bytes (once: this then
    /// resets to null) and the connection is lost. The bytes up to the limit are read or written.</summary>
    public long? DropAfterBytes
    {
        get { lock (_lock) return _dropAfterBytes; }
        set { lock (_lock) _dropAfterBytes = value; }
    }

    /// <summary>The paths opened for reading, in order; an open that failed is not a read.</summary>
    public IReadOnlyList<VfsPath> Reads
    {
        get { lock (_lock) return _reads.ToArray(); }
    }

    public string Id => Inner.Id;

    /// <summary>Those of the wrapped source unless set.</summary>
    public ProviderCapabilities Capabilities
    {
        get { lock (_lock) return _capabilities ?? Inner.Capabilities; }
        set { lock (_lock) _capabilities = value; }
    }

    public async IAsyncEnumerable<FileEntry> EnumerateAsync(VfsPath directory,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        Check(directory);
        await foreach (var entry in Inner.EnumerateAsync(directory, ct).ConfigureAwait(false))
            yield return entry;
    }

    public Task<FileEntry?> GetEntryAsync(VfsPath path, CancellationToken ct) =>
        Call(path, () => Inner.GetEntryAsync(path, ct));

    public Task<Stream> OpenReadAsync(VfsPath path, CancellationToken ct) => Call(path, async () =>
    {
        var stream = await Inner.OpenReadAsync(path, ct).ConfigureAwait(false);
        lock (_lock) _reads.Add(path);
        return Wrap(stream);
    });

    public Task<Stream> OpenWriteAsync(VfsPath path, CancellationToken ct) => Call(path, async () =>
        Wrap(await Inner.OpenWriteAsync(path, ct).ConfigureAwait(false)));

    public Task CreateDirectoryAsync(VfsPath path, CancellationToken ct) =>
        Call(path, () => Inner.CreateDirectoryAsync(path, ct));

    public Task DeleteAsync(VfsPath path, bool recursive, CancellationToken ct) =>
        Call(path, () => Inner.DeleteAsync(path, recursive, ct));

    public Task MoveAsync(VfsPath source, VfsPath destination, CancellationToken ct) =>
        Call(source, () => Inner.MoveAsync(source, destination, ct));

    public Task CreateSymlinkAsync(VfsPath path, string target, CancellationToken ct) =>
        Call(path, () => Inner.CreateSymlinkAsync(path, target, ct));

    public Task SetModifiedAsync(VfsPath path, DateTimeOffset modified, CancellationToken ct) =>
        Call(path, () => Inner.SetModifiedAsync(path, modified, ct));

    public Task SetModeAsync(VfsPath path, UnixFileMode mode, CancellationToken ct) =>
        Call(path, () => Inner.SetModeAsync(path, mode, ct));

    public Task<bool> IsSameEntryAsync(VfsPath a, VfsPath b, CancellationToken ct) =>
        Call(a, () => Inner.IsSameEntryAsync(a, b, ct));

    public Task<string> GetVolumeKeyAsync(VfsPath path, CancellationToken ct) =>
        Call(path, () => Inner.GetVolumeKeyAsync(path, ct));

    public IDisposable? Watch(VfsPath directory, Action changed)
    {
        Check(directory);
        return Inner.Watch(directory, changed);
    }

    private Task Call(VfsPath path, Func<Task> body)
    {
        try
        {
            Check(path);
        }
        catch (Exception e)
        {
            return Task.FromException(e);
        }
        return body();
    }

    private Task<T> Call<T>(VfsPath path, Func<Task<T>> body)
    {
        try
        {
            Check(path);
        }
        catch (Exception e)
        {
            return Task.FromException<T>(e);
        }
        return body();
    }

    private void Check(VfsPath path)
    {
        lock (_lock)
        {
            if (_connected) return;
            if (_reconnectOnNextCall)
            {
                _connected = true;
                _reconnectOnNextCall = false;
                return;
            }
        }
        throw new VfsConnectionLostException($"The connection to '{Id}' was lost ({path.Path}).");
    }

    private Stream Wrap(Stream inner)
    {
        long? limit;
        lock (_lock)
        {
            limit = _dropAfterBytes;
            _dropAfterBytes = null;
        }
        return limit is { } bytes ? new DroppingStream(this, inner, bytes) : inner;
    }

    private void Drop()
    {
        lock (_lock) _connected = false;
    }

    /// <summary>Passes <c>limit</c> bytes through, then loses the connection and throws on every later read or
    /// write.</summary>
    private sealed class DroppingStream(DroppingProvider owner, Stream inner, long limit) : Stream
    {
        private long _left = limit;
        private bool _dropped;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            ThrowIfDropped(buffer.Length);
            var read = inner.Read(buffer[..(int)Math.Min(buffer.Length, _left)]);
            _left -= read;
            return read;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try
            {
                return ValueTask.FromResult(Read(buffer.Span));
            }
            catch (Exception e)
            {
                return ValueTask.FromException<int>(e);
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            var allowed = (int)Math.Min(buffer.Length, Math.Max(_left, 0));
            inner.Write(buffer[..allowed]);
            _left -= allowed;
            if (allowed < buffer.Length) ThrowIfDropped(buffer.Length - allowed);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try
            {
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

        public override void Flush() => inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        /// <summary>Throws once the limit is used up and more is asked for.</summary>
        private void ThrowIfDropped(int wanted)
        {
            if (!_dropped && (_left > 0 || wanted == 0)) return;
            _dropped = true;
            owner.Drop();
            throw new VfsConnectionLostException("The connection was lost during the transfer.");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
