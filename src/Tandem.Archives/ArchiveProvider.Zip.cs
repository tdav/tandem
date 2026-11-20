using System.Text;
using SharpCompress.Archives.Zip;
using SharpCompress.Common;
using SharpCompress.Writers.Zip;
using Tandem.Core.Operations;
using Tandem.Core.Vfs;

namespace Tandem.Archives;

/// <summary>
/// Changing a zip. A zip without encrypted entries, links or entries its listing renamed or left out
/// (<see cref="ArchiveIndex.RenamedOrDroppedCount"/>), whose host takes writes, offers
/// <see cref="ProviderCapabilities.Write"/>.
/// <para>Changes are collected: a written file's content goes to a file of the <see cref="TempFileStore"/>; deletions,
/// renames (of a folder with its subtree), new folders and times are kept in memory, as a tree of the archive as it
/// will be. Reads see that tree. In a batch the changes are applied by <see cref="IProviderBatch.CommitAsync"/>;
/// outside one, each call applies its own change at once (a write when its stream is closed). Only the flow that holds
/// the batch (<see cref="BatchScope.Holds"/>) adds to it; any other caller waits for the batch to end and then applies
/// its change on its own.</para>
/// <para>Applying them rewrites the archive: the new archive is written through the host provider under
/// <c>.{name}.tandem-part</c> next to it (every entry of the tree, each folder before its contents and as an entry of
/// its own; unchanged files with their content and time; Deflate; UTF-8 names; Zip64 when the entries or the content
/// need it), read back and its listing checked against what was written,
/// and only then put in place by <see cref="SafeReplace.MoveIntoPlaceAsync"/>. If any of that fails, the part file is
/// removed, the original archive is left as it was, the collected changes are dropped, and the error goes to the
/// caller. The provider never begins its host's batch itself; a change outside a batch runs in a short
/// <see cref="BatchScope"/> of its own, which begins the hosts first. So when the host collects changes too (a zip
/// inside a zip), the rewrite lands in the host's batch and the host rewrites once, at the scope's commit.</para>
/// </summary>
public sealed partial class ArchiveProvider
{
    /// <summary>Content this large or larger is written in Zip64 form: 4 GiB, less room for deflate's growth of data
    /// that does not compress.</summary>
    internal const long Zip64Size = 4L * 1024 * 1024 * 1024 - 64L * 1024 * 1024;

    /// <summary>The most entries a zip without Zip64 can count.</summary>
    private const int MaxPlainZipEntries = ushort.MaxValue;

    private static readonly DateTime EarliestZipTime = new(1980, 1, 1, 0, 0, 0, DateTimeKind.Local);

    private readonly Lock _editLock = new();

    /// <summary>Called just before a change that is not part of the open batch begins a batch of its own (and
    /// waits for the open one to end). A seam for tests.</summary>
    internal Action? BeforeOwnBatch { get; set; }

    /// <summary>Called when <see cref="BeginBatchAsync"/> is entered, before it waits for the gate. A seam for
    /// tests.</summary>
    internal Action? BeginningBatch { get; set; }
    private Batch? _openBatch; // guarded by _editLock

    public ProviderCapabilities Capabilities =>
        IsEditable(_snapshot) ? ProviderCapabilities.Write : ProviderCapabilities.None;

    /// <summary>Whether the whole archive uses Zip64: more entries than a plain zip counts, or so much content that
    /// offsets may pass 4 GiB.</summary>
    internal static bool NeedsZip64(int entries, long totalBytes) =>
        entries > MaxPlainZipEntries || totalBytes >= Zip64Size;

    /// <summary>Whether one entry of <paramref name="size"/> bytes is written in Zip64 form.</summary>
    internal static bool EntryNeedsZip64(long size) => size >= Zip64Size;

    // ---- The changing calls --------------------------------------------------------------------------------

    /// <summary>Creates the file, or empties it; the content is the archive's once the stream is closed (outside a
    /// batch: closing it rewrites the archive, and fails if that fails).
    /// <para>Outside a batch the stream holds a <see cref="BatchScope"/> of its own until it is closed. A flow has a
    /// single active scope (<see cref="BatchScope.Holds"/>), and work forked from an operation's flow inherits it; so
    /// a second change to this archive from the same flow while such a stream is open is not part of any batch it
    /// holds and waits for the stream's batch to end — that is, on itself. Close the stream before changing the
    /// archive again from the same flow.</para></summary>
    public async Task<Stream> OpenWriteAsync(VfsPath path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RequireEditable(path);
        if (JoinableBatch() is { } open)
            return CreateFile(await open.EditsAsync(ct).ConfigureAwait(false), path, commitOnClose: null);
        var own = await BeginOwnAsync(ct).ConfigureAwait(false);
        try
        {
            return CreateFile(await own.Batch.EditsAsync(ct).ConfigureAwait(false), path, commitOnClose: own);
        }
        catch
        {
            await own.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public Task CreateDirectoryAsync(VfsPath path, CancellationToken ct) =>
        ChangeAsync(path, edits => CreateDirectory(edits, path), ct);

    public Task DeleteAsync(VfsPath path, bool recursive, CancellationToken ct) =>
        ChangeAsync(path, edits => Delete(edits, path, recursive), ct);

    public Task MoveAsync(VfsPath source, VfsPath destination, CancellationToken ct) =>
        ChangeAsync(source, edits => Move(edits, source, destination), ct);

    public Task SetModifiedAsync(VfsPath path, DateTimeOffset modified, CancellationToken ct) =>
        ChangeAsync(path, edits => SetModified(edits, path, modified), ct);

    /// <summary>A zip keeps no links.</summary>
    public Task CreateSymlinkAsync(VfsPath path, string target, CancellationToken ct) =>
        Task.FromException(NotSupported(path, "links"));

    /// <summary>A zip keeps no permissions here.</summary>
    public Task SetModeAsync(VfsPath path, UnixFileMode mode, CancellationToken ct) =>
        Task.FromException(NotSupported(path, "permissions"));

    private VfsException NotSupported(VfsPath path, string what) => IsEditable(_snapshot)
        ? new VfsException($"'{GetDisplayPath(path)}' is in an archive, which keeps no {what}.")
        : ReadOnly(path);

    private VfsAccessDeniedException ReadOnly(VfsPath path) =>
        new($"'{GetDisplayPath(path)}' is in an archive that cannot be changed.");

    /// <summary>A zip whose every entry the rewrite can carry over as it is, in a host that takes writes. A host
    /// that lives inside another provider must collect changes itself (<see cref="BatchHost"/>): a rewrite is
    /// never written past a middle layer that does not.</summary>
    private bool IsEditable(Snapshot snapshot)
    {
        if (snapshot.Format != ArchiveFormat.Zip || snapshot.HasEncryptedEntries || snapshot.HasLinks ||
            snapshot.Index.RenamedOrDroppedCount != 0)
            return false;
        var host = _providers.TryGet(Host.ProviderId, out var found) ? found : null;
        return host is not null && host.Capabilities.HasFlag(ProviderCapabilities.Write) &&
               (host is not IHostedProvider || BatchHost is not null);
    }

    private void RequireEditable(VfsPath path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsEditable(_snapshot)) throw ReadOnly(path);
    }

    private Batch? OpenBatch()
    {
        lock (_editLock) return _openBatch;
    }

    /// <summary>The open batch when the calling flow is the one that holds it (<see cref="BatchScope.Holds"/>);
    /// null otherwise. A change from any other caller waits for the batch to end and is applied on its own, so it is
    /// never lost with a batch that fails or is dropped.</summary>
    private Batch? JoinableBatch() => BatchScope.Holds(this) ? OpenBatch() : null;

    /// <summary>Applies <paramref name="change"/> (true when it changed something) to the open batch the caller
    /// holds, or else in a batch of its own that is committed right away.</summary>
    private async Task ChangeAsync(VfsPath path, Func<Edits, bool> change, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RequireEditable(path);
        if (JoinableBatch() is { } open)
        {
            Apply(await open.EditsAsync(ct).ConfigureAwait(false), change);
            return;
        }
        await using var own = await BeginOwnAsync(ct).ConfigureAwait(false);
        Apply(await own.Batch.EditsAsync(ct).ConfigureAwait(false), change);
        await own.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The batch for a change from a caller that holds none: a <see cref="BatchScope"/> of its own on this
    /// provider, which batches the hosts too and takes their gates first (the global order, so it never waits on a
    /// host while holding this provider's gate), and through which the change commits — a zip inside a zip then
    /// rewrites the outer one once. Called inside a host's commit, the scope joins it. A provider that is not
    /// registered (no operation can reach it) takes its own batch directly.</summary>
    private async Task<OwnBatch> BeginOwnAsync(CancellationToken ct)
    {
        BeforeOwnBatch?.Invoke();
        if (!_providers.TryGet(Id, out var registered) || !ReferenceEquals(registered, this))
            return new OwnBatch((Batch)await BeginBatchAsync(ct).ConfigureAwait(false), scope: null);
        var scope = await BatchScope.BeginAsync(_providers, [new VfsPath(Id, "/")], ct).ConfigureAwait(false);
        var batch = OpenBatch();
        if (batch is null)
        {
            await scope.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException($"The batch scope on '{Id}' holds no batch of it.");
        }
        return new OwnBatch(batch, scope);
    }

    private void Apply(Edits edits, Func<Edits, bool> change)
    {
        lock (_editLock)
            if (change(edits))
                edits.Changed = true;
    }

    // ---- The tree of changes (callers hold _editLock) ------------------------------------------------------

    private Stream CreateFile(Edits edits, VfsPath path, OwnBatch? commitOnClose)
    {
        lock (_editLock)
        {
            if (path.IsRoot) throw new VfsException($"'{GetDisplayPath(path)}' is the archive's root.");
            var parent = RequireDirectory(edits, path.Parent!.Value);
            if (parent.Children!.TryGetValue(path.Name, out var existing) && existing.Kind == EntryKind.Directory)
                throw new VfsException($"'{GetDisplayPath(path)}' is a directory.");
            var content = _temp.NewFile("entry");
            FileStream file;
            try
            {
                file = new FileStream(content, FileMode.CreateNew, FileAccess.Write, FileShare.Read, CopyBufferSize,
                    useAsync: true);
            }
            catch
            {
                ReleaseQuietly(content);
                throw;
            }
            edits.TempFiles.Add(content);
            var node = EditNode.NewFile(content, DateTimeOffset.Now);
            parent.Children[path.Name] = node;
            edits.Changed = true;
            edits.OpenStreams++;
            return new EditStream(this, file, edits, node, commitOnClose);
        }
    }

    private bool CreateDirectory(Edits edits, VfsPath path)
    {
        var node = edits.Root;
        var created = false;
        var walked = "/";
        foreach (var name in Segments(path.Path))
        {
            walked = Join(walked, name);
            if (!node.Children!.TryGetValue(name, out var child))
            {
                node.Children[name] = child = EditNode.NewDirectory(DateTimeOffset.Now);
                created = true;
            }
            else if (child.Kind != EntryKind.Directory)
            {
                throw new VfsAlreadyExistsException(
                    $"'{GetDisplayPath(new VfsPath(Id, walked))}' exists and is not a directory.");
            }
            node = child;
        }
        return created;
    }

    private bool Delete(Edits edits, VfsPath path, bool recursive)
    {
        if (path.IsRoot) throw new VfsAccessDeniedException("The archive's root cannot be deleted.");
        var parent = RequireDirectory(edits, path.Parent!.Value);
        if (!parent.Children!.TryGetValue(path.Name, out var node))
            throw new VfsNotFoundException($"'{GetDisplayPath(path)}' does not exist.");
        if (node.Kind == EntryKind.Directory && node.Children!.Count > 0 && !recursive)
            throw new VfsException($"Directory '{GetDisplayPath(path)}' is not empty.");
        parent.Children.Remove(path.Name);
        return true;
    }

    private bool Move(Edits edits, VfsPath source, VfsPath destination)
    {
        if (source.IsRoot) throw new VfsAccessDeniedException("The archive's root cannot be moved.");
        if (!string.Equals(destination.ProviderId, Id, StringComparison.Ordinal))
            throw new ArgumentException($"'{destination}' is not in this archive.", nameof(destination));
        var sourceParent = Find(edits, source.Parent!.Value);
        if (sourceParent?.Children is null || !sourceParent.Children.TryGetValue(source.Name, out var node))
            throw new VfsNotFoundException($"'{GetDisplayPath(source)}' does not exist.");
        if (Find(edits, destination) is not null)
            throw new VfsAlreadyExistsException($"'{GetDisplayPath(destination)}' already exists.");
        if (destination.IsSameOrDescendantOf(source))
            throw new VfsException($"'{GetDisplayPath(source)}' cannot be moved into itself.");
        var destinationParent = RequireDirectory(edits, destination.Parent!.Value);
        sourceParent.Children.Remove(source.Name);
        destinationParent.Children![destination.Name] = node;
        return true;
    }

    private bool SetModified(Edits edits, VfsPath path, DateTimeOffset modified)
    {
        if (path.IsRoot) return false; // the root is no entry: its time is the archive file's
        var node = Find(edits, path) ?? throw new VfsNotFoundException($"'{GetDisplayPath(path)}' does not exist.");
        node.Modified = modified;
        return true;
    }

    private EditNode RequireDirectory(Edits edits, VfsPath path)
    {
        var node = Find(edits, path) ?? throw new VfsNotFoundException($"'{GetDisplayPath(path)}' does not exist.");
        if (node.Kind != EntryKind.Directory) throw new VfsException($"'{GetDisplayPath(path)}' is not a directory.");
        return node;
    }

    private static EditNode? Find(Edits edits, VfsPath path)
    {
        var node = edits.Root;
        foreach (var name in Segments(path.Path))
            if (node.Children is null || !node.Children.TryGetValue(name, out node))
                return null;
        return node;
    }

    private static string[] Segments(string path) => path.Split('/', StringSplitOptions.RemoveEmptyEntries);

    private static string Join(string parent, string name) => parent == "/" ? "/" + name : parent + "/" + name;

    // ---- Reads that see the changes ------------------------------------------------------------------------

    /// <summary>The changes of the open batch, when it has any; reads then see the tree as it will be.</summary>
    private Edits? PendingEdits()
    {
        lock (_editLock) return _openBatch?.PendingEdits;
    }

    private FileEntry? PendingEntry(Edits edits, VfsPath path)
    {
        lock (_editLock) return Find(edits, path) is { } node ? ToEntry(path.Path, node) : null;
    }

    private List<FileEntry> PendingChildren(Edits edits, VfsPath directory)
    {
        lock (_editLock)
        {
            var node = Find(edits, directory)
                       ?? throw new VfsNotFoundException($"'{GetDisplayPath(directory)}' does not exist.");
            if (node.Children is null) throw new VfsException($"'{GetDisplayPath(directory)}' is not a directory.");
            return node.Children.Select(child => ToEntry(Join(directory.Path, child.Key), child.Value)).ToList();
        }
    }

    private async Task<Stream> OpenPendingAsync(Edits edits, VfsPath path, CancellationToken ct)
    {
        EditNode node;
        lock (_editLock)
        {
            node = Find(edits, path) ?? throw new VfsNotFoundException($"'{GetDisplayPath(path)}' does not exist.");
            if (node.Kind != EntryKind.File) throw new VfsException($"'{GetDisplayPath(path)}' is not a file.");
        }
        return await OpenContentAsync(edits, node, ct).ConfigureAwait(false);
    }

    /// <summary>A file's content as it will be: written in this batch, or the archive's entry as last read.</summary>
    private Task<Stream> OpenContentAsync(Edits edits, EditNode node, CancellationToken ct) =>
        node.Content is { } content
            ? Task.FromResult<Stream>(new FileStream(content, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                CopyBufferSize, useAsync: true))
            : OpenEntryAsync(edits.Base, node.Original!, _password, ct);

    private FileEntry ToEntry(string path, EditNode node)
    {
        var name = path == "/" ? "" : path[(path.LastIndexOf('/') + 1)..];
        var size = node.Kind != EntryKind.File ? 0
            : node.Content is { } content ? new FileInfo(content).Length : node.Size;
        return new FileEntry(new VfsPath(Id, path), node.Kind, size, node.Modified, node.Modified, Mode: null,
            IsHidden: name.StartsWith('.'), LinkTarget: null);
    }

    // ---- Rewriting ------------------------------------------------------------------------------------------

    /// <summary>Rewrites the archive with the batch's changes, if it has any (see the class remarks). The batch's
    /// changes are dropped afterwards, whether it worked or not.</summary>
    private async Task CommitEditsAsync(Batch batch, CancellationToken ct)
    {
        Edits? edits;
        lock (_editLock) edits = batch.PendingEdits;
        if (edits is null) return;
        if (!edits.Changed)
        {
            batch.Discard();
            return;
        }
        var host = HostProvider;
        var part = SafeReplace.PartPathFor(Host);
        try
        {
            lock (_editLock)
                if (edits.OpenStreams > 0)
                    throw new VfsException(
                        $"A file written into '{_providers.DisplayPath(Host)}' is still open; the archive was left " +
                        "as it is.");
            if (!IsEditable(edits.Base)) throw ReadOnly(new VfsPath(Id, "/"));
            if (await HostStampAsync(ct).ConfigureAwait(false) != edits.Base.Stamp)
                throw new VfsException(
                    $"'{_providers.DisplayPath(Host)}' was changed elsewhere after it was read; it was left as it is.");
            var written = await WriteArchiveAsync(edits, host, part, ct).ConfigureAwait(false);
            await CheckArchiveAsync(host, part, written, ct).ConfigureAwait(false);
            await SafeReplace.MoveIntoPlaceAsync(host, part, Host, replace: true).ConfigureAwait(false);
        }
        catch
        {
            await DeletePartQuietlyAsync(host, part).ConfigureAwait(false);
            batch.Discard();
            throw;
        }
        batch.Discard();
        await ReloadAfterRewriteAsync().ConfigureAwait(false);
    }

    /// <summary>Writes the archive as the changes make it to <paramref name="part"/>; returns the entries written:
    /// name → (folder, size). The zip writer cannot write Zip64 to a stream that does not seek (a file inside another
    /// archive): such an archive is written to a temporary file first and copied.</summary>
    private async Task<Dictionary<string, (bool Directory, long Size)>> WriteArchiveAsync(Edits edits,
        IFileSystemProvider host, VfsPath part, CancellationToken ct)
    {
        List<(string Key, EditNode Node, long Size)> entries;
        lock (_editLock) entries = Flatten(edits.Root);
        var zip64 = NeedsZip64(entries.Count, entries.Sum(e => e.Size));
        var output = await host.OpenWriteAsync(part, ct).ConfigureAwait(false);
        Dictionary<string, (bool Directory, long Size)> written;
        try
        {
            written = await WriteToAsync(output, edits, entries, zip64, ct).ConfigureAwait(false);
        }
        catch
        {
            await AbortAsync(output).ConfigureAwait(false);
            throw;
        }
        await output.DisposeAsync().ConfigureAwait(false); // a zip host outside a batch takes the part now
        return written;
    }

    private async Task<Dictionary<string, (bool Directory, long Size)>> WriteToAsync(Stream output, Edits edits,
        List<(string Key, EditNode Node, long Size)> entries, bool zip64, CancellationToken ct)
    {
        if (output.CanSeek || !zip64)
        {
            var direct = await WriteEntriesAsync(output, edits, entries, zip64, ct).ConfigureAwait(false);
            await output.FlushAsync(ct).ConfigureAwait(false);
            return direct;
        }
        var local = _temp.NewFile("rewrite.zip");
        try
        {
            await using var file = new FileStream(local, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                CopyBufferSize, useAsync: true);
            var written = await WriteEntriesAsync(file, edits, entries, zip64, ct).ConfigureAwait(false);
            file.Position = 0;
            await file.CopyToAsync(output, CopyBufferSize, ct).ConfigureAwait(false);
            await output.FlushAsync(ct).ConfigureAwait(false);
            return written;
        }
        finally
        {
            ReleaseQuietly(local);
        }
    }

    /// <summary>Closes the host's stream of a write that failed, so that nothing of it is applied (a zip host outside
    /// a batch would otherwise take the half-written part) and the failure stays the one reported: it never throws.
    /// Internal for tests.</summary>
    internal static async Task AbortAsync(Stream output)
    {
        if (output is EditStream edit)
        {
            await edit.AbortAsync().ConfigureAwait(false);
            return;
        }
        try
        {
            await output.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // the write has failed already; the part file is removed next
        }
    }

    private async Task<Dictionary<string, (bool Directory, long Size)>> WriteEntriesAsync(Stream output, Edits edits,
        List<(string Key, EditNode Node, long Size)> entries, bool zip64, CancellationToken ct)
    {
        var written = new Dictionary<string, (bool, long)>(StringComparer.Ordinal);
        // Stored by default, Deflate per file: a folder written under a Deflate default is marked deflated with no
        // data, which other tools (unzip) reject.
        var options = new ZipWriterOptions(CompressionType.None)
        {
            LeaveStreamOpen = true,
            ArchiveEncoding = new ArchiveEncoding { Default = Encoding.UTF8 },
            UseZip64 = zip64,
        };
        if (!string.IsNullOrEmpty(edits.Base.ArchiveComment)) options.ArchiveComment = edits.Base.ArchiveComment;
        var writer = new ZipWriter(output, options);
        // Not disposed after a failure: disposing writes the central directory, which would only hide the error.
        foreach (var (key, node, size) in entries)
        {
            ct.ThrowIfCancellationRequested();
            var time = ZipTime(node.Modified);
            if (node.Kind == EntryKind.Directory)
            {
                writer.WriteDirectory(key, time);
                written[key + "/"] = (true, 0);
                continue;
            }
            await using (var content = await OpenContentAsync(edits, node, ct).ConfigureAwait(false))
                writer.Write(key, content, new ZipWriterEntryOptions
                {
                    CompressionType = CompressionType.Deflate,
                    ModificationDateTime = time,
                    EnableZip64 = EntryNeedsZip64(size),
                    EntryComment = edits.Base.CommentOf(node.Original),
                });
            written[key] = (false, size);
        }
        writer.Dispose(); // the central directory
        return written;
    }

    /// <summary>Every node below the root with its zip name, each folder before what it holds.</summary>
    private static List<(string Key, EditNode Node, long Size)> Flatten(EditNode root)
    {
        var entries = new List<(string, EditNode, long)>();
        var pending = new Stack<(string Prefix, EditNode Directory)>([("", root)]);
        while (pending.TryPop(out var item))
        {
            var directories = new List<(string, EditNode)>();
            foreach (var (name, node) in item.Directory.Children!.OrderBy(c => c.Key, StringComparer.Ordinal))
            {
                var key = item.Prefix + name;
                var size = node.Kind != EntryKind.File ? 0
                    : node.Content is { } content ? new FileInfo(content).Length : node.Size;
                entries.Add((key, node, size));
                if (node.Kind == EntryKind.Directory) directories.Add((key + "/", node));
            }
            for (var i = directories.Count - 1; i >= 0; i--) pending.Push(directories[i]);
        }
        return entries;
    }

    /// <summary>The time for the zip writer: local, as zip stores the wall clock; not before what zip can hold.
    /// </summary>
    private static DateTime ZipTime(DateTimeOffset time)
    {
        var local = time.LocalDateTime;
        return local < EarliestZipTime ? EarliestZipTime : local;
    }

    /// <summary>Reads the written archive back and compares its listing with what was written.</summary>
    /// <exception cref="VfsException">The archive cannot be read back, or lists other entries.</exception>
    private async Task CheckArchiveAsync(IFileSystemProvider host, VfsPath part,
        Dictionary<string, (bool Directory, long Size)> written, CancellationToken ct)
    {
        var stream = await host.OpenReadAsync(part, ct).ConfigureAwait(false);
        string? copy = null;
        try
        {
            if (!stream.CanSeek)
            {
                copy = _temp.NewFile("check.zip");
                await using (var local = new FileStream(copy, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                                 CopyBufferSize, useAsync: true))
                    await stream.CopyToAsync(local, CopyBufferSize, ct).ConfigureAwait(false);
                await stream.DisposeAsync().ConfigureAwait(false);
                stream = OpenLocalCopy(copy);
            }
            var listed = new Dictionary<string, (bool, long)>(StringComparer.Ordinal);
            try
            {
                using var archive = ZipArchive.Open(stream, Options(password: null));
                foreach (var entry in archive.Entries)
                    if (!listed.TryAdd(entry.Key ?? "", (entry.IsDirectory, entry.IsDirectory ? 0 : entry.Size)))
                        throw new VfsException($"'{entry.Key}' is in the rewritten archive twice.");
            }
            catch (Exception e) when (e is not (OperationCanceledException or VfsException))
            {
                throw new VfsException(
                    $"The rewritten '{_providers.DisplayPath(Host)}' cannot be read back; it was not put in place.", e);
            }
            if (listed.Count != written.Count ||
                written.Any(w => !listed.TryGetValue(w.Key, out var found) || found != w.Value))
                throw new VfsException(
                    $"The rewritten '{_providers.DisplayPath(Host)}' does not list what was written; it was not put " +
                    "in place.");
        }
        finally
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            if (copy is not null) ReleaseQuietly(copy);
        }
    }

    private static async Task DeletePartQuietlyAsync(IFileSystemProvider host, VfsPath part)
    {
        try
        {
            if (await host.GetEntryAsync(part, CancellationToken.None).ConfigureAwait(false) is not null)
                await host.DeleteAsync(part, recursive: false, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // the error being handled is what gets reported; the stray part file has a recognisable name
        }
    }

    /// <summary>Reads the listing of the archive just put in place. Even if the host file kept its size and time
    /// (a coarse clock), the old listing must not stay: if reading fails now, the next call reads it.</summary>
    private async Task ReloadAfterRewriteAsync()
    {
        await _reload.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            var old = _snapshot;
            try
            {
                var stamp = await HostStampAsync(CancellationToken.None).ConfigureAwait(false);
                _snapshot = await LoadAsync(stamp, CancellationToken.None).ConfigureAwait(false);
                _stale = false;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _stale = true; // the change is in place; reading its listing is left to the next call
                return;
            }
            old.Retire(_temp);
        }
        finally
        {
            _reload.Release();
        }
    }

    // ---- Parts ---------------------------------------------------------------------------------------------

    /// <summary>The batch of a change from a caller that holds none: through its own scope (committing and disposing
    /// that scope), or directly (an unregistered provider).</summary>
    private sealed class OwnBatch(Batch batch, BatchScope? scope) : IAsyncDisposable
    {
        public Batch Batch { get; } = batch;

        public Task CommitAsync(CancellationToken ct) =>
            scope is null ? Batch.CommitAsync(ct) : scope.CommitAsync(ct);

        public ValueTask DisposeAsync() => scope is null ? Batch.DisposeAsync() : scope.DisposeAsync();
    }

    /// <summary>The changes of one batch: the tree as it will be, over the listing it was made from.</summary>
    private sealed class Edits(Snapshot @base, EditNode root)
    {
        public Snapshot Base { get; } = @base;
        public EditNode Root { get; } = root;
        public List<string> TempFiles { get; } = [];
        public bool Changed { get; set; }

        /// <summary>Write streams opened on these changes and not yet closed.</summary>
        public int OpenStreams { get; set; }

        public static Edits From(Snapshot snapshot) => new(snapshot, Copy(snapshot.Index, snapshot.Index.Find("/")!));

        private static EditNode Copy(ArchiveIndex index, ArchiveNode node)
        {
            if (node.Kind != EntryKind.Directory) return EditNode.FromArchive(node);
            var directory = EditNode.NewDirectory(node.Modified);
            foreach (var child in index.ChildrenOf(node.Path) ?? [])
                directory.Children![child.Name] = Copy(index, child);
            return directory;
        }
    }

    /// <summary>A file or folder of the archive as it will be. A file's content is <see cref="Content"/> (written
    /// in this batch) or else <see cref="Original"/>'s.</summary>
    private sealed class EditNode
    {
        private EditNode(EntryKind kind, DateTimeOffset modified)
        {
            Kind = kind;
            Modified = modified;
        }

        public EntryKind Kind { get; }
        public DateTimeOffset Modified { get; set; }
        public long Size { get; private init; }
        public ArchiveNode? Original { get; private init; }
        public string? Content { get; private init; }
        public Dictionary<string, EditNode>? Children { get; private init; }

        public static EditNode FromArchive(ArchiveNode node) =>
            new(node.Kind, node.Modified) { Size = node.Size, Original = node };

        public static EditNode NewFile(string content, DateTimeOffset modified) =>
            new(EntryKind.File, modified) { Content = content };

        public static EditNode NewDirectory(DateTimeOffset modified) =>
            new(EntryKind.Directory, modified) { Children = new Dictionary<string, EditNode>(StringComparer.Ordinal) };
    }

    /// <summary>Writes a file of the changes. Closing it sets the file's time and, for a write outside a batch,
    /// rewrites the archive (an error of that is thrown from closing).</summary>
    private sealed class EditStream(ArchiveProvider owner, FileStream file, Edits edits, EditNode node,
        OwnBatch? commitOnClose) : Stream
    {
        private bool _closed;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => !_closed;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => file.Position;
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count) => file.Write(buffer, offset, count);

        public override void Write(ReadOnlySpan<byte> buffer) => file.Write(buffer);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) =>
            file.WriteAsync(buffer, ct);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            file.WriteAsync(buffer, offset, count, ct);

        public override void Flush() => file.Flush();

        public override Task FlushAsync(CancellationToken ct) => file.FlushAsync(ct);

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override async ValueTask DisposeAsync()
        {
            await CloseAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) CloseAsync().GetAwaiter().GetResult();
            base.Dispose(disposing);
        }

        /// <summary>Closes the file without applying it: for a write that failed. Outside a batch nothing is
        /// committed; in one the file stays as written until removed. Errors of closing are swallowed, so the
        /// failure being handled stays the one reported.</summary>
        public async Task AbortAsync()
        {
            if (_closed) return;
            _closed = true;
            try
            {
                await file.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // the write has failed already
            }
            lock (owner._editLock) edits.OpenStreams--;
            if (commitOnClose is null) return;
            try
            {
                await commitOnClose.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // the write has failed already; disposing the scope still released every batch it held
            }
        }

        private async Task CloseAsync()
        {
            if (_closed) return;
            _closed = true;
            try
            {
                try
                {
                    await file.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    lock (owner._editLock)
                    {
                        edits.OpenStreams--;
                        node.Modified = DateTimeOffset.Now;
                    }
                }
                if (commitOnClose is not null)
                    await commitOnClose.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                if (commitOnClose is not null) await commitOnClose.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
