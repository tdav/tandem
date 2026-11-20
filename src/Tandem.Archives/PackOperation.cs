using System.Buffers;
using System.Text;
using SharpCompress.Common;
using SharpCompress.Common.Tar.Headers;
using SharpCompress.Writers.Tar;
using SharpCompress.Writers.Zip;
using Tandem.Core.Operations;
using Tandem.Core.Vfs;

namespace Tandem.Archives;

/// <summary>
/// Packs <c>sources</c> into a new archive <c>archiveFile</c> of <c>format</c>
/// (one of <see cref="ArchiveFormats.Creatable"/>).
/// <para>Refused before anything is written (<see cref="OperationRejectedException"/>): a format that cannot be
/// created (<see cref="RejectionReason.FormatNotWritable"/>), an archive file inside a source
/// (<see cref="RejectionReason.DestinationInsideSource"/>), a root as a source or two sources of one name
/// (<see cref="RejectionReason.InvalidName"/>), and an archive file that exists already (<see cref="RejectionReason.DestinationExists"/>).</para>
/// <para>Scans as a copy does (<see cref="OperationState.Scanning"/>: folders recursively, links not followed), then
/// writes (<see cref="OperationState.Running"/>) with totals of files and bytes. Entries are named from the folder of
/// the sources: <c>name</c>, <c>name/inner/file</c>; every folder, empty or not, is an entry of its own, before what it
/// holds; times are kept; names are UTF-8. Links are not packed: each is in the summary as
/// <see cref="SkipReason.SymlinksNotSupported"/>. A zip stores folders and compresses files with Deflate; a zip that
/// needs Zip64 on a stream that does not seek is built in a file of <c>temp</c> first.</para>
/// <para>The archive is written through <see cref="SafeReplace.WriteAsync"/>: until it is complete it exists only as
/// <c>.{name}.tandem-part</c>, which a cancel or a failure removes, so neither leaves an archive under the real name
/// or a temporary file. Files are read in blocks of at most 1 MiB, pausing and cancelling between blocks. A file that
/// cannot be opened is put to <c>interaction</c> (retry, skip, skip all, abort); an error once its content
/// is being written, or a file whose size differs from the scanned one, ends the operation as failed. The work runs
/// in a <see cref="BatchScope"/> over the sources and the archive's folder, so an archive packed into a writable
/// archive lands there in one rewrite.</para>
/// </summary>
public sealed class PackOperation : FileOperation
{
    private const int BlockSize = 1024 * 1024;

    private readonly ProviderRegistry _providers;
    private readonly IReadOnlyList<VfsPath> _sources;
    private readonly VfsPath _archiveFile;
    private readonly VfsPath _folder;
    private readonly ArchiveFormat _format;
    private readonly IOperationInteraction _interaction;
    private readonly TempFileStore _temp;

    /// <exception cref="ArgumentException"><paramref name="archiveFile"/> is a root.</exception>
    public PackOperation(ProviderRegistry providers, IReadOnlyList<VfsPath> sources, VfsPath archiveFile,
        ArchiveFormat format, IOperationInteraction interaction, TempFileStore temp)
    {
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
        _sources = Array.AsReadOnly(sources?.ToArray() ?? throw new ArgumentNullException(nameof(sources)));
        _folder = archiveFile.Parent ??
                  throw new ArgumentException($"'{archiveFile}' is a root, not a file.", nameof(archiveFile));
        _archiveFile = archiveFile;
        _format = format;
        _interaction = interaction ?? throw new ArgumentNullException(nameof(interaction));
        _temp = temp ?? throw new ArgumentNullException(nameof(temp));
    }

    public override OperationKind Kind => OperationKind.Pack;

    public override int ItemCount => _sources.Count;

    public override IReadOnlyList<VfsPath> Sources => _sources;

    /// <summary>The folder the archive file goes to.</summary>
    public override VfsPath? Destination => _folder;

    /// <summary>The archive file being created.</summary>
    public VfsPath ArchiveFile => _archiveFile;

    public ArchiveFormat Format => _format;

    /// <summary>Writes a zip in Zip64 form whatever its size. A seam for tests.</summary>
    internal bool AlwaysZip64 { get; init; }

    /// <summary>"{provider id}:{volume key}" for every source and the archive's folder, without duplicates.</summary>
    public override async Task<IReadOnlyCollection<string>> GetVolumeKeysAsync(CancellationToken ct)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in _sources.Append(_folder))
            keys.Add($"{path.ProviderId}:{await _providers.For(path).GetVolumeKeyAsync(path, ct).ConfigureAwait(false)}");
        return keys;
    }

    protected override Task ExecuteAsync(CancellationToken ct)
    {
        RejectInvalidRequest();
        return RunInBatchAsync(_providers, _sources.Append(_folder), _ => PackAsync(ct), afterCommit: null, ct);
    }

    /// <exception cref="OperationRejectedException">The format cannot be created, a source is a root, two sources
    /// share a name (ignoring case), or the archive file lies in a source.</exception>
    private void RejectInvalidRequest()
    {
        if (!ArchiveFormats.Creatable.Contains(_format))
            throw new OperationRejectedException(RejectionReason.FormatNotWritable,
                $"Archives of the format {_format} cannot be created.", destination: _archiveFile);
        // Ignoring case: unpacked on a case-insensitive disk (APFS by default) the two would be one item.
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in _sources)
        {
            if (!source.IsRoot && !names.Add(source.Name))
                throw new OperationRejectedException(RejectionReason.InvalidName,
                    $"Two sources are named '{source.Name}': they would be two entries of one name.", source,
                    _archiveFile, source.Name);
            if (source.IsRoot)
                throw new OperationRejectedException(RejectionReason.InvalidName,
                    $"The root '{source}' has no name to pack it under.", source, _archiveFile, source.Name);
            if (_archiveFile.IsSameOrDescendantOf(source))
                throw new OperationRejectedException(RejectionReason.DestinationInsideSource,
                    $"'{_archiveFile.Path}' lies inside '{source.Path}', which is packed into it.", source,
                    _archiveFile);
        }
    }

    private async Task PackAsync(CancellationToken ct)
    {
        var target = _providers.For(_archiveFile);
        if (await target.GetEntryAsync(_archiveFile, ct).ConfigureAwait(false) is not null)
            throw new OperationRejectedException(RejectionReason.DestinationExists,
                $"'{_archiveFile.Path}' already exists.", destination: _archiveFile);

        var errors = new ItemErrors(this, _interaction);
        SetState(OperationState.Scanning);
        var (items, bytes) = await ScanAsync(errors, ct).ConfigureAwait(false);

        SetState(OperationState.Running);
        await SafeReplace.WriteAsync(target, _archiveFile, replace: false,
            part => WriteArchiveAsync(target, part, items, bytes, errors, ct)).ConfigureAwait(false);
    }

    // ---- Scanning -----------------------------------------------------------------------------------------

    /// <summary>One entry of the archive: what it is made from and its name in the archive.</summary>
    private sealed record PackItem(FileEntry Source, string Name);

    /// <summary>Walks the sources (links are not followed), each folder before what it holds, and reports the totals:
    /// files and links count as files, only files' sizes as bytes. A folder that cannot be listed goes to the error
    /// question; skipping it leaves it out with everything inside.</summary>
    /// <exception cref="VfsNotFoundException">A source does not exist.</exception>
    private async Task<(List<PackItem> Items, long Bytes)> ScanAsync(ItemErrors errors, CancellationToken ct)
    {
        var items = new List<PackItem>();
        long files = 0, bytes = 0;
        foreach (var source in _sources)
        {
            var entry = await _providers.For(source).GetEntryAsync(source, ct).ConfigureAwait(false)
                        ?? throw new VfsNotFoundException($"'{source.Path}' does not exist.");
            await AddAsync(entry, source.Name).ConfigureAwait(false);
        }
        SetTotals(files, bytes);
        return (items, bytes);

        async Task AddAsync(FileEntry entry, string name)
        {
            await WaitWhilePausedAsync(ct).ConfigureAwait(false);
            if (entry.Kind == EntryKind.Directory)
            {
                var children = await ListChildrenAsync(entry.Path, errors, ct).ConfigureAwait(false);
                if (children is null) return; // skipped, with everything inside it
                items.Add(new PackItem(entry, name));
                foreach (var child in children)
                    await AddAsync(child, name + "/" + child.Name).ConfigureAwait(false);
                return;
            }
            items.Add(new PackItem(entry, name));
            files++;
            if (entry.Kind == EntryKind.File) bytes += entry.Size;
        }
    }

    /// <summary>The folder's entries by name, or null when listing failed and the user skipped it.</summary>
    private async Task<List<FileEntry>?> ListChildrenAsync(VfsPath folder, ItemErrors errors, CancellationToken ct)
    {
        while (true)
        {
            try
            {
                var children = new List<FileEntry>();
                await foreach (var child in _providers.For(folder).EnumerateAsync(folder, ct).ConfigureAwait(false))
                    children.Add(child);
                children.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
                return children;
            }
            catch (Exception e) when (ItemErrors.IsItemError(e))
            {
                if (!await errors.ShouldRetryAsync(folder, e, ct).ConfigureAwait(false)) return null;
            }
        }
    }

    // ---- Writing ------------------------------------------------------------------------------------------

    /// <summary>Writes the archive to <paramref name="part"/>. If anything fails, the part's stream is closed without
    /// being applied (<see cref="ArchiveProvider.AbortAsync"/>) and the error passes on; the caller removes the
    /// part.</summary>
    private async Task WriteArchiveAsync(IFileSystemProvider target, VfsPath part, List<PackItem> items, long bytes,
        ItemErrors errors, CancellationToken ct)
    {
        var output = await target.OpenWriteAsync(part, ct).ConfigureAwait(false);
        try
        {
            var zip64 = _format == ArchiveFormat.Zip && (AlwaysZip64 || ArchiveProvider.NeedsZip64(items.Count, bytes));
            if (zip64 && !output.CanSeek)
                await WriteThroughTemporaryFileAsync(output, part.Name, items, errors, ct).ConfigureAwait(false);
            else
                await WriteEntriesAsync(output, items, zip64, errors, ct).ConfigureAwait(false);
            await output.FlushAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await ArchiveProvider.AbortAsync(output).ConfigureAwait(false);
            throw;
        }
        await output.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>The zip writer writes Zip64 only to a stream that seeks: the archive is built in a temporary file and
    /// then copied to <paramref name="output"/>. The temporary file is removed in any case.</summary>
    private async Task WriteThroughTemporaryFileAsync(Stream output, string name, List<PackItem> items,
        ItemErrors errors, CancellationToken ct)
    {
        var local = _temp.NewFile(name);
        try
        {
            await using var file = new FileStream(local, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                BlockSize, useAsync: true);
            await WriteEntriesAsync(file, items, zip64: true, errors, ct).ConfigureAwait(false);
            file.Position = 0;
            var buffer = ArrayPool<byte>.Shared.Rent(BlockSize);
            try
            {
                int read;
                while ((read = await file.ReadAsync(buffer.AsMemory(0, BlockSize), ct).ConfigureAwait(false)) > 0)
                {
                    await WaitWhilePausedAsync(ct).ConfigureAwait(false);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        finally
        {
            try
            {
                _temp.Release(local);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // the store's next Clear removes it
            }
        }
    }

    /// <summary>Writes every entry and the archive's end. After a failure the writer is not closed: closing would
    /// write the archive's end, which can only hide the error (the part is thrown away anyway).</summary>
    private async Task WriteEntriesAsync(Stream output, List<PackItem> items, bool zip64, ItemErrors errors,
        CancellationToken ct)
    {
        var encoding = new ArchiveEncoding { Default = Encoding.UTF8 };
        if (_format == ArchiveFormat.Zip)
        {
            // Stored by default, Deflate per file: a folder written under a Deflate default is marked deflated with
            // no data, which other tools (unzip) reject.
            var zip = new ZipWriter(output, new ZipWriterOptions(CompressionType.None)
            {
                LeaveStreamOpen = true,
                ArchiveEncoding = encoding,
                UseZip64 = zip64,
            });
            await WriteEachAsync(items, errors, ct,
                (item, time) =>
                {
                    zip.WriteDirectory(item.Name, ArchiveProvider.ZipTime(time));
                    return Task.CompletedTask;
                },
                async (item, content) =>
                {
                    var entry = zip.WriteToStream(item.Name, new ZipWriterEntryOptions
                    {
                        CompressionType = CompressionType.Deflate,
                        ModificationDateTime = ArchiveProvider.ZipTime(item.Source.Modified),
                        EnableZip64 = ArchiveProvider.EntryNeedsZip64(item.Source.Size),
                    });
                    await content.CopyToAsync(entry, BlockSize, ct).ConfigureAwait(false);
                    entry.Dispose(); // ends the entry; not reached after a failure
                }).ConfigureAwait(false);
            zip.Dispose(); // the central directory
            return;
        }

        var compression = _format == ArchiveFormat.TarGz ? CompressionType.GZip : CompressionType.None;
        var tar = new TarWriter(output, new TarWriterOptions(compression, finalizeArchiveOnClose: true,
            TarHeaderWriteFormat.GNU_TAR_LONG_LINK)
        {
            LeaveStreamOpen = true,
            ArchiveEncoding = encoding,
        });
        await WriteEachAsync(items, errors, ct,
            (item, time) => tar.WriteDirectoryAsync(item.Name, time.UtcDateTime, ct),
            (item, content) => tar.WriteAsync(item.Name, content, item.Source.Modified.UtcDateTime, item.Source.Size,
                ct)).ConfigureAwait(false);
        tar.Dispose(); // the end blocks and, for tar.gz, the end of the compressed stream
    }

    /// <summary>Goes through the plan: folders and files to <paramref name="writeFolder"/> and
    /// <paramref name="writeFile"/> (a file's content as a <see cref="SourceStream"/> that pauses, cancels and counts
    /// between blocks), links to the summary.</summary>
    private async Task WriteEachAsync(List<PackItem> items, ItemErrors errors, CancellationToken ct,
        Func<PackItem, DateTimeOffset, Task> writeFolder, Func<PackItem, Stream, Task> writeFile)
    {
        foreach (var item in items)
        {
            await WaitWhilePausedAsync(ct).ConfigureAwait(false);
            var source = item.Source;
            BeginItem(source.Path.Path);
            switch (source.Kind)
            {
                case EntryKind.Directory:
                    await writeFolder(item, source.Modified).ConfigureAwait(false);
                    break;
                case EntryKind.File:
                    if (await OpenSourceAsync(source, errors, ct).ConfigureAwait(false) is not { } input)
                    {
                        DropBytesFromTotal(source.Size);
                        CompleteFile();
                        break;
                    }
                    await using (var content = new SourceStream(this, input, ct))
                    {
                        await writeFile(item, content).ConfigureAwait(false);
                        // A tar writer reads only the scanned size: a byte beyond it means the file grew.
                        if (content.BytesRead == source.Size)
                            await content.ReadAsync(new byte[1], ct).ConfigureAwait(false);
                        if (content.BytesRead != source.Size)
                            throw new VfsException(
                                $"'{source.Path.Path}' changed while it was packed: {content.BytesRead} bytes were " +
                                $"read, {source.Size} expected.");
                    }
                    CompleteFile();
                    break;
                default: // a link
                    AddSkipped(new SkippedItem(source.Path, SkipReason.SymlinksNotSupported));
                    CompleteFile();
                    break;
            }
        }
    }

    /// <summary>Opens a file to pack, asking about an error; null when the user skipped it (recorded).</summary>
    private async Task<Stream?> OpenSourceAsync(FileEntry source, ItemErrors errors, CancellationToken ct)
    {
        Stream? input = null;
        var opened = await errors.RunAsync(source.Path, async () =>
            input = await _providers.For(source.Path).OpenReadAsync(source.Path, ct).ConfigureAwait(false), ct)
            .ConfigureAwait(false);
        return opened ? input : null;
    }

    /// <summary>A file being packed, as the archive writer reads it: at most 1 MiB a read, a pause checkpoint and a
    /// cancellation check before each, and every byte read counted in the operation's progress.</summary>
    private sealed class SourceStream(PackOperation operation, Stream inner, CancellationToken ct) : Stream
    {
        public long BytesRead { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await operation.WaitWhilePausedAsync(ct).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var read = await inner.ReadAsync(buffer[..Math.Min(buffer.Length, BlockSize)], ct).ConfigureAwait(false);
            Count(read);
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        /// <summary>For a writer that reads synchronously: cancellation is still checked between blocks; a pause
        /// takes effect at the next asynchronous read or entry.</summary>
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            ct.ThrowIfCancellationRequested();
            var read = inner.Read(buffer[..Math.Min(buffer.Length, BlockSize)]);
            Count(read);
            return read;
        }

        private void Count(int read)
        {
            BytesRead += read;
            if (read > 0) operation.AddBytesDone(read);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }
}
