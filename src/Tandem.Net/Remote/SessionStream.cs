using System.Runtime.ExceptionServices;

namespace Tandem.Net.Remote;

/// <summary>Translates errors from an open transfer, including its final response.</summary>
internal sealed class SessionStream(Stream stream, Func<Exception, Exception> map,
    Func<bool, ValueTask>? complete = null, TimeSpan? asyncTimeout = null) : Stream
{
    private int _closed;
    private int _failed;
    public override bool CanRead => stream.CanRead;
    public override bool CanSeek => stream.CanSeek;
    public override bool CanWrite => stream.CanWrite;
    public override long Length => stream.Length;
    public override long Position { get => stream.Position; set => stream.Position = value; }
    public override long Seek(long offset, SeekOrigin origin) => stream.Seek(offset, origin);
    public override void SetLength(long value) => stream.SetLength(value);
    public override void Flush() => Invoke(() => stream.Flush());
    public override int Read(byte[] buffer, int offset, int count) => Invoke(() => stream.Read(buffer, offset, count));
    public override void Write(byte[] buffer, int offset, int count) => Invoke(() => stream.Write(buffer, offset, count));
    public override Task FlushAsync(CancellationToken ct) =>
        InvokeAsync(async token => { await stream.FlushAsync(token).ConfigureAwait(false); return true; }, ct).AsTask();
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
        InvokeAsync(token => stream.ReadAsync(buffer, token), ct);
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) =>
        await InvokeAsync(async token => { await stream.WriteAsync(buffer, token).ConfigureAwait(false); return true; }, ct)
            .ConfigureAwait(false);
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            CloseTransferAsync().AsTask().GetAwaiter().GetResult();
        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync() => CloseTransferAsync();

    private async ValueTask CloseTransferAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 1) return;
        try
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            var mapped = map(error);
            if (complete is not null) await complete(false).ConfigureAwait(false);
            if (Volatile.Read(ref _failed) != 0) return;
            ExceptionDispatchInfo.Throw(mapped);
        }
        if (complete is not null) await complete(Volatile.Read(ref _failed) == 0).ConfigureAwait(false);
    }

    private void Invoke(Action call) => Invoke(() => { call(); return true; });

    private T Invoke<T>(Func<T> call)
    {
        try { return call(); }
        catch (Exception error) { ExceptionDispatchInfo.Throw(Failed(error)); throw; }
    }

    private async ValueTask<T> InvokeAsync<T>(Func<CancellationToken, ValueTask<T>> call, CancellationToken ct)
    {
        using var deadline = asyncTimeout.HasValue ? CancellationTokenSource.CreateLinkedTokenSource(ct) : null;
        if (asyncTimeout is { } wait) deadline!.CancelAfter(wait);
        try { return await call(deadline?.Token ?? ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (deadline?.IsCancellationRequested == true && !ct.IsCancellationRequested)
        {
            ExceptionDispatchInfo.Throw(Failed(new TimeoutException()));
            throw;
        }
        catch (Exception error) { ExceptionDispatchInfo.Throw(Failed(error)); throw; }
    }

    private Exception Failed(Exception error)
    {
        Interlocked.Exchange(ref _failed, 1);
        return map(error);
    }
}
