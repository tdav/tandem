namespace Tandem.Core.Tests.Viewing;

/// <summary>
/// A read-only seekable stream of any declared length whose bytes are computed from their position,
/// so a test can read "an 8 GiB file" without one on disk. Counts every byte handed out
/// (<see cref="BytesRead"/>) and the largest single request (<see cref="MaxRequestedCount"/>).
/// </summary>
public sealed class SparseStream(long length, Func<long, byte>? content) : Stream
{
    private readonly byte[]? _bytes;
    private long _position;

    /// <summary>A counting stream over real bytes, copied a span at a time (fast for many reads).</summary>
    public SparseStream(byte[] bytes) : this(bytes.Length, null) => _bytes = bytes;

    public long BytesRead { get; private set; }
    public int MaxRequestedCount { get; private set; }

    public void ResetCounters()
    {
        BytesRead = 0;
        MaxRequestedCount = 0;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => length;

    public override long Position
    {
        get => _position;
        set => _position = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        MaxRequestedCount = Math.Max(MaxRequestedCount, buffer.Length);
        var n = (int)Math.Clamp(length - _position, 0, buffer.Length);
        if (_bytes is not null)
            _bytes.AsSpan((int)_position, n).CopyTo(buffer);
        else
            for (var i = 0; i < n; i++)
                buffer[i] = content!(_position + i);
        _position += n;
        BytesRead += n;
        return n;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Read(buffer.Span));
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
    {
        SeekOrigin.Begin => offset,
        SeekOrigin.Current => _position + offset,
        SeekOrigin.End => length + offset,
        _ => throw new ArgumentOutOfRangeException(nameof(origin)),
    };

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
