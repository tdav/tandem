namespace Tandem.Core.Platform;

/// <summary>The platform's own file copy (e.g. clonefile/copyfile on macOS), used only for local → local copies.</summary>
public interface INativeCopy
{
    /// <summary>Copies one file. <paramref name="destinationPath"/> does not exist yet; the caller renames it into
    /// place afterwards. <paramref name="bytesCopied"/> receives the running total of bytes copied for this file.</summary>
    Task CopyFileAsync(string sourcePath, string destinationPath, IProgress<long> bytesCopied, CancellationToken ct);
}
