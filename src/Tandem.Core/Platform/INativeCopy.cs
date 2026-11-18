namespace Tandem.Core.Platform;

/// <summary>The platform's own file copy (e.g. clonefile/copyfile on macOS), used only for local → local copies.</summary>
public interface INativeCopy
{
    /// <summary>Copies one file. <paramref name="destinationPath"/> must be a private temporary path of the
    /// caller's (such as a <c>.tandem-part</c> file), which the caller renames into place afterwards: whatever is
    /// already there — a stale leftover, even a symbolic link — is replaced (the link itself, never its target).
    /// <paramref name="bytesCopied"/> receives the running total of bytes copied for this file.</summary>
    Task CopyFileAsync(string sourcePath, string destinationPath, IProgress<long> bytesCopied, CancellationToken ct);
}
