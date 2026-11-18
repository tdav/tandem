using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Tandem.Core.Platform;
using Tandem.Core.Vfs.Local;
using Tandem.Platform.Mac.Interop;

namespace Tandem.Platform.Mac;

/// <summary>
/// The system file copy, <c>copyfile(3)</c>: data, permissions, times, ACLs and extended attributes, as an APFS
/// clone when the volume allows it. A symbolic link source is copied as the link itself; a leftover at the
/// destination (a stale <c>.tandem-part</c>, even a symbolic link) is unlinked first, never written through.
/// <para>Progress comes from copyfile's status callback, which a clone never invokes, so the final size is
/// always reported once the copy is done. Cancellation stops the copy at the next write.</para>
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacNativeCopy(IFileAccessBroker broker) : INativeCopy
{
    private const uint Flags = LibC.COPYFILE_ALL | LibC.COPYFILE_CLONE | LibC.COPYFILE_NOFOLLOW |
        // COPYFILE_CLONE implies COPYFILE_EXCL; the engine's part file may be a stale leftover that is to be replaced.
        LibC.COPYFILE_UNLINK;

    private readonly IFileAccessBroker _broker = broker ?? throw new ArgumentNullException(nameof(broker));

    /// <exception cref="IOException">The copy failed; the message is <c>strerror(errno)</c>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public Task CopyFileAsync(string sourcePath, string destinationPath, IProgress<long> bytesCopied,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourcePath);
        ArgumentException.ThrowIfNullOrEmpty(destinationPath);
        ArgumentNullException.ThrowIfNull(bytesCopied);
        if (ct.IsCancellationRequested) return Task.FromCanceled(ct);

        return Task.Run(() =>
        {
            using var sourceAccess = _broker.Access(sourcePath);
            using var destinationAccess = _broker.Access(destinationPath);
            Copy(sourcePath, destinationPath, bytesCopied, ct);
        }, ct);
    }

    private static unsafe void Copy(string sourcePath, string destinationPath, IProgress<long> bytesCopied,
        CancellationToken ct)
    {
        var state = LibC.copyfile_state_alloc();
        if (state == IntPtr.Zero) throw new OutOfMemoryException("copyfile_state_alloc failed.");
        var context = new CopyContext(bytesCopied, ct);
        var handle = GCHandle.Alloc(context);
        try
        {
            delegate* unmanaged[Cdecl]<int, int, IntPtr, byte*, byte*, IntPtr, int> callback = &OnStatus;
            LibC.copyfile_state_set(state, LibC.COPYFILE_STATE_STATUS_CB, callback);
            LibC.copyfile_state_set(state, LibC.COPYFILE_STATE_STATUS_CTX, (void*)GCHandle.ToIntPtr(handle));

            var result = LibC.copyfile(sourcePath, destinationPath, state, Flags);
            var errno = Marshal.GetLastPInvokeError();

            context.Failure?.Throw();
            if (result < 0)
            {
                ct.ThrowIfCancellationRequested(); // after COPYFILE_QUIT, copyfile returns -1 with errno untouched
                throw new IOException(LibC.StrError(errno));
            }

            bytesCopied.Report(new FileInfo(destinationPath).Length);
        }
        finally
        {
            LibC.copyfile_state_free(state);
            handle.Free();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int OnStatus(int what, int stage, IntPtr state, byte* source, byte* destination,
        IntPtr ctx)
    {
        var context = (CopyContext)GCHandle.FromIntPtr(ctx).Target!;
        try
        {
            if (context.Token.IsCancellationRequested) return LibC.COPYFILE_QUIT;
            if (what == LibC.COPYFILE_COPY_DATA && stage == LibC.COPYFILE_PROGRESS)
            {
                long copied = 0; // off_t
                if (LibC.copyfile_state_get(state, LibC.COPYFILE_STATE_COPIED, &copied) == 0)
                    context.Progress.Report(copied);
            }
            return LibC.COPYFILE_CONTINUE;
        }
        catch (Exception e)
        {
            // An exception must not unwind through native code: stop the copy and rethrow it afterwards.
            context.Failure = ExceptionDispatchInfo.Capture(e);
            return LibC.COPYFILE_QUIT;
        }
    }

    private sealed class CopyContext(IProgress<long> progress, CancellationToken token)
    {
        public IProgress<long> Progress { get; } = progress;

        public CancellationToken Token { get; } = token;

        public ExceptionDispatchInfo? Failure { get; set; }
    }
}
