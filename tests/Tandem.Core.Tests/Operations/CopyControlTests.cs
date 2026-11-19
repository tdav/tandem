using System.Runtime.Versioning;
using Tandem.Core.Operations;
using Tandem.Core.Platform;
using Tandem.Core.Tests.Vfs;
using Tandem.Core.Vfs;
using Tandem.Core.Vfs.InMemory;
using Tandem.Core.Vfs.Local;

namespace Tandem.Core.Tests.Operations;

[SupportedOSPlatform("macos")]
public sealed class CopyControlTests
{
    private const int BigSize = 4 * 1024 * 1024 + 123; // several 1 MB copy blocks
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly InMemoryFileSystem _mem = new();
    private readonly ProviderRegistry _providers = new();
    private readonly ScriptedInteraction _interaction = new();

    public CopyControlTests() => _providers.Register(_mem);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static VfsPath Mem(string path) => new("mem", path);

    private CopyOperation Copy(CopyOptions? options, params string[] sources) =>
        new(_providers, sources.Select(Mem).ToArray(), Mem("/dst"), options ?? new CopyOptions(), _interaction);

    private CopyOperation Copy(params string[] sources) => Copy(null, sources);

    private static byte[] BigContent()
    {
        var content = new byte[BigSize];
        new Random(42).NextBytes(content);
        return content;
    }

    private IEnumerable<string> DestinationPaths() =>
        _mem.AllPaths.Where(p => p.StartsWith("/dst", StringComparison.Ordinal));

    private async Task<byte[]> ReadAllBytesAsync(string path)
    {
        await using var stream = await _mem.OpenReadAsync(Mem(path), Ct);
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy, Ct);
        return copy.ToArray();
    }

    /// <summary>Opening <paramref name="path"/> for reading takes 150 ms. Progress events are throttled to one per
    /// 100 ms, so the first block copied after that wait is sure to raise <see cref="FileOperation.Changed"/>
    /// while the file is still being copied.</summary>
    private void SlowOpenRead(string path) =>
        _mem.FailOn((op, p) =>
        {
            if (op == "OpenRead" && p.Path == path) Thread.Sleep(150);
            return false;
        }, () => new IOException("unused"));

    /// <summary>Counts how often <paramref name="operation"/> is called on <paramref name="path"/>.</summary>
    private Func<int> Count(string operation, string path)
    {
        var count = 0;
        _mem.FailOn((op, p) =>
        {
            if (op == operation && p.Path == path) Interlocked.Increment(ref count);
            return false;
        }, () => new IOException("unused"));
        return () => Volatile.Read(ref count);
    }

    [Fact]
    public async Task Cancel_mid_file_removes_part_and_leaves_no_target()
    {
        _mem.AddFile("/src/big.bin", BigContent());
        _mem.AddDirectory("/dst");
        SlowOpenRead("/src/big.bin");
        var op = Copy("/src/big.bin");
        long bytesAtCancel = -1;
        op.Changed += (_, _) =>
        {
            if (op.Progress.BytesDone <= 0 || bytesAtCancel >= 0) return;
            bytesAtCancel = op.Progress.BytesDone;
            op.Cancel();
        };

        var result = await op.RunAsync().WaitAsync(Timeout, Ct);

        Assert.Equal(OperationState.Cancelled, result.State);
        Assert.InRange(bytesAtCancel, 1, BigSize - 1); // it really was mid-file
        Assert.Equal(["/dst"], DestinationPaths());
        Assert.Equal(BigContent(), await ReadAllBytesAsync("/src/big.bin"));
    }

    [Fact]
    public async Task Cancel_keeps_already_copied_files()
    {
        _mem.AddFile("/src/a.txt", "first");
        _mem.AddFile("/src/b.txt", "second");
        _mem.AddFile("/src/c.bin", BigContent());
        _mem.AddDirectory("/dst");
        SlowOpenRead("/src/c.bin");
        var op = Copy("/src/a.txt", "/src/b.txt", "/src/c.bin");
        op.Changed += (_, _) =>
        {
            if (op.Progress is { CurrentItem: "/src/c.bin", BytesDone: > 11 }) op.Cancel();
        };

        var result = await op.RunAsync().WaitAsync(Timeout, Ct);

        Assert.Equal(OperationState.Cancelled, result.State);
        Assert.Equal("first", _mem.ReadAllText("/dst/a.txt"));
        Assert.Equal("second", _mem.ReadAllText("/dst/b.txt"));
        Assert.Equal(["/dst", "/dst/a.txt", "/dst/b.txt"], DestinationPaths());
    }

    [Fact]
    public async Task Pause_stops_progress_until_resume()
    {
        _mem.AddFile("/src/big.bin", BigContent());
        _mem.AddDirectory("/dst");
        SlowOpenRead("/src/big.bin");
        var op = Copy("/src/big.bin");
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pauseRequested = 0;
        op.Changed += (_, _) =>
        {
            if (op.State == OperationState.Paused) paused.TrySetResult();
            else if (op.Progress.BytesDone > 0 && Interlocked.Exchange(ref pauseRequested, 1) == 0) op.Pause();
        };

        var run = op.RunAsync();
        await paused.Task.WaitAsync(Timeout, Ct);
        var bytesWhenPaused = op.Progress.BytesDone;
        var speedWhenPaused = op.Progress.BytesPerSecond;
        await Task.Delay(200, Ct);

        Assert.Equal(OperationState.Paused, op.State);
        Assert.Equal(bytesWhenPaused, op.Progress.BytesDone);
        Assert.Equal(speedWhenPaused, op.Progress.BytesPerSecond); // time spent paused does not count
        Assert.InRange(bytesWhenPaused, 1, BigSize - 1);
        Assert.False(_mem.Exists("/dst/big.bin"));

        op.Resume();
        var result = await run.WaitAsync(Timeout, Ct);

        Assert.Equal(OperationState.Completed, result.State);
        Assert.Equal(BigContent(), await ReadAllBytesAsync("/dst/big.bin"));
        Assert.Equal(op.Progress.BytesTotal, op.Progress.BytesDone);
    }

    [Fact]
    public async Task Resume_returns_to_running()
    {
        _mem.AddFile("/src/big.bin", BigContent());
        _mem.AddDirectory("/dst");
        SlowOpenRead("/src/big.bin");
        var op = Copy("/src/big.bin");
        var states = new List<OperationState>();
        var pauseRequested = 0;
        op.Changed += (_, _) =>
        {
            var state = op.State;
            lock (states)
                if (states.Count == 0 || states[^1] != state) states.Add(state);
            if (state == OperationState.Paused) op.Resume();
            else if (op.Progress.BytesDone > 0 && Interlocked.Exchange(ref pauseRequested, 1) == 0) op.Pause();
        };

        await op.RunAsync().WaitAsync(Timeout, Ct);

        Assert.Equal(
            [OperationState.Scanning, OperationState.Running, OperationState.Paused, OperationState.Running,
                OperationState.Completed],
            states);
    }

    [Fact]
    public async Task Pause_while_scanning_returns_to_scanning()
    {
        _mem.AddFile("/src/tree/a.txt", "a");
        _mem.AddFile("/src/tree/b.txt", "b");
        _mem.AddDirectory("/dst");
        var op = Copy("/src/tree");
        var states = new List<OperationState>();
        var pauseRequested = 0;
        op.Changed += (_, _) =>
        {
            var state = op.State;
            lock (states)
                if (states.Count == 0 || states[^1] != state) states.Add(state);
            if (state == OperationState.Scanning && Interlocked.Exchange(ref pauseRequested, 1) == 0) op.Pause();
            else if (state == OperationState.Paused) op.Resume();
        };

        var result = await op.RunAsync().WaitAsync(Timeout, Ct);

        Assert.Equal(OperationState.Completed, result.State);
        Assert.Equal(
            [OperationState.Scanning, OperationState.Paused, OperationState.Scanning, OperationState.Running,
                OperationState.Completed],
            states);
        Assert.Equal("b", _mem.ReadAllText("/dst/tree/b.txt"));
    }

    [Fact]
    public async Task Cancel_while_paused_ends_as_cancelled()
    {
        _mem.AddFile("/src/big.bin", BigContent());
        _mem.AddDirectory("/dst");
        SlowOpenRead("/src/big.bin");
        var op = Copy("/src/big.bin");
        var pauseRequested = 0;
        op.Changed += (_, _) =>
        {
            if (op.State == OperationState.Paused) op.Cancel();
            else if (op.Progress.BytesDone > 0 && Interlocked.Exchange(ref pauseRequested, 1) == 0) op.Pause();
        };

        var result = await op.RunAsync().WaitAsync(Timeout, Ct);

        Assert.Equal(OperationState.Cancelled, result.State);
        Assert.Equal(["/dst"], DestinationPaths());
    }

    [Fact]
    public async Task Pause_before_run_and_after_completion_is_harmless()
    {
        _mem.AddFile("/src/a.txt", "a");
        _mem.AddDirectory("/dst");
        var op = Copy("/src/a.txt");

        op.Pause();
        var result = await op.RunAsync().WaitAsync(Timeout, Ct);
        op.Pause();
        op.Resume();

        Assert.Equal(OperationState.Completed, result.State);
        Assert.Equal(OperationState.Completed, op.State);
        Assert.Equal("a", _mem.ReadAllText("/dst/a.txt"));
    }

    [Fact]
    public async Task Target_failure_mid_file_removes_part_and_keeps_source()
    {
        _mem.AddFile("/src/big.bin", BigContent());
        _mem.AddDirectory("/dst");
        _mem.FailWriteAfter("/dst/.big.bin.tandem-part", 5, () => new IOException("No space"));
        _interaction.OnError(ErrorAction.Abort);
        var op = Copy("/src/big.bin");

        var error = await Assert.ThrowsAsync<IOException>(() => op.RunAsync().WaitAsync(Timeout, Ct));

        Assert.Equal("No space", error.Message);
        Assert.Equal(OperationState.Failed, op.State);
        Assert.Equal(["/dst"], DestinationPaths());
        Assert.Equal(BigContent(), await ReadAllBytesAsync("/src/big.bin"));
    }

    [Fact]
    public async Task Verify_passes_for_identical_content()
    {
        _mem.AddFile("/src/big.bin", BigContent());
        _mem.AddDirectory("/dst");
        var sourceReads = Count("OpenRead", "/src/big.bin");
        var partReads = Count("OpenRead", "/dst/.big.bin.tandem-part");
        var op = Copy(new CopyOptions(VerifyContent: true), "/src/big.bin");

        var result = await op.RunAsync().WaitAsync(Timeout, Ct);

        Assert.Equal(OperationState.Completed, result.State);
        Assert.Empty(result.Skipped);
        Assert.Equal(BigContent(), await ReadAllBytesAsync("/dst/big.bin"));
        Assert.Equal(2, sourceReads());
        Assert.Equal(1, partReads());
        Assert.Equal(2L * BigSize, op.Progress.BytesTotal); // verification bytes count too
        Assert.Equal(op.Progress.BytesTotal, op.Progress.BytesDone);
        Assert.Equal(["/dst", "/dst/big.bin"], DestinationPaths());
    }

    [Fact]
    public async Task Verify_mismatch_is_item_error()
    {
        _mem.AddFile("/src/a.txt", "original");
        _mem.AddFile("/src/b.txt", "fine");
        _mem.AddDirectory("/dst");
        _mem.ChangeContentOnRead("/src/a.txt", 2, "tampered"u8.ToArray()); // the verification read differs
        _interaction.OnError(ErrorAction.Skip);
        var op = Copy(new CopyOptions(VerifyContent: true), "/src/a.txt", "/src/b.txt");

        var result = await op.RunAsync().WaitAsync(Timeout, Ct);

        Assert.Equal(OperationState.Completed, result.State);
        var (item, error) = Assert.Single(_interaction.Errors);
        Assert.Equal(Mem("/src/a.txt"), item);
        Assert.IsType<ContentMismatchException>(error);
        Assert.Equal(new SkippedItem(Mem("/src/a.txt"), SkipReason.Error, error.Message), Assert.Single(result.Skipped));
        Assert.Equal(["/dst", "/dst/b.txt"], DestinationPaths());
        Assert.Equal(2L * 4, op.Progress.BytesTotal); // the skipped file's copy and verification bytes leave the total
        Assert.Equal(op.Progress.BytesTotal, op.Progress.BytesDone);
    }

    [Fact]
    public async Task Verify_mismatch_can_be_retried()
    {
        _mem.AddFile("/src/a.txt", "original");
        _mem.AddDirectory("/dst");
        _mem.ChangeContentOnRead("/src/a.txt", 2, "tampered"u8.ToArray());
        _interaction.OnError(ErrorAction.Retry);
        var op = Copy(new CopyOptions(VerifyContent: true), "/src/a.txt");

        var result = await op.RunAsync().WaitAsync(Timeout, Ct);

        Assert.Empty(result.Skipped);
        Assert.Equal("original", _mem.ReadAllText("/dst/a.txt"));
        Assert.Equal(op.Progress.BytesTotal, op.Progress.BytesDone);
    }

    [Fact]
    public async Task Verify_mismatch_on_overwrite_keeps_old_file()
    {
        _mem.AddFile("/src/a.txt", "original");
        _mem.AddFile("/dst/a.txt", "old");
        _mem.ChangeContentOnRead("/src/a.txt", 2, "tampered"u8.ToArray());
        _interaction.OnConflict(ConflictAction.Overwrite).OnError(ErrorAction.Skip);

        await Copy(new CopyOptions(VerifyContent: true), "/src/a.txt").RunAsync().WaitAsync(Timeout, Ct);

        Assert.Equal("old", _mem.ReadAllText("/dst/a.txt"));
        Assert.Equal(["/dst", "/dst/a.txt"], DestinationPaths());
    }

    [Fact]
    public async Task Verify_checks_a_native_copy_too()
    {
        using var temp = new TempDir();
        _providers.Register(new LocalFileSystemProvider(new UnrestrictedAccessBroker()));
        File.WriteAllText(temp.Combine("a.txt"), "native content");
        Directory.CreateDirectory(temp.Combine("dst"));
        _interaction.OnError(ErrorAction.Skip);
        var native = new CorruptingNativeCopy();
        var op = new CopyOperation(_providers, [VfsPath.Local(temp.Combine("a.txt"))],
            VfsPath.Local(temp.Combine("dst")), new CopyOptions(VerifyContent: true), _interaction, native);

        var result = await op.RunAsync().WaitAsync(Timeout, Ct);

        Assert.Equal(1, native.Calls);
        Assert.IsType<ContentMismatchException>(Assert.Single(_interaction.Errors).Error);
        Assert.Equal(SkipReason.Error, Assert.Single(result.Skipped).Reason);
        Assert.Empty(Directory.GetFileSystemEntries(temp.Combine("dst")));
    }

    [Fact]
    public async Task Cancel_reaches_an_in_flight_native_copy()
    {
        using var temp = new TempDir();
        _providers.Register(new LocalFileSystemProvider(new UnrestrictedAccessBroker()));
        File.WriteAllText(temp.Combine("a.txt"), "content");
        Directory.CreateDirectory(temp.Combine("dst"));
        var native = new BlockingNativeCopy();
        var op = new CopyOperation(_providers, [VfsPath.Local(temp.Combine("a.txt"))],
            VfsPath.Local(temp.Combine("dst")), new CopyOptions(), _interaction, native);

        var run = op.RunAsync();
        await native.Started.WaitAsync(Timeout, Ct);
        op.Cancel();
        var result = await run.WaitAsync(Timeout, Ct);

        Assert.Equal(OperationState.Cancelled, result.State);
        Assert.Empty(Directory.GetFileSystemEntries(temp.Combine("dst")));
    }

    [Fact]
    public async Task Pause_reaches_an_in_flight_native_copy_and_resume_finishes_it()
    {
        using var temp = new TempDir();
        _providers.Register(new LocalFileSystemProvider(new UnrestrictedAccessBroker()));
        File.WriteAllText(temp.Combine("a.txt"), "content");
        Directory.CreateDirectory(temp.Combine("dst"));
        var native = new CheckpointNativeCopy();
        var op = new CopyOperation(_providers, [VfsPath.Local(temp.Combine("a.txt"))],
            VfsPath.Local(temp.Combine("dst")), new CopyOptions(), _interaction, native);
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        op.Changed += (_, _) =>
        {
            if (op.State == OperationState.Paused) paused.TrySetResult();
        };

        var run = op.RunAsync();
        await native.Started.WaitAsync(Timeout, Ct);
        op.Pause();
        native.Continue();
        await paused.Task.WaitAsync(Timeout, Ct);
        await Task.Delay(100, Ct);

        Assert.Equal(OperationState.Paused, op.State);
        Assert.False(native.Finished); // the copy stands still inside the file
        Assert.False(run.IsCompleted);

        op.Resume();
        var result = await run.WaitAsync(Timeout, Ct);

        Assert.Equal(OperationState.Completed, result.State);
        Assert.True(native.Finished);
        Assert.Equal("content", File.ReadAllText(temp.Combine("dst/a.txt")));
    }

    [Fact]
    public async Task Cancel_while_paused_inside_a_native_copy_removes_the_part_file()
    {
        using var temp = new TempDir();
        _providers.Register(new LocalFileSystemProvider(new UnrestrictedAccessBroker()));
        File.WriteAllText(temp.Combine("a.txt"), "content");
        Directory.CreateDirectory(temp.Combine("dst"));
        var native = new CheckpointNativeCopy();
        var op = new CopyOperation(_providers, [VfsPath.Local(temp.Combine("a.txt"))],
            VfsPath.Local(temp.Combine("dst")), new CopyOptions(), _interaction, native);
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        op.Changed += (_, _) =>
        {
            if (op.State == OperationState.Paused) paused.TrySetResult();
        };

        var run = op.RunAsync();
        await native.Started.WaitAsync(Timeout, Ct);
        op.Pause();
        native.Continue();
        await paused.Task.WaitAsync(Timeout, Ct);
        op.Cancel();
        var result = await run.WaitAsync(Timeout, Ct);

        Assert.Equal(OperationState.Cancelled, result.State);
        Assert.False(native.Finished);
        Assert.Empty(Directory.GetFileSystemEntries(temp.Combine("dst")));
        Assert.Equal("content", File.ReadAllText(temp.Combine("a.txt")));
    }

    /// <summary>Writes part of the file, waits for <see cref="Continue"/>, passes the pause checkpoint, then writes
    /// the rest.</summary>
    private sealed class CheckpointNativeCopy : INativeCopy
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _continue = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool _finished;

        public Task Started => _started.Task;

        public bool Finished => _finished;

        public void Continue() => _continue.TrySetResult();

        public async Task CopyFileAsync(string sourcePath, string destinationPath, IProgress<long> bytesCopied,
            Func<CancellationToken, ValueTask> pauseCheckpoint, CancellationToken ct)
        {
            var content = await File.ReadAllBytesAsync(sourcePath, ct);
            await File.WriteAllBytesAsync(destinationPath, content[..4], ct);
            bytesCopied.Report(4);
            _started.TrySetResult();
            await _continue.Task.WaitAsync(ct);
            await pauseCheckpoint(ct);
            await File.WriteAllBytesAsync(destinationPath, content, ct);
            bytesCopied.Report(content.Length);
            _finished = true;
        }
    }

    /// <summary>Writes the part with one byte changed, as a faulty platform copy might.</summary>
    private sealed class CorruptingNativeCopy : INativeCopy
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public async Task CopyFileAsync(string sourcePath, string destinationPath, IProgress<long> bytesCopied,
            Func<CancellationToken, ValueTask> pauseCheckpoint, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            var content = await File.ReadAllBytesAsync(sourcePath, ct);
            content[0] ^= 0xFF;
            await File.WriteAllBytesAsync(destinationPath, content, ct);
            bytesCopied.Report(content.Length);
        }
    }

    /// <summary>Writes part of the file, then waits until cancelled.</summary>
    private sealed class BlockingNativeCopy : INativeCopy
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public async Task CopyFileAsync(string sourcePath, string destinationPath, IProgress<long> bytesCopied,
            Func<CancellationToken, ValueTask> pauseCheckpoint, CancellationToken ct)
        {
            await File.WriteAllTextAsync(destinationPath, "cont", ct);
            bytesCopied.Report(4);
            _started.TrySetResult();
            await Task.Delay(System.Threading.Timeout.Infinite, ct);
        }
    }
}
