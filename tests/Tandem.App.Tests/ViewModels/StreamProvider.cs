using System.Runtime.CompilerServices;
using Tandem.Core.Vfs;

namespace Tandem.App.Tests.ViewModels;

/// <summary>
/// A read-only provider whose files are streams the test hands out: of any size (a <c>SparseStream</c>), without
/// seeking (<see cref="TestStream"/> with <c>seekable: false</c>), or slow. Every stream opened is kept in
/// <see cref="Opened"/>. Files sit directly in "/".
/// </summary>
internal sealed class StreamProvider(string id = "streams") : IFileSystemProvider
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly Dictionary<string, (Func<Stream> Open, long Size)> _files = new(StringComparer.Ordinal);
    private readonly List<Stream> _opened = [];

    public string Id { get; } = id;

    public ProviderCapabilities Capabilities => ProviderCapabilities.None;

    /// <summary>Every stream handed out, in order.</summary>
    public IReadOnlyList<Stream> Opened
    {
        get { lock (_opened) return _opened.ToArray(); }
    }

    /// <summary>Adds the file <c>/{name}</c> of <paramref name="size"/> bytes; each open calls
    /// <paramref name="open"/>.</summary>
    public VfsPath Add(string name, long size, Func<Stream> open)
    {
        _files["/" + name] = (open, size);
        return new VfsPath(Id, "/" + name);
    }

    private FileEntry EntryOf(string path, long size) =>
        new(new VfsPath(Id, path), EntryKind.File, size, T0, T0, Mode: null, IsHidden: false, LinkTarget: null,
            LinkPointsToDirectory: false);

    public async IAsyncEnumerable<FileEntry> EnumerateAsync(VfsPath directory,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        if (directory.Path != "/") throw new VfsNotFoundException($"'{directory.Path}' does not exist.");
        foreach (var (path, file) in _files) yield return EntryOf(path, file.Size);
    }

    public Task<FileEntry?> GetEntryAsync(VfsPath path, CancellationToken ct) =>
        Task.FromResult(path.Path == "/"
            ? new FileEntry(path, EntryKind.Directory, 0, T0, T0, null, false, null, false)
            : _files.TryGetValue(path.Path, out var file) ? EntryOf(path.Path, file.Size) : null);

    public Task<Stream> OpenReadAsync(VfsPath path, CancellationToken ct)
    {
        if (!_files.TryGetValue(path.Path, out var file))
            throw new VfsNotFoundException($"'{path.Path}' does not exist.");
        var stream = file.Open();
        lock (_opened) _opened.Add(stream);
        return Task.FromResult(stream);
    }

    private static NotSupportedException ReadOnly() => new("The stream provider is read-only.");

    public Task<Stream> OpenWriteAsync(VfsPath path, CancellationToken ct) => throw ReadOnly();
    public Task CreateDirectoryAsync(VfsPath path, CancellationToken ct) => throw ReadOnly();
    public Task DeleteAsync(VfsPath path, bool recursive, CancellationToken ct) => throw ReadOnly();
    public Task MoveAsync(VfsPath source, VfsPath destination, CancellationToken ct) => throw ReadOnly();
    public Task CreateSymlinkAsync(VfsPath path, string target, CancellationToken ct) => throw ReadOnly();
    public Task SetModifiedAsync(VfsPath path, DateTimeOffset modified, CancellationToken ct) => throw ReadOnly();
    public Task SetModeAsync(VfsPath path, UnixFileMode mode, CancellationToken ct) => throw ReadOnly();
    public Task<bool> IsSameEntryAsync(VfsPath a, VfsPath b, CancellationToken ct) => Task.FromResult(a == b);
    public Task<string> GetVolumeKeyAsync(VfsPath path, CancellationToken ct) => Task.FromResult(Id);
    public IDisposable? Watch(VfsPath directory, Action changed) => null;
}

/// <summary>A read-only stream over another that can hide its seeking (as a decompressing stream does), hold every
/// read until <see cref="Gate"/> opens (honouring cancellation), and tells whether it was disposed.</summary>
internal sealed class TestStream(Stream inner, bool seekable) : Stream
{
    public bool IsDisposed { get; private set; }

    /// <summary>While set, each read waits for it first.</summary>
    public TaskCompletionSource? Gate { get; set; }

    /// <summary>While set, each read throws it.</summary>
    public Exception? Failure { get; set; }

    /// <summary>Set when a read starts waiting on <see cref="Gate"/>.</summary>
    public TaskCompletionSource ReadWaiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override bool CanRead => !IsDisposed;
    public override bool CanSeek => seekable && !IsDisposed;
    public override bool CanWrite => false;
    public override long Length => seekable ? inner.Length : throw new NotSupportedException();

    public override long Position
    {
        get => seekable ? inner.Position : throw new NotSupportedException();
        set
        {
            if (!seekable) throw new NotSupportedException();
            inner.Position = value;
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (Gate is { } gate)
        {
            ReadWaiting.TrySetResult();
            gate.Task.Wait();
        }
        return inner.Read(buffer, offset, count);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (Gate is { } gate)
        {
            ReadWaiting.TrySetResult();
            await gate.Task.WaitAsync(cancellationToken);
        }
        if (Failure is { } failure) throw failure;
        return await inner.ReadAsync(buffer, cancellationToken);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override long Seek(long offset, SeekOrigin origin) =>
        seekable ? inner.Seek(offset, origin) : throw new NotSupportedException();

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        IsDisposed = true;
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
}
