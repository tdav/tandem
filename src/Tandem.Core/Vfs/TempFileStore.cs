namespace Tandem.Core.Vfs;

/// <summary>
/// The app's temporary files — files of a provider copied to the disk so that another program, Quick Look or the
/// viewer can open them. Every file gets a folder of its own, <c>{Directory}/{guid}/{name}</c>, so it keeps its name
/// and two files of the same name do not meet. The store owns its directory (in the app:
/// <see cref="DefaultDirectory"/>): it is emptied when the store is created, on <see cref="Clear"/> and on
/// <see cref="Dispose"/>. This is one of the app's own folders, so it is written directly, not through a provider.
/// </summary>
public sealed class TempFileStore : IDisposable
{
    private const int BufferSize = 81920;

    /// <summary>Creates <paramref name="directory"/> if needed and removes whatever an earlier run left in
    /// it.</summary>
    public TempFileStore(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        Directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        System.IO.Directory.CreateDirectory(Directory);
        Clear();
    }

    /// <summary>The full path of the store's directory.</summary>
    public string Directory { get; }

    /// <summary>The app's store directory: <c>~/Library/Caches/Tandem/tmp</c>.</summary>
    public static string DefaultDirectory(string homeDirectory) =>
        Path.Combine(homeDirectory, "Library", "Caches", "Tandem", "tmp");

    /// <summary>A new path <c>{Directory}/{guid}/{name}</c>. The folder is created, the file is not.</summary>
    /// <exception cref="ArgumentException"><paramref name="name"/> is not a single file name.</exception>
    public string NewFile(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length == 0 || name == "." || name == ".." || name.Contains('/') || name.Contains('\0'))
            throw new ArgumentException($"'{name}' is not a single file name.", nameof(name));
        var folder = Path.Combine(Directory, Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(folder);
        return Path.Combine(folder, name);
    }

    /// <summary>Copies <paramref name="file"/> of <paramref name="provider"/> to a new temporary file of the same name
    /// and returns its path. <paramref name="bytes"/> gets the number of bytes copied so far after each block. On
    /// cancellation or an error the partly copied file is removed; if even that fails, the original error or
    /// cancellation is still what is thrown, and the leftover goes with the next <see cref="Clear"/>.</summary>
    public async Task<string> MaterializeAsync(IFileSystemProvider provider, VfsPath file, IProgress<long>? bytes,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var target = NewFile(file.Name);
        try
        {
            await using var source = await provider.OpenReadAsync(file, ct).ConfigureAwait(false);
            await using var destination = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                BufferSize, useAsync: true);
            var buffer = new byte[BufferSize];
            long copied = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                copied += read;
                bytes?.Report(copied);
                ct.ThrowIfCancellationRequested();
            }
        }
        catch
        {
            try
            {
                Release(target);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The original error or cancellation stays the outcome; Clear removes the leftover later.
            }
            throw;
        }
        return target;
    }

    /// <summary>Deletes a file made by this store together with its folder. A path that is not of the form
    /// <c>{Directory}/{folder}/{name}</c> is left alone.</summary>
    public void Release(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var full = Path.GetFullPath(path);
        var folder = Path.GetDirectoryName(full);
        if (folder is null || !string.Equals(Path.GetDirectoryName(folder), Directory, StringComparison.Ordinal))
            return;
        DeleteEntry(folder);
    }

    /// <summary>Removes everything in the store's directory (links are removed, not followed).</summary>
    public void Clear()
    {
        System.IO.Directory.CreateDirectory(Directory);
        foreach (var entry in System.IO.Directory.EnumerateFileSystemEntries(Directory))
            DeleteEntry(entry);
    }

    /// <summary>Same as <see cref="Clear"/>.</summary>
    public void Dispose() => Clear();

    private static void DeleteEntry(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists && !System.IO.Directory.Exists(path) && info.LinkTarget is null) return;
        if (info.LinkTarget is null && info.Attributes.HasFlag(FileAttributes.Directory))
            System.IO.Directory.Delete(path, recursive: true);
        else
            File.Delete(path);
    }
}
