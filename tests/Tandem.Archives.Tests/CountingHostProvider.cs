using Tandem.Core.Vfs;
using Tandem.Core.Vfs.InMemory;

namespace Tandem.Archives.Tests;

/// <summary>A host for archives in tests: an <see cref="InMemoryFileSystem"/> that counts how often each file is
/// opened for reading and can hand out streams that do not seek (like an entry of another archive).</summary>
internal sealed class CountingHostProvider(string id = "mem") : IFileSystemProvider
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, int> _reads = new(StringComparer.Ordinal);

    public InMemoryFileSystem Files { get; } = new(id);

    /// <summary>Streams from <see cref="OpenReadAsync"/> cannot seek.</summary>
    public bool Unseekable { get; set; }

    public int ReadsOf(string path)
    {
        lock (_lock) return _reads.GetValueOrDefault(path);
    }

    public string Id => Files.Id;

    public ProviderCapabilities Capabilities => Files.Capabilities;

    public IAsyncEnumerable<FileEntry> EnumerateAsync(VfsPath directory, CancellationToken ct) =>
        Files.EnumerateAsync(directory, ct);

    public Task<FileEntry?> GetEntryAsync(VfsPath path, CancellationToken ct) => Files.GetEntryAsync(path, ct);

    public async Task<Stream> OpenReadAsync(VfsPath path, CancellationToken ct)
    {
        lock (_lock) _reads[path.Path] = _reads.GetValueOrDefault(path.Path) + 1;
        var stream = await Files.OpenReadAsync(path, ct);
        return Unseekable ? new ForwardOnlyStream(stream) : stream;
    }

    public Task<Stream> OpenWriteAsync(VfsPath path, CancellationToken ct) => Files.OpenWriteAsync(path, ct);

    public Task CreateDirectoryAsync(VfsPath path, CancellationToken ct) => Files.CreateDirectoryAsync(path, ct);

    public Task DeleteAsync(VfsPath path, bool recursive, CancellationToken ct) =>
        Files.DeleteAsync(path, recursive, ct);

    public Task MoveAsync(VfsPath source, VfsPath destination, CancellationToken ct) =>
        Files.MoveAsync(source, destination, ct);

    public Task CreateSymlinkAsync(VfsPath path, string target, CancellationToken ct) =>
        Files.CreateSymlinkAsync(path, target, ct);

    public Task SetModifiedAsync(VfsPath path, DateTimeOffset modified, CancellationToken ct) =>
        Files.SetModifiedAsync(path, modified, ct);

    public Task SetModeAsync(VfsPath path, UnixFileMode mode, CancellationToken ct) =>
        Files.SetModeAsync(path, mode, ct);

    public Task<bool> IsSameEntryAsync(VfsPath a, VfsPath b, CancellationToken ct) =>
        Files.IsSameEntryAsync(a, b, ct);

    public Task<string> GetVolumeKeyAsync(VfsPath path, CancellationToken ct) => Files.GetVolumeKeyAsync(path, ct);

    public IDisposable? Watch(VfsPath directory, Action changed) => Files.Watch(directory, changed);

    private sealed class ForwardOnlyStream(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
