using SharpCompress.Archives.Zip;
using SharpCompress.Readers;
using Tandem.Core.Operations;
using Tandem.Core.Vfs;

namespace Tandem.Archives;

/// <summary>What an archive writer was asked to write, checked against what the written archive lists: every entry
/// by its name (a folder's ends in '/'), with its kind and size. A writer that stores a name other than the one given,
/// or leaves an entry out, makes the check fail, so the archive is never put in place.</summary>
internal sealed class WrittenListing
{
    private const int CopyBufferSize = 81920;

    private readonly Dictionary<string, (bool Directory, long Size)> _entries = new(StringComparer.Ordinal);

    public int Count => _entries.Count;

    public void AddDirectory(string name) => _entries[Key(name, directory: true)] = (true, 0);

    public void AddFile(string name, long size) => _entries[Key(name, directory: false)] = (false, size);

    private static string Key(string name, bool directory) =>
        directory ? name.TrimEnd('/') + "/" : name;

    /// <summary>Reads <paramref name="file"/> of <paramref name="host"/> back as <paramref name="format"/> and
    /// compares its listing with what was written. A stream that does not seek is copied to a file of
    /// <paramref name="temp"/> first (removed afterwards). <paramref name="what"/> names the archive in the messages,
    /// "The rewritten 'a.zip'".</summary>
    /// <exception cref="VfsException">The archive cannot be read back, or lists other entries.</exception>
    public async Task VerifyAsync(IFileSystemProvider host, VfsPath file, ArchiveFormat format, TempFileStore temp,
        string what, CancellationToken ct)
    {
        var stream = await host.OpenReadAsync(file, ct).ConfigureAwait(false);
        string? copy = null;
        try
        {
            if (!stream.CanSeek)
            {
                copy = temp.NewFile("check");
                await using (var local = new FileStream(copy, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                                 CopyBufferSize, useAsync: true))
                    await stream.CopyToAsync(local, CopyBufferSize, ct).ConfigureAwait(false);
                await stream.DisposeAsync().ConfigureAwait(false);
                stream = new FileStream(copy, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, CopyBufferSize,
                    useAsync: false);
            }
            Dictionary<string, (bool, long)> listed;
            try
            {
                listed = List(stream, format);
            }
            catch (Exception e) when (e is not (OperationCanceledException or VfsException))
            {
                throw new VfsException($"{what} cannot be read back; it was not put in place.", e);
            }
            if (listed.Count != _entries.Count ||
                _entries.Any(w => !listed.TryGetValue(w.Key, out var found) || found != w.Value))
                throw new VfsException($"{what} does not list what was written; it was not put in place.");
        }
        finally
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            if (copy is not null)
            {
                try
                {
                    temp.Release(copy);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // the store's next Clear removes it
                }
            }
        }
    }

    private static Dictionary<string, (bool, long)> List(Stream stream, ArchiveFormat format)
    {
        var listed = new Dictionary<string, (bool, long)>(StringComparer.Ordinal);
        var options = ArchiveProvider.Options(password: null);
        if (format == ArchiveFormat.Zip)
        {
            using var archive = ZipArchive.Open(stream, options);
            foreach (var entry in archive.Entries)
                Add(entry.Key, entry.IsDirectory, entry.Size);
            return listed;
        }
        using var reader = ReaderFactory.Open(stream, options);
        while (reader.MoveToNextEntry())
            Add(reader.Entry.Key, reader.Entry.IsDirectory, reader.Entry.Size);
        return listed;

        void Add(string? name, bool directory, long size)
        {
            var key = Key(name ?? "", directory);
            if (!listed.TryAdd(key, (directory, directory ? 0 : size)))
                throw new VfsException($"'{key}' is in the written archive twice.");
        }
    }
}
