using System.Buffers;

namespace Tandem.Core.Platform.Portable;

/// <summary>
/// A managed <see cref="INativeCopy"/> for platforms without a system copy of their own (Windows): the data in
/// 1 MiB blocks through <see cref="FileStream"/>, then the creation and last-write times and — on Windows — the
/// attributes without <see cref="FileAttributes.ReadOnly"/> (which <see cref="CopyFlagsAsync"/> adds once the copy is
/// in place), on Unix the <see cref="UnixFileMode"/>. Alternate streams and ACLs are not copied, except the
/// <c>Zone.Identifier</c> stream by <see cref="CopyFileMetadataAsync"/> on Windows.
/// <para>A leftover at the destination is deleted first: a link itself (never its target), a read-only file after
/// taking its read-only attribute off; a directory there is an <see cref="IOException"/>. On a failure or a
/// cancellation the partial copy is deleted. Errors are .NET's own file exceptions
/// (<see cref="FileNotFoundException"/>, <see cref="UnauthorizedAccessException"/>, <see cref="IOException"/>).</para>
/// </summary>
public sealed class PortableNativeCopy : INativeCopy
{
    /// <summary>The size of the blocks the data is copied in, with the checkpoint awaited after each.</summary>
    public const int BlockSize = 1024 * 1024;

    /// <summary>The Windows attributes a file or directory can be given (<c>SetFileAttributesW</c>).</summary>
    private const FileAttributes Settable = FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System |
                                            FileAttributes.Archive | FileAttributes.Temporary |
                                            FileAttributes.Offline | FileAttributes.NotContentIndexed;

    private const string ZoneStream = ":Zone.Identifier";

    public Task CopyFileAsync(string sourcePath, string destinationPath, IProgress<long> bytesCopied,
        Func<CancellationToken, ValueTask> pauseCheckpoint, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourcePath);
        ArgumentException.ThrowIfNullOrEmpty(destinationPath);
        ArgumentNullException.ThrowIfNull(bytesCopied);
        ArgumentNullException.ThrowIfNull(pauseCheckpoint);
        if (ct.IsCancellationRequested) return Task.FromCanceled(ct);
        return Task.Run(() => CopyAsync(sourcePath, destinationPath, bytesCopied, pauseCheckpoint, ct), ct);
    }

    public Task CopyFlagsAsync(string sourcePath, string destinationPath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourcePath);
        ArgumentException.ThrowIfNullOrEmpty(destinationPath);
        if (!OperatingSystem.IsWindows()) return ct.IsCancellationRequested ? Task.FromCanceled(ct) : Task.CompletedTask;
        return Task.Run(() =>
        {
            if (!File.GetAttributes(sourcePath).HasFlag(FileAttributes.ReadOnly)) return;
            var current = File.GetAttributes(destinationPath);
            if (!current.HasFlag(FileAttributes.ReadOnly))
                File.SetAttributes(destinationPath, (current & Settable) | FileAttributes.ReadOnly);
        }, ct);
    }

    public Task CopyDirectoryMetadataAsync(string sourcePath, string destinationPath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourcePath);
        ArgumentException.ThrowIfNullOrEmpty(destinationPath);
        return Task.Run(() =>
        {
            if (OperatingSystem.IsWindows())
                File.SetAttributes(destinationPath, Normal(File.GetAttributes(sourcePath) & Settable));
            else
                File.SetUnixFileMode(destinationPath, File.GetUnixFileMode(sourcePath));
            // Last: on Windows a directory's times would survive the attributes anyway, but on Unix a mode change
            // does not touch them either; the order only keeps both platforms alike.
            Directory.SetCreationTimeUtc(destinationPath, Directory.GetCreationTimeUtc(sourcePath));
            Directory.SetLastWriteTimeUtc(destinationPath, Directory.GetLastWriteTimeUtc(sourcePath));
        }, ct);
    }

    /// <remarks>On Unix there is nothing to carry (no extended attributes or ACL here); the caller sets the mode
    /// itself.</remarks>
    public Task CopyFileMetadataAsync(string sourcePath, string destinationPath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourcePath);
        ArgumentException.ThrowIfNullOrEmpty(destinationPath);
        if (!OperatingSystem.IsWindows()) return ct.IsCancellationRequested ? Task.FromCanceled(ct) : Task.CompletedTask;
        return Task.Run(() =>
        {
            var source = File.GetAttributes(sourcePath) & Settable & ~FileAttributes.ReadOnly;
            var readOnly = File.GetAttributes(destinationPath) & FileAttributes.ReadOnly;
            CopyZoneStream(sourcePath, destinationPath);
            File.SetAttributes(destinationPath, Normal(source | readOnly));
        }, ct);
    }

    private static async Task CopyAsync(string sourcePath, string destinationPath, IProgress<long> bytesCopied,
        Func<CancellationToken, ValueTask> pauseCheckpoint, CancellationToken ct)
    {
        DeleteLeftover(destinationPath);

        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, bufferSize: 0, FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            var buffer = ArrayPool<byte>.Shared.Rent(BlockSize);
            try
            {
                // CreateNew: a link raced in after the deletion is refused, never written through.
                await using var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, bufferSize: 0, FileOptions.Asynchronous);
                var total = 0L;
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    var read = await source.ReadAtLeastAsync(buffer.AsMemory(0, BlockSize), BlockSize,
                        throwOnEndOfStream: false, ct).ConfigureAwait(false);
                    if (read == 0) break;
                    await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    total += read;
                    bytesCopied.Report(total);
                    if (read < BlockSize) break;
                    await pauseCheckpoint(ct).ConfigureAwait(false);
                }
                if (total == 0) bytesCopied.Report(0);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            CopyTimesAndAttributes(sourcePath, destinationPath);
        }
        catch
        {
            TryDelete(destinationPath);
            throw;
        }
    }

    private static void CopyTimesAndAttributes(string sourcePath, string destinationPath)
    {
        File.SetCreationTimeUtc(destinationPath, File.GetCreationTimeUtc(sourcePath));
        File.SetLastWriteTimeUtc(destinationPath, File.GetLastWriteTimeUtc(sourcePath));
        if (OperatingSystem.IsWindows())
            File.SetAttributes(destinationPath,
                Normal(File.GetAttributes(sourcePath) & Settable & ~FileAttributes.ReadOnly));
        else
            File.SetUnixFileMode(destinationPath, File.GetUnixFileMode(sourcePath));
    }

    /// <summary>Deletes what is at <paramref name="path"/>: a link itself, or a file (read-only or not); nothing when
    /// nothing is there.</summary>
    /// <exception cref="IOException">A directory is there.</exception>
    private static void DeleteLeftover(string path)
    {
        var info = new FileInfo(path);
        if (info.LinkTarget is not null)
        {
            // On Windows a link to a directory is a directory entry, removed with RemoveDirectory (never recursive);
            // on Unix unlink removes any link.
            if (OperatingSystem.IsWindows() && info.Attributes.HasFlag(FileAttributes.Directory))
                Directory.Delete(path);
            else
                File.Delete(path);
            return;
        }
        if (Directory.Exists(path))
            throw new IOException($"'{path}' is a directory.");
        if (!info.Exists) return;
        if (OperatingSystem.IsWindows() && info.Attributes.HasFlag(FileAttributes.ReadOnly))
            info.Attributes &= ~FileAttributes.ReadOnly;
        info.Delete();
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The original failure is the one to report.
        }
    }

    /// <summary>Copies the NTFS <c>Zone.Identifier</c> stream when the source has one; best effort (a volume
    /// without streams, or a stream that cannot be read, is passed over).</summary>
    private static void CopyZoneStream(string sourcePath, string destinationPath)
    {
        try
        {
            if (!File.Exists(sourcePath + ZoneStream)) return;
            File.WriteAllBytes(destinationPath + ZoneStream, File.ReadAllBytes(sourcePath + ZoneStream));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Best effort.
        }
    }

    private static FileAttributes Normal(FileAttributes attributes) =>
        attributes == 0 ? FileAttributes.Normal : attributes;
}
