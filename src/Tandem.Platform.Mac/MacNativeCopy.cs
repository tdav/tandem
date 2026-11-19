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
/// <para>The Finder lock and append-only flags are taken off the copy (and off a leftover it replaces), since the
/// caller still has to time, rename and possibly delete it; <see cref="CopyFlagsAsync"/> puts them back once the copy
/// is in place.</para>
/// <para>Any error copyfile reports to the status callback stops the copy (nothing is retried or ignored). Errors
/// are typed by errno (<see cref="SystemErrors"/>): a full disk is an <see cref="IOException"/> with HResult 28,
/// EACCES/EPERM an <see cref="UnauthorizedAccessException"/>, ENOENT a <see cref="FileNotFoundException"/>, anything
/// else an <see cref="IOException"/>; the message is <c>strerror(errno)</c>.</para>
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

    /// <exception cref="IOException">The copy failed (typed by errno, see the class); the message is
    /// <c>strerror(errno)</c>.</exception>
    /// <exception cref="UnauthorizedAccessException">Access was denied (EACCES, EPERM).</exception>
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
            // A locked leftover (from a version that did not unlock its part files) cannot be unlinked by copyfile.
            ClearBlockingFlags(destinationPath);
            try
            {
                Copy(sourcePath, destinationPath, _flags, bytesCopied, pauseCheckpoint, ct);
                // copyfile gave the copy the source's flags; a locked part could not be timed, renamed or removed.
                ClearBlockingFlags(destinationPath);
            }
            catch
            {
                try
                {
                    ClearBlockingFlags(destinationPath); // so that the caller can remove what is left
                }
                catch (Exception)
                {
                    // The original failure is the one to report.
                }
                throw;
            }
        }, ct);
    }

    /// <exception cref="IOException">Reading or setting the flags failed.</exception>
    public Task CopyFlagsAsync(string sourcePath, string destinationPath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourcePath);
        ArgumentException.ThrowIfNullOrEmpty(destinationPath);
        return Task.Run(() =>
        {
            using var sourceAccess = _broker.Access(sourcePath);
            using var destinationAccess = _broker.Access(destinationPath);
            var wanted = FlagsOf(sourcePath) & BlockingFlags;
            if (wanted == 0) return;
            var current = FlagsOf(destinationPath);
            if ((current & wanted) != wanted && LibC.lchflags(destinationPath, current | wanted) != 0)
                throw Error(Marshal.GetLastPInvokeError());
        }, ct);
    }

    /// <exception cref="IOException">copyfile failed (e.g. the destination volume cannot store an attribute or
    /// ACL).</exception>
    public Task CopyDirectoryMetadataAsync(string sourcePath, string destinationPath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourcePath);
        ArgumentException.ThrowIfNullOrEmpty(destinationPath);
        return Task.Run(() =>
        {
            using var sourceAccess = _broker.Access(sourcePath);
            using var destinationAccess = _broker.Access(destinationPath);
            // No COPYFILE_RECURSIVE and no data: only the directory's own metadata.
            if (LibC.copyfile(sourcePath, destinationPath, IntPtr.Zero, LibC.COPYFILE_METADATA | LibC.COPYFILE_NOFOLLOW)
                < 0)
                throw Error(Marshal.GetLastPInvokeError());
        }, ct);
    }

    /// <summary>The flags a copy must not carry while the caller still writes, times and renames it.</summary>
    private const uint BlockingFlags = LibC.UF_IMMUTABLE | LibC.UF_APPEND;

    private static uint FlagsOf(string path) =>
        LibC.lstat(path, out var stat) == 0 ? stat.Flags : throw Error(Marshal.GetLastPInvokeError());

    /// <summary>Takes the lock and append-only flags off <paramref name="path"/> (the link itself, if it is one);
    /// nothing when it does not exist.</summary>
    private static void ClearBlockingFlags(string path)
    {
        if (LibC.lstat(path, out var stat) != 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            if (errno == SystemErrors.ENOENT) return;
            throw Error(errno);
        }
        if ((stat.Flags & BlockingFlags) != 0 && LibC.lchflags(path, stat.Flags & ~BlockingFlags) != 0)
            throw Error(Marshal.GetLastPInvokeError());
    }

    /// <summary>The exception for a failed system call, typed by its errno.</summary>
    private static Exception Error(int errno) => SystemErrors.FromErrno(errno);

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
                throw Error(Marshal.GetLastPInvokeError());

            var result = LibC.copyfile(sourcePath, destinationPath, state, flags);
            var errno = Marshal.GetLastPInvokeError();

            context.Failure?.Throw();
            if (result < 0)
            {
                // After COPYFILE_QUIT copyfile returns -1 and leaves errno as it was, so the callback's own record
                // says why: a cancellation, or the error it was told about (errno as it was at that moment).
                if (context.QuitForCancellation) throw new OperationCanceledException(ct);
                throw Error(context.ErrorErrno is int seen and not 0 ? seen : errno);
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
