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
    /// operation is cancelled, which stops the copy.</para>
    /// <para>The copy carries the source's metadata, except the flags that would stop the caller from finishing
    /// it (the Finder lock, append-only): those are left off until <see cref="CopyFlagsAsync"/>. A leftover at the
    /// destination that has them is replaced all the same.</para></summary>
    Task CopyFileAsync(string sourcePath, string destinationPath, IProgress<long> bytesCopied,
        Func<CancellationToken, ValueTask> pauseCheckpoint, CancellationToken ct);

    /// <summary>Gives <paramref name="destinationPath"/> — a copy made by <see cref="CopyFileAsync"/>, now in place
    /// under its real name — the flags <see cref="CopyFileAsync"/> left off (such as the Finder lock) when
    /// <paramref name="sourcePath"/> has them. Links are not followed.</summary>
    Task CopyFlagsAsync(string sourcePath, string destinationPath, CancellationToken ct);

    /// <summary>Gives the directory <paramref name="destinationPath"/> the metadata of the directory
    /// <paramref name="sourcePath"/> — extended attributes (Finder tags among them), ACL, mode, times and flags —
    /// without copying anything inside it. Links are not followed.</summary>
    Task CopyDirectoryMetadataAsync(string sourcePath, string destinationPath, CancellationToken ct);

    /// <summary>Gives the file <paramref name="destinationPath"/> — new content about to replace
    /// <paramref name="sourcePath"/>, such as a <c>.tandem-part</c> file — the file's identity besides its content:
    /// extended attributes (Finder tags among them), ACL and the user flags such as "hidden". Not its data, times or
    /// mode, and not the flags that would stop the caller from putting it in place (the Finder lock, append-only).
    /// Links are not followed.</summary>
    Task CopyFileMetadataAsync(string sourcePath, string destinationPath, CancellationToken ct);
}
