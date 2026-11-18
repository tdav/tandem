using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Tandem.Core.Platform;
using Tandem.Core.Vfs;

namespace Tandem.Core.Operations;

/// <summary>One thing to copy: a source entry and the path it becomes.</summary>
internal sealed record CopyItem(FileEntry Source, VfsPath Destination);

internal enum CopyOutcome { Copied, DirectoryCreated, DirectoryMerged, Skipped }

/// <summary>
/// The copy logic shared by copy and move: validating the request, scanning the sources into a flat plan
/// (directories before their contents), and copying one plan item at a time. Progress goes to the owning
/// <see cref="FileOperation"/>. A file is written under <c>.{name}.tandem-part</c> next to its destination,
/// given the source's modification time (and mode, when both sides support modes), then renamed into place;
/// when it replaces an existing file, the old file is set aside as <c>.{name}.tandem-old</c> only after the part
/// file is complete, and deleted once the new file is in place (or put back if that fails).
/// <para>An existing destination file is a conflict, put to <see cref="IOperationInteraction.ResolveConflictAsync"/>;
/// an existing directory is merged into. An I/O error on an item (including a file meeting a directory or the other
/// way round, and a source directory that cannot be listed) is put to
/// <see cref="IOperationInteraction.ResolveErrorAsync"/>. "Apply to all" and "skip all" answers last until the
/// engine is discarded. Skipping a directory skips everything inside it, recorded as one item.</para>
/// </summary>
internal sealed class CopyEngine(ProviderRegistry providers, FileOperation operation,
    IOperationInteraction interaction, INativeCopy? nativeCopy)
{
    public const string PartSuffix = ".tandem-part";
    public const string BackupSuffix = ".tandem-old";
    private const int BufferSize = 1024 * 1024;
    private const int MaxLinkHops = 32;

    // The engine runs one item at a time on the operation's thread; these are not shared.
    private ConflictAction? _conflictActionForAll;
    private bool _skipAllErrors;

    /// <summary>Pairs every source with its destination path, refusing requests that cannot be valid.</summary>
    /// <exception cref="OperationRejectedException">The request is refused.</exception>
    public static IReadOnlyList<(VfsPath Source, VfsPath Destination)> ResolveTargets(
        IReadOnlyList<VfsPath> sources, VfsPath destinationDirectory, string? newName)
    {
        if (newName is not null && sources.Count != 1)
            throw new OperationRejectedException(RejectionReason.NewNameWithMultipleSources,
                "A new name can be given only when there is a single source.",
                destination: destinationDirectory, name: newName);

        var targets = new List<(VfsPath, VfsPath)>(sources.Count);
        foreach (var source in sources)
        {
            if (destinationDirectory.IsSameOrDescendantOf(source))
                throw new OperationRejectedException(RejectionReason.DestinationInsideSource,
                    $"'{source.Path}' cannot be copied into itself.", source, destinationDirectory);

            VfsPath destination;
            try
            {
                destination = destinationDirectory.Combine(newName ?? source.Name);
            }
            catch (ArgumentException)
            {
                throw new OperationRejectedException(RejectionReason.InvalidName,
                    $"'{newName ?? source.Name}' is not a valid name.", source, destinationDirectory,
                    newName ?? source.Name);
            }

            if (destination == source)
                throw new OperationRejectedException(RejectionReason.SourceEqualsDestination,
                    $"'{source.Path}' cannot be copied onto itself.", source, destination);
            targets.Add((source, destination));
        }
        return targets;
    }

    /// <summary>The real directory behind <paramref name="destinationDirectory"/>: the path itself, or, when it is a
    /// symlink (possibly via further links) to a directory, the directory the link leads to. Copying through the
    /// resolved path works on providers that do not resolve links in the middle of a path, and lets the "into
    /// itself" check see where the files really go.</summary>
    /// <exception cref="VfsNotFoundException">There is no directory there (missing, a file, or a broken link).</exception>
    public async Task<VfsPath> ResolveDestinationDirectoryAsync(VfsPath destinationDirectory, CancellationToken ct)
    {
        var provider = providers.For(destinationDirectory);
        var path = destinationDirectory;
        for (var hop = 0; hop < MaxLinkHops; hop++)
        {
            var entry = await provider.GetEntryAsync(path, ct).ConfigureAwait(false);
            if (entry is { Kind: EntryKind.Directory }) return path;
            if (entry is not { Kind: EntryKind.Symlink, IsDirectoryLike: true, LinkTarget: { } target }) break;
            try
            {
                path = target.StartsWith('/')
                    ? path with { Path = target }
                    : path with { Path = path.Parent!.Value.Path + "/" + target };
            }
            catch (ArgumentException)
            {
                break; // the link climbs above the root
            }
        }
        throw new VfsNotFoundException($"Destination directory '{destinationDirectory.Path}' does not exist.");
    }

    /// <summary>Walks the sources (links are not followed) and reports the totals to the operation. A directory
    /// that cannot be listed goes to the error question; skipping it leaves its whole subtree out of the plan.</summary>
    /// <exception cref="VfsNotFoundException">A source does not exist.</exception>
    public async Task<IReadOnlyList<CopyItem>> ScanAsync(IReadOnlyList<(VfsPath Source, VfsPath Destination)> targets,
        CancellationToken ct)
    {
        var items = new List<CopyItem>();
        long files = 0, bytes = 0;
        foreach (var (source, destination) in targets)
        {
            var entry = await providers.For(source).GetEntryAsync(source, ct).ConfigureAwait(false)
                        ?? throw new VfsNotFoundException($"'{source.Path}' does not exist.");
            await AddAsync(entry, destination).ConfigureAwait(false);
        }
        operation.SetTotals(files, bytes);
        return items;

        async Task AddAsync(FileEntry entry, VfsPath destination)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.Kind == EntryKind.Directory)
            {
                var children = await ListChildrenAsync(entry.Path, ct).ConfigureAwait(false);
                if (children is null) return; // skipped, with everything inside it
                items.Add(new CopyItem(entry, destination));
                foreach (var child in children)
                    await AddAsync(child, destination.Combine(child.Name)).ConfigureAwait(false);
                return;
            }
            items.Add(new CopyItem(entry, destination));
            files++;
            if (entry.Kind == EntryKind.File) bytes += entry.Size;
        }
    }

    /// <summary>The directory's entries, or null when listing failed and the user skipped it.</summary>
    private async Task<List<FileEntry>?> ListChildrenAsync(VfsPath directory, CancellationToken ct)
    {
        while (true)
        {
            try
            {
                var children = new List<FileEntry>();
                await foreach (var child in providers.For(directory).EnumerateAsync(directory, ct).ConfigureAwait(false))
                    children.Add(child);
                return children;
            }
            catch (Exception e) when (IsItemError(e))
            {
                if (!await ShouldRetryAsync(directory, e, ct).ConfigureAwait(false)) return null;
            }
        }
    }

    /// <summary>Copies the plan in order and returns each item's outcome (same order as <paramref name="items"/>).
    /// The items inside a skipped directory are skipped too, without a record of their own.</summary>
    /// <exception cref="OperationCanceledException">The operation was cancelled, by <paramref name="ct"/> or by a
    /// "cancel" answer to a conflict.</exception>
    public async Task<IReadOnlyList<CopyOutcome>> CopyAllAsync(IReadOnlyList<CopyItem> items, CancellationToken ct)
    {
        var outcomes = new CopyOutcome[items.Count];
        VfsPath? skippedDirectory = null;
        for (var i = 0; i < items.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var item = items[i];
            if (skippedDirectory is { } skipped && item.Source.Path.IsSameOrDescendantOf(skipped))
            {
                CountSkipped(item.Source);
                outcomes[i] = CopyOutcome.Skipped;
                continue;
            }
            outcomes[i] = await CopyItemAsync(item, ct).ConfigureAwait(false);
            if (outcomes[i] == CopyOutcome.Skipped && item.Source.Kind == EntryKind.Directory)
                skippedDirectory = item.Source.Path;
        }
        return outcomes;
    }

    /// <summary>Copies one plan item, asking about a conflict or an error as needed. A directory's own times and
    /// mode are applied later, by <see cref="ApplyDirectoryMetadataAsync"/>, once its contents are written.</summary>
    private async Task<CopyOutcome> CopyItemAsync(CopyItem item, CancellationToken ct)
    {
        operation.BeginItem(item.Source.Path.Path);
        var conflictAction = new StrongBox<ConflictAction?>(); // decided once per item, kept across retries
        while (true)
        {
            try
            {
                return await CopyItemOnceAsync(item, conflictAction, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (IsItemError(e))
            {
                if (!await ShouldRetryAsync(item.Source.Path, e, ct).ConfigureAwait(false))
                {
                    CountSkipped(item.Source);
                    return CopyOutcome.Skipped;
                }
                ct.ThrowIfCancellationRequested();
            }
        }
    }

    private async Task<CopyOutcome> CopyItemOnceAsync(CopyItem item, StrongBox<ConflictAction?> conflictAction,
        CancellationToken ct)
    {
        // The operation's token only interrupts questions; I/O inside an item is not cancellable yet, so the
        // operation stops between items.
        var io = CancellationToken.None;
        var source = item.Source;
        var destination = item.Destination;
        var target = providers.For(destination);

        if (source.Kind == EntryKind.Symlink && !target.Capabilities.HasFlag(ProviderCapabilities.Symlinks))
            return Skip(source, SkipReason.SymlinksNotSupported);

        var existing = await target.GetEntryAsync(destination, io).ConfigureAwait(false);
        if (source.Kind == EntryKind.Directory)
        {
            if (existing is null)
            {
                await target.CreateDirectoryAsync(destination, io).ConfigureAwait(false);
                return CopyOutcome.DirectoryCreated;
            }
            if (existing.Kind == EntryKind.Directory) return CopyOutcome.DirectoryMerged;
            throw new VfsAlreadyExistsException($"'{destination.Path}' already exists and is not a directory.");
        }

        var replace = false;
        if (existing is not null)
        {
            if (existing.Kind == EntryKind.Directory)
                throw new VfsAlreadyExistsException($"'{destination.Path}' already exists and is a directory.");

            conflictAction.Value ??= await DecideConflictAsync(source, existing, ct).ConfigureAwait(false);
            switch (conflictAction.Value)
            {
                case ConflictAction.Skip:
                    return Skip(source, SkipReason.ConflictSkipped);
                case ConflictAction.OverwriteOlder when source.Modified <= existing.Modified:
                    return Skip(source, SkipReason.OlderThanDestination);
                case ConflictAction.Overwrite or ConflictAction.OverwriteOlder:
                    replace = true;
                    break;
                case ConflictAction.Rename:
                    destination = await FirstFreeNameAsync(destination, target, io).ConfigureAwait(false);
                    break;
                case ConflictAction.Cancel:
                    operation.Cancel();
                    throw new OperationCanceledException(ct);
            }
        }

        if (source.Kind == EntryKind.Symlink)
        {
            if (replace)
                await ReplaceWithPartAsync(destination, target, replace: true,
                    part => target.CreateSymlinkAsync(part, source.LinkTarget!, io)).ConfigureAwait(false);
            else
                await target.CreateSymlinkAsync(destination, source.LinkTarget!, io).ConfigureAwait(false);
        }
        else
        {
            await CopyFileAsync(source, destination, target, replace, io).ConfigureAwait(false);
        }
        operation.CompleteFile();
        return CopyOutcome.Copied;
    }

    private async Task<ConflictAction> DecideConflictAsync(FileEntry source, FileEntry existing, CancellationToken ct)
    {
        if (_conflictActionForAll is { } forAll) return forAll;
        var decision = await interaction.ResolveConflictAsync(source, existing, ct).ConfigureAwait(false);
        if (decision.ApplyToAll) _conflictActionForAll = decision.Action;
        return decision.Action;
    }

    /// <summary>Puts <paramref name="error"/> on <paramref name="item"/> to the user (unless "skip all" was
    /// chosen). Returns true to retry; false when the item is skipped, which is recorded.</summary>
    /// <exception cref="Exception"><paramref name="error"/> itself, rethrown, when the user aborts.</exception>
    private async Task<bool> ShouldRetryAsync(VfsPath item, Exception error, CancellationToken ct)
    {
        var action = _skipAllErrors
            ? ErrorAction.Skip
            : await interaction.ResolveErrorAsync(item, error, ct).ConfigureAwait(false);
        switch (action)
        {
            case ErrorAction.Retry:
                return true;
            case ErrorAction.Abort:
                ExceptionDispatchInfo.Throw(error);
                return false; // not reached
            case ErrorAction.SkipAll:
                _skipAllErrors = true;
                break;
        }
        operation.AddSkipped(new SkippedItem(item, SkipReason.Error, error.Message));
        return false;
    }

    private CopyOutcome Skip(FileEntry source, SkipReason reason)
    {
        operation.AddSkipped(new SkippedItem(source.Path, reason));
        CountSkipped(source);
        return CopyOutcome.Skipped;
    }

    /// <summary>A skipped file or link counts as done; a skipped file's bytes leave the total.</summary>
    private void CountSkipped(FileEntry source)
    {
        if (source.Kind == EntryKind.Directory) return;
        if (source.Kind == EntryKind.File) operation.DropBytesFromTotal(source.Size);
        operation.CompleteFile();
    }

    /// <summary>"name (2).ext", "name (3).ext", ... — the first that does not exist.</summary>
    private static async Task<VfsPath> FirstFreeNameAsync(VfsPath destination, IFileSystemProvider target,
        CancellationToken ct)
    {
        var directory = destination.Parent!.Value;
        var name = destination.Name;
        var dot = name.LastIndexOf('.');
        var (stem, extension) = dot > 0 ? (name[..dot], name[dot..]) : (name, "");
        for (var n = 2; ; n++)
        {
            var candidate = directory.Combine($"{stem} ({n}){extension}");
            if (await target.GetEntryAsync(candidate, ct).ConfigureAwait(false) is null) return candidate;
        }
    }

    /// <summary>Gives a directory created by <see cref="CopyItemAsync"/> the source's time and mode.</summary>
    public async Task ApplyDirectoryMetadataAsync(CopyItem item)
    {
        var target = providers.For(item.Destination);
        await ApplyMetadataAsync(item.Source, item.Destination, target, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Writes <paramref name="source"/> to <paramref name="destination"/> through a part file. With
    /// <paramref name="replace"/>, the existing destination is replaced only once the part file is complete (see
    /// <see cref="ReplaceWithPartAsync"/>). On failure the part file is removed and the attempt's bytes are taken
    /// back.</summary>
    private async Task CopyFileAsync(FileEntry source, VfsPath destination, IFileSystemProvider target, bool replace,
        CancellationToken ct)
    {
        var copied = 0L;
        void Count(long bytes)
        {
            copied += bytes;
            operation.AddBytesDone(bytes);
        }

        try
        {
            await ReplaceWithPartAsync(destination, target, replace, async part =>
            {
                if (nativeCopy is not null && IsLocal(source.Path) && IsLocal(destination))
                {
                    var progress = new RunningTotal(Count);
                    await nativeCopy.CopyFileAsync(source.Path.Path, part.Path, progress, ct).ConfigureAwait(false);
                }
                else
                {
                    await CopyStreamAsync(source.Path, part, target, Count, ct).ConfigureAwait(false);
                }
                if (copied < source.Size) Count(source.Size - copied); // keep BytesDone in step with the scanned total

                await ApplyMetadataAsync(source, part, target, ct).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        catch
        {
            operation.AddBytesDone(-copied);
            throw;
        }
    }

    /// <summary>Has <paramref name="write"/> produce the item under its part name next to
    /// <paramref name="destination"/>, then renames it into place. With <paramref name="replace"/>, the existing
    /// destination is first set aside as <c>.{name}.tandem-old</c> and deleted only once the new item is in place;
    /// if moving the part into place fails, the backup is put back, so the destination is never left empty.
    /// If anything fails, the part file is removed and the original error propagates.</summary>
    private static async Task ReplaceWithPartAsync(VfsPath destination, IFileSystemProvider target, bool replace,
        Func<VfsPath, Task> write)
    {
        var ct = CancellationToken.None;
        var part = SiblingPath(destination, PartSuffix);
        try
        {
            await write(part).ConfigureAwait(false);
            if (!replace)
            {
                await target.MoveAsync(part, destination, ct).ConfigureAwait(false);
                return;
            }

            var backup = SiblingPath(destination, BackupSuffix);
            // A backup left by an earlier crash is stale: the destination it backed up is still in place.
            await DeleteQuietlyAsync(backup, target).ConfigureAwait(false);
            await target.MoveAsync(destination, backup, ct).ConfigureAwait(false);
            try
            {
                await target.MoveAsync(part, destination, ct).ConfigureAwait(false);
            }
            catch
            {
                await RestoreQuietlyAsync(backup, destination, target).ConfigureAwait(false);
                throw;
            }
            // The new item is in place; a backup that cannot be removed is harmless and cleared by the next overwrite.
            await DeleteQuietlyAsync(backup, target).ConfigureAwait(false);
        }
        catch
        {
            await DeleteQuietlyAsync(part, target).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary><c>.{name}{suffix}</c> next to <paramref name="destination"/>.</summary>
    private static VfsPath SiblingPath(VfsPath destination, string suffix) =>
        destination.Parent!.Value.Combine("." + destination.Name + suffix);

    /// <summary>Best-effort return of the set-aside original; a failure here must not hide the error being handled
    /// (the original then survives under its backup name).</summary>
    private static async Task RestoreQuietlyAsync(VfsPath backup, VfsPath destination, IFileSystemProvider target)
    {
        try
        {
            await target.MoveAsync(backup, destination, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Nothing better to do: the user is asked about the original error.
        }
    }

    /// <summary>Best-effort removal of a part or backup file; a failure here must not hide the error being
    /// handled.</summary>
    private static async Task DeleteQuietlyAsync(VfsPath part, IFileSystemProvider target)
    {
        try
        {
            if (await target.GetEntryAsync(part, CancellationToken.None).ConfigureAwait(false) is not null)
                await target.DeleteAsync(part, recursive: false, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The original error is what the user is asked about; the stray part file has a recognisable name.
        }
    }

    private static bool IsItemError(Exception e) => e is IOException or UnauthorizedAccessException;

    private async Task CopyStreamAsync(VfsPath source, VfsPath part, IFileSystemProvider target, Action<long> count,
        CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            await using var input = await providers.For(source).OpenReadAsync(source, ct).ConfigureAwait(false);
            await using var output = await target.OpenWriteAsync(part, ct).ConfigureAwait(false);
            int read;
            while ((read = await input.ReadAsync(buffer.AsMemory(0, BufferSize), ct).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                count(read);
            }
            await output.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task ApplyMetadataAsync(FileEntry source, VfsPath destination, IFileSystemProvider target,
        CancellationToken ct)
    {
        await target.SetModifiedAsync(destination, source.Modified, ct).ConfigureAwait(false);
        if (source.Mode is { } mode &&
            providers.For(source.Path).Capabilities.HasFlag(ProviderCapabilities.UnixPermissions) &&
            target.Capabilities.HasFlag(ProviderCapabilities.UnixPermissions))
            await target.SetModeAsync(destination, mode, ct).ConfigureAwait(false);
    }

    private static bool IsLocal(VfsPath path) =>
        string.Equals(path.ProviderId, VfsPath.LocalProviderId, StringComparison.Ordinal);

    /// <summary>Turns a running total (as <see cref="INativeCopy"/> reports it) into increments, synchronously.</summary>
    private sealed class RunningTotal(Action<long> increment) : IProgress<long>
    {
        private readonly Lock _lock = new();
        private long _last;

        public void Report(long value)
        {
            lock (_lock)
            {
                if (value <= _last) return;
                increment(value - _last);
                _last = value;
            }
        }
    }
}
