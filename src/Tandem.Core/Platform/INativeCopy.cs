namespace Tandem.Core.Platform;

/// <summary>The platform's own file copy (e.g. clonefile/copyfile on macOS), used only for local → local copies.</summary>
public interface INativeCopy
{
    /// <summary>Copies one file. <paramref name="destinationPath"/> must be a private temporary path of the
    /// caller's (such as a <c>.tandem-part</c> file), which the caller renames into place afterwards: whatever is
    /// already there — a stale leftover, even a symbolic link — is replaced (the link itself, never its target).
    /// <paramref name="bytesCopied"/> receives the running total of bytes copied for this file.
    /// <para><paramref name="pauseCheckpoint"/> is awaited between the blocks of the copy (when the platform copies
    /// in blocks at all; an instant clone has none): while the caller's operation is paused it does not complete, so
    /// the copy stands still with its files open; it throws <see cref="OperationCanceledException"/> when the
    /// operation is cancelled, which stops the copy.</para></summary>
    Task CopyFileAsync(string sourcePath, string destinationPath, IProgress<long> bytesCopied,
        Func<CancellationToken, ValueTask> pauseCheckpoint, CancellationToken ct);
}
