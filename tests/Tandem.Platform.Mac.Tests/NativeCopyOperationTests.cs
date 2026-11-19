using System.Runtime.Versioning;
using Tandem.Core.Operations;
using Tandem.Core.Platform;
using Tandem.Core.Tests.Operations;
using Tandem.Core.Vfs;
using Tandem.Core.Vfs.Local;

namespace Tandem.Platform.Mac.Tests;

/// <summary>The production combination: copy and move operations over the local provider with the real
/// <see cref="MacNativeCopy"/>, in a temporary directory.</summary>
[SupportedOSPlatform("macos")]
public sealed class NativeCopyOperationTests : IDisposable
{
    private const int BigSize = 8 * 1024 * 1024;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private readonly TempDir _temp = new();
    private readonly ProviderRegistry _providers = new();
    private readonly ScriptedInteraction _interaction = new();

    public NativeCopyOperationTests()
    {
        _providers.Register(new LocalFileSystemProvider(new UnrestrictedAccessBroker()));
        Directory.CreateDirectory(_temp.Combine("src"));
        Directory.CreateDirectory(_temp.Combine("dst"));
    }

    public void Dispose() => _temp.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The production copy.</summary>
    private static MacNativeCopy Native() => new(new UnrestrictedAccessBroker());

    /// <summary>Without cloning, so copyfile copies in blocks and its callback (progress, pause, cancel) runs even
    /// when the temp directory is on APFS.</summary>
    private static MacNativeCopy NativeInBlocks() =>
        new(new UnrestrictedAccessBroker(), MacNativeCopy.DefaultFlags & ~Interop.LibC.COPYFILE_CLONE);

    private CopyOperation Copy(string source, INativeCopy native, CopyOptions? options = null) =>
        new(_providers, [VfsPath.Local(_temp.Combine(source))], VfsPath.Local(_temp.Combine("dst")),
            options ?? new CopyOptions(), _interaction, native);

    private string[] Entries(string directory) =>
        Directory.EnumerateFileSystemEntries(_temp.Combine(directory), "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(_temp.Combine(directory), p))
            .Order(StringComparer.Ordinal).ToArray();

    private static byte[] RandomBytes(int count)
    {
        var bytes = new byte[count];
        Random.Shared.NextBytes(bytes);
        return bytes;
    }

    [Fact]
    public async Task Pause_inside_a_file_stops_the_bytes_and_resume_completes_the_copy()
    {
        var content = RandomBytes(BigSize);
        await File.WriteAllBytesAsync(_temp.Combine("src/big.bin"), content, Ct);
        CopyOperation? op = null;
        op = Copy("src/big.bin", new FirstBytesHook(NativeInBlocks(), () => op!.Pause()));
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        op.Changed += (_, _) =>
        {
            if (op.State == OperationState.Paused) paused.TrySetResult();
        };

        var run = op.RunAsync();
        await paused.Task.WaitAsync(Timeout, Ct);
        var bytesWhenPaused = op.Progress.BytesDone;
        await Task.Delay(300, Ct);

        Assert.Equal(OperationState.Paused, op.State);
        Assert.Equal(bytesWhenPaused, op.Progress.BytesDone);
        Assert.InRange(bytesWhenPaused, 1, BigSize - 1); // really inside the file
        Assert.Equal([".big.bin.tandem-part"], Entries("dst"));
        Assert.False(run.IsCompleted);

        op.Resume();
        var result = await run.WaitAsync(Timeout, Ct);

        Assert.Equal(OperationState.Completed, result.State);
        Assert.Equal(["big.bin"], Entries("dst"));
        Assert.Equal(content, await File.ReadAllBytesAsync(_temp.Combine("dst/big.bin"), Ct));
        Assert.Equal(op.Progress.BytesTotal, op.Progress.BytesDone);
    }

    [Fact]
    public async Task Cancel_while_paused_inside_a_file_ends_cancelled_without_leftovers()
    {
        var content = RandomBytes(BigSize);
        await File.WriteAllBytesAsync(_temp.Combine("src/big.bin"), content, Ct);
        CopyOperation? op = null;
        op = Copy("src/big.bin", new FirstBytesHook(NativeInBlocks(), () => op!.Pause()));
        op.Changed += (_, _) =>
        {
            if (op.State == OperationState.Paused) op.Cancel();
        };

        var result = await op.RunAsync().WaitAsync(Timeout, Ct);

        Assert.Equal(OperationState.Cancelled, result.State);
        Assert.Empty(Entries("dst"));
        Assert.Equal(content, await File.ReadAllBytesAsync(_temp.Combine("src/big.bin"), Ct));
    }

    /// <summary>The real copy, with <c>onFirstBytes</c> run (on copyfile's thread, synchronously) when it first
    /// reports copied bytes: a pause or cancel requested there reaches the copy inside the file, however fast the
    /// disk is.</summary>
    private sealed class FirstBytesHook(INativeCopy inner, Action onFirstBytes) : INativeCopy
    {
        public Task CopyFileAsync(string sourcePath, string destinationPath, IProgress<long> bytesCopied,
            Func<CancellationToken, ValueTask> pauseCheckpoint, CancellationToken ct) =>
            inner.CopyFileAsync(sourcePath, destinationPath, new Hook(bytesCopied, onFirstBytes), pauseCheckpoint, ct);

        private sealed class Hook(IProgress<long> inner, Action onFirstBytes) : IProgress<long>
        {
            private int _fired;

            public void Report(long value)
            {
                inner.Report(value);
                if (value > 0 && Interlocked.Exchange(ref _fired, 1) == 0) onFirstBytes();
            }
        }
    }
}
