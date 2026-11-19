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
/// destination (a stale <c>.tandem-part</c>, even a symbolic link) is unlinked first, never written through —
/// so the destination must be a private temporary path of the caller's, never a real file of the user's.
/// <para>Any error copyfile reports to the status callback stops the copy (nothing is retried or ignored) and
/// surfaces as an <see cref="IOException"/>.</para>
/// <para>Progress comes from copyfile's status callback, which a clone never invokes, so the final size is
/// always reported once the copy is done. Cancellation stops the copy at the next write. The pause checkpoint is
/// awaited in the same callback after each reported block: while it waits, copyfile waits (the thread is blocked,
/// the files stay open); a cancellation during the wait stops the copy.</para>
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacNativeCopy : INativeCopy
{
    /// <summary>The copyfile flags used in production.</summary>
    internal const uint DefaultFlags = LibC.COPYFILE_ALL | LibC.COPYFILE_CLONE | LibC.COPYFILE_NOFOLLOW |
        // COPYFILE_CLONE implies COPYFILE_EXCL; the engine's part file may be a stale leftover that is to be replaced.
        LibC.COPYFILE_UNLINK;

    private readonly IFileAccessBroker _broker;
    private readonly uint _flags;

    public MacNativeCopy(IFileAccessBroker broker) : this(broker, DefaultFlags) { }

    /// <summary>For tests: other copyfile flags (e.g. without <c>COPYFILE_CLONE</c>, so the callback runs).</summary>
    internal MacNativeCopy(IFileAccessBroker broker, uint flags)
    {
        _broker = broker ?? throw new ArgumentNullException(nameof(broker));
        _flags = flags;
    }

    /// <exception cref="IOException">The copy failed; the message is <c>strerror(errno)</c>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public Task CopyFileAsync(string sourcePath, string destinationPath, IProgress<long> bytesCopied,
        Func<CancellationToken, ValueTask> pauseCheckpoint, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourcePath);
        ArgumentException.ThrowIfNullOrEmpty(destinationPath);
        ArgumentNullException.ThrowIfNull(bytesCopied);
        ArgumentNullException.ThrowIfNull(pauseCheckpoint);
        if (ct.IsCancellationRequested) return Task.FromCanceled(ct);

        return Task.Run(() =>
        {
            using var sourceAccess = _broker.Access(sourcePath);
            using var destinationAccess = _broker.Access(destinationPath);
            Copy(sourcePath, destinationPath, _flags, bytesCopied, pauseCheckpoint, ct);
        }, ct);
    }

    private static unsafe void Copy(string sourcePath, string destinationPath, uint flags, IProgress<long> bytesCopied,
        Func<CancellationToken, ValueTask> pauseCheckpoint, CancellationToken ct)
    {
        var state = LibC.copyfile_state_alloc();
        if (state == IntPtr.Zero) throw new OutOfMemoryException("copyfile_state_alloc failed.");
        var context = new CopyContext(bytesCopied, pauseCheckpoint, ct);
        var handle = GCHandle.Alloc(context);
        try
        {
            delegate* unmanaged[Cdecl]<int, int, IntPtr, byte*, byte*, IntPtr, int> callback = &OnStatus;
            if (LibC.copyfile_state_set(state, LibC.COPYFILE_STATE_STATUS_CB, callback) != 0 ||
                LibC.copyfile_state_set(state, LibC.COPYFILE_STATE_STATUS_CTX, (void*)GCHandle.ToIntPtr(handle)) != 0)
                throw new IOException(LibC.StrError(Marshal.GetLastPInvokeError()));

            var result = LibC.copyfile(sourcePath, destinationPath, state, flags);
            var errno = Marshal.GetLastPInvokeError();

            context.Failure?.Throw();
            if (result < 0)
            {
                // After COPYFILE_QUIT copyfile returns -1 and leaves errno as it was, so the callback's own record
                // says why: a cancellation, or the error it was told about (errno as it was at that moment).
                if (context.QuitForCancellation) throw new OperationCanceledException(ct);
                throw new IOException(LibC.StrError(context.ErrorErrno is int seen and not 0 ? seen : errno));
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
        // Read errno first, before any managed code can change it.
        var errno = Marshal.GetLastSystemError();
        var context = (CopyContext)GCHandle.FromIntPtr(ctx).Target!;
        try
        {
            // At an error stage the answer decides the error's fate: "continue" retries a failed write (forever, for
            // ENOSPC or EIO) or ignores a failed extended attribute. Neither is wanted: stop and report it.
            if (stage == LibC.COPYFILE_ERR)
            {
                context.ErrorErrno = errno;
                return LibC.COPYFILE_QUIT;
            }
            if (context.Token.IsCancellationRequested)
            {
                context.QuitForCancellation = true;
                return LibC.COPYFILE_QUIT;
            }
            if (what == LibC.COPYFILE_COPY_DATA && stage == LibC.COPYFILE_PROGRESS)
            {
                long copied = 0; // off_t
                if (LibC.copyfile_state_get(state, LibC.COPYFILE_STATE_COPIED, &copied) == 0)
                    context.Progress.Report(copied);
                // Blocks this (copyfile's) thread for as long as the operation is paused.
                context.PauseCheckpoint(context.Token).AsTask().GetAwaiter().GetResult();
            }
            return LibC.COPYFILE_CONTINUE;
        }
        catch (OperationCanceledException) when (context.Token.IsCancellationRequested)
        {
            context.QuitForCancellation = true; // cancelled while paused
            return LibC.COPYFILE_QUIT;
        }
        catch (Exception e)
        {
            // An exception must not unwind through native code: stop the copy and rethrow it afterwards.
            context.Failure = ExceptionDispatchInfo.Capture(e);
            return LibC.COPYFILE_QUIT;
        }
    }

    private sealed class CopyContext(IProgress<long> progress, Func<CancellationToken, ValueTask> pauseCheckpoint,
        CancellationToken token)
    {
        public IProgress<long> Progress { get; } = progress;

        public Func<CancellationToken, ValueTask> PauseCheckpoint { get; } = pauseCheckpoint;

        public CancellationToken Token { get; } = token;

        public ExceptionDispatchInfo? Failure { get; set; }

        /// <summary>The callback stopped the copy because the token was cancelled.</summary>
        public bool QuitForCancellation { get; set; }

        /// <summary>The errno of the error stage at which the callback stopped the copy, if it did.</summary>
        public int? ErrorErrno { get; set; }
    }
}
