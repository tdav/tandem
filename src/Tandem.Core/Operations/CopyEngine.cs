using System.Buffers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
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
/// <para>Cancellation and pausing are checked between items, between scanned entries, and between the blocks of a
/// file being written or verified; a cancelled or failed file leaves no part file behind. With
/// <paramref name="verifyContent"/>, each written file and its source are read back and their SHA-256 compared
/// before the file is moved into place; the verification bytes count in the totals, doubling them. With
/// <paramref name="checkSize"/> (used by move), a written file whose size differs from the source's, both read
/// afresh once it is written, is an item error too, raised before the file is moved into place.</para>
/// </summary>
internal sealed class CopyEngine(ProviderRegistry providers, FileOperation operation,
    IOperationInteraction interaction, INativeCopy? nativeCopy, bool verifyContent, bool checkSize = false)
{
    public const string PartSuffix = ".tandem-part";
    public const string BackupSuffix = ".tandem-old";
    private const int BufferSize = 1024 * 1024;
    private const int MaxLinkHops = 32;

    // The engine runs one item at a time on the operation's thread; these are not shared.
    private ConflictAction? _conflictActionForAll;

    /// <summary>The error questions, with the "skip all" answer shared by everything this engine's operation does.</summary>
    public ItemErrors Errors { get; } = new(operation, interaction);

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

            if (destination == source) throw OntoItself(source, destination);
            targets.Add((source, destination));
        }
        return targets;
    }

    /// <summary>The refusal for an item that would land on itself.</summary>
    public static OperationRejectedException OntoItself(VfsPath source, VfsPath destination) =>
        new(RejectionReason.SourceEqualsDestination, $"'{source.Path}' cannot be copied or moved onto itself.",
            source, destination);

    /// <summary>True when <paramref name="a"/> and <paramref name="b"/> both exist and are one file-system object
    /// (on a case-insensitive volume "/a/x" and "/A/X" can be). Never true across providers.</summary>
    public Task<bool> IsSameEntryAsync(VfsPath a, VfsPath b, CancellationToken ct) =>
        string.Equals(a.ProviderId, b.ProviderId, StringComparison.Ordinal)
            ? providers.For(a).IsSameEntryAsync(a, b, ct)
            : Task.FromResult(false);

    /// <summary>Refuses targets whose destination already is the source itself under another path (a copy
    /// there would overwrite or merge the source into itself).</summary>
    /// <exception cref="OperationRejectedException">A destination is its own source.</exception>
    public async Task RejectTargetsOntoThemselvesAsync(IReadOnlyList<(VfsPath Source, VfsPath Destination)> targets,
        CancellationToken ct)
    {
        foreach (var (source, destination) in targets)
            if (await IsSameEntryAsync(source, destination, ct).ConfigureAwait(false))
                throw OntoItself(source, destination);
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
    /// <param name="extraFiles">Items the operation handles outside the plan, added to the file total.</param>
    /// <exception cref="VfsNotFoundException">A source does not exist.</exception>
    public async Task<IReadOnlyList<CopyItem>> ScanAsync(IReadOnlyList<(VfsPath Source, VfsPath Destination)> targets,
        CancellationToken ct, long extraFiles = 0)
    {
        var items = new List<CopyItem>();
        long files = extraFiles, bytes = 0;
        foreach (var (source, destination) in targets)
        {
            var entry = await providers.For(source).GetEntryAsync(source, ct).ConfigureAwait(false)
                        ?? throw new VfsNotFoundException($"'{source.Path}' does not exist.");
            await AddAsync(entry, destination).ConfigureAwait(false);
        }
        operation.SetTotals(files, PlannedBytes(bytes));
        return items;

        async Task AddAsync(FileEntry entry, VfsPath destination)
        {
            await operation.WaitWhilePausedAsync(ct).ConfigureAwait(false);
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
            catch (Exception e) when (ItemErrors.IsItemError(e))
            {
                if (!await Errors.ShouldRetryAsync(directory, e, ct).ConfigureAwait(false)) return null;
            }
        }
    }

    /// <summary>Copies the plan in order and returns each item's outcome (same order as <paramref name="items"/>).
    /// The items inside a skipped directory are skipped too, without a record of their own.
    /// <paramref name="itemDone"/>, when given, is awaited after each item with its outcome and the path it was
    /// written to (its final name, after a "rename" answer), before the next item starts.</summary>
    /// <exception cref="OperationCanceledException">The operation was cancelled, by <paramref name="ct"/> or by a
    /// "cancel" answer to a conflict.</exception>
    public async Task<IReadOnlyList<CopyOutcome>> CopyAllAsync(IReadOnlyList<CopyItem> items, CancellationToken ct,
        Func<CopyItem, CopyOutcome, VfsPath, Task>? itemDone = null)
    {
        var outcomes = new CopyOutcome[items.Count];
        VfsPath? skippedDirectory = null;
        for (var i = 0; i < items.Count; i++)
        {
            await operation.WaitWhilePausedAsync(ct).ConfigureAwait(false);
            var item = items[i];
            var writtenTo = item.Destination;
            if (skippedDirectory is { } skipped && item.Source.Path.IsSameOrDescendantOf(skipped))
            {
                CountSkipped(item.Source);
                outcomes[i] = CopyOutcome.Skipped;
            }
            else
            {
                (outcomes[i], writtenTo) = await CopyItemAsync(item, ct).ConfigureAwait(false);
                if (outcomes[i] == CopyOutcome.Skipped && item.Source.Kind == EntryKind.Directory)
                    skippedDirectory = item.Source.Path;
            }
            if (itemDone is not null) await itemDone(item, outcomes[i], writtenTo).ConfigureAwait(false);
        }
        return outcomes;
    }

    /// <summary>Copies one plan item, asking about a conflict or an error as needed. A directory's own times and
    /// mode are applied later, by <see cref="ApplyDirectoryMetadataAsync"/>, once its contents are written.</summary>
    private async Task<(CopyOutcome, VfsPath)> CopyItemAsync(CopyItem item, CancellationToken ct)
    {
        operation.BeginItem(item.Source.Path.Path);
        var conflictAction = new StrongBox<ConflictAction?>(); // decided once per item, kept across retries
        while (true)
        {
            try
            {
                return await CopyItemOnceAsync(item, conflictAction, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (ItemErrors.IsItemError(e))
            {
                if (!await Errors.ShouldRetryAsync(item.Source.Path, e, ct).ConfigureAwait(false))
                {
                    CountSkipped(item.Source);
                    return (CopyOutcome.Skipped, item.Destination);
                }
                ct.ThrowIfCancellationRequested();
            }
        }
    }

    private async Task<(CopyOutcome, VfsPath)> CopyItemOnceAsync(CopyItem item, StrongBox<ConflictAction?> conflictAction,
        CancellationToken ct)
    {
        // Only writing (and verifying) a file's content is cancellable, block by block; the short steps around
        // it are not, so a cancel never interrupts putting a finished file into place.
        var io = CancellationToken.None;
        var source = item.Source;
        var destination = item.Destination;
        var target = providers.For(destination);

        if (source.Kind == EntryKind.Symlink && !target.Capabilities.HasFlag(ProviderCapabilities.Symlinks))
            return (Skip(source, SkipReason.SymlinksNotSupported), destination);

        var existing = await target.GetEntryAsync(destination, io).ConfigureAwait(false);
        // The destination may be the source itself under another path (letter case, a linked folder): it must not
        // be asked about, set aside, overwritten or merged into.
        if (existing is not null && await IsSameEntryAsync(source.Path, destination, io).ConfigureAwait(false))
            throw new VfsAlreadyExistsException($"'{destination.Path}' is '{source.Path.Path}' itself.");
        if (source.Kind == EntryKind.Directory)
        {
            if (existing is null)
            {
                await target.CreateDirectoryAsync(destination, io).ConfigureAwait(false);
                return (CopyOutcome.DirectoryCreated, destination);
            }
            if (existing.Kind == EntryKind.Directory) return (CopyOutcome.DirectoryMerged, destination);
            throw new VfsAlreadyExistsException($"'{destination.Path}' already exists and is not a directory.");
        }

        var replace = false;
        if (existing is not null)
        {
            if (existing.Kind == EntryKind.Directory)
                throw new VfsAlreadyExistsException($"'{destination.Path}' already exists and is a directory.");

            conflictAction.Value ??= await DecideConflictAsync(source, existing, ct).ConfigureAwait(false);
            var resolution = await ApplyConflictActionAsync(conflictAction.Value.Value, source, existing, destination,
                target, ct).ConfigureAwait(false);
            if (resolution.Skip is { } reason) return (Skip(source, reason), destination);
            (destination, replace) = (resolution.Destination, resolution.Replace);
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
            await CopyFileAsync(source, destination, target, replace, ct).ConfigureAwait(false);
        }
        operation.CompleteFile();
        return (CopyOutcome.Copied, destination);
    }

    /// <summary>Moves <paramref name="source"/> to <paramref name="destination"/> with a single rename (the two
    /// are on one volume), asking about a conflict or an error exactly as for a copied item; a directory meeting
    /// anything, or anything meeting a directory, is an error (merging directories is done by copying). Counts as one
    /// file done. Returns true when the item was moved; false when it was skipped (recorded) and stays where it
    /// is.</summary>
    /// <exception cref="OperationCanceledException">The operation was cancelled, by <paramref name="ct"/> or by a
    /// "cancel" answer to a conflict.</exception>
    public async Task<bool> RenameAsync(VfsPath source, VfsPath destination, CancellationToken ct)
    {
        await operation.WaitWhilePausedAsync(ct).ConfigureAwait(false);
        operation.BeginItem(source.Path);
        var conflictAction = new StrongBox<ConflictAction?>(); // decided once per item, kept across retries
        while (true)
        {
            try
            {
                var moved = await RenameOnceAsync(source, destination, conflictAction, ct).ConfigureAwait(false);
                operation.CompleteFile();
                return moved;
            }
            catch (Exception e) when (ItemErrors.IsItemError(e))
            {
                if (!await Errors.ShouldRetryAsync(source, e, ct).ConfigureAwait(false))
                {
                    operation.CompleteFile();
                    return false;
                }
                ct.ThrowIfCancellationRequested();
            }
        }
    }

    private async Task<bool> RenameOnceAsync(VfsPath sourcePath, VfsPath destination,
        StrongBox<ConflictAction?> conflictAction, CancellationToken ct)
    {
        var io = CancellationToken.None; // a single rename is not interrupted
        var provider = providers.For(sourcePath);
        var source = await provider.GetEntryAsync(sourcePath, io).ConfigureAwait(false)
                     ?? throw new VfsNotFoundException($"'{sourcePath.Path}' does not exist.");

        // A destination that is the source itself under another spelling (a rename that changes only letter case on
        // a case-insensitive volume) is no conflict: the provider's rename changes the name in place.
        var existing = await provider.GetEntryAsync(destination, io).ConfigureAwait(false);
        if (existing is not null && await IsSameEntryAsync(sourcePath, destination, io).ConfigureAwait(false))
            existing = null;
        var replace = false;
        if (existing is not null)
        {
            if (source.Kind == EntryKind.Directory || existing.Kind == EntryKind.Directory)
                throw new VfsAlreadyExistsException($"'{destination.Path}' already exists.");

            conflictAction.Value ??= await DecideConflictAsync(source, existing, ct).ConfigureAwait(false);
            var resolution = await ApplyConflictActionAsync(conflictAction.Value.Value, source, existing, destination,
                provider, ct).ConfigureAwait(false);
            if (resolution.Skip is { } reason)
            {
                operation.AddSkipped(new SkippedItem(sourcePath, reason));
                return false;
            }
            (destination, replace) = (resolution.Destination, resolution.Replace);
        }

        await MoveIntoPlaceAsync(sourcePath, destination, provider, replace).ConfigureAwait(false);
        return true;
    }

    /// <summary>What a conflict answer means for one item: skip it (with the reason), replace the existing
    /// destination, or write under another (free) name.</summary>
    private readonly record struct ConflictResolution(VfsPath Destination, bool Replace, SkipReason? Skip);

    private async Task<ConflictResolution> ApplyConflictActionAsync(ConflictAction action, FileEntry source,
        FileEntry existing, VfsPath destination, IFileSystemProvider target, CancellationToken ct)
    {
        switch (action)
        {
            case ConflictAction.Skip:
                return new ConflictResolution(destination, false, SkipReason.ConflictSkipped);
            case ConflictAction.OverwriteOlder when source.Modified <= existing.Modified:
                return new ConflictResolution(destination, false, SkipReason.OlderThanDestination);
            case ConflictAction.Overwrite or ConflictAction.OverwriteOlder:
                return new ConflictResolution(destination, true, null);
            case ConflictAction.Rename:
                var free = await FirstFreeNameAsync(destination, target, CancellationToken.None).ConfigureAwait(false);
                return new ConflictResolution(free, false, null);
            default: // ConflictAction.Cancel
                operation.Cancel();
                throw new OperationCanceledException(ct);
        }
    }

    private async Task<ConflictAction> DecideConflictAsync(FileEntry source, FileEntry existing, CancellationToken ct)
    {
        if (_conflictActionForAll is { } forAll) return forAll;
        var decision = await interaction.ResolveConflictAsync(source, existing, ct).ConfigureAwait(false);
        if (decision.ApplyToAll) _conflictActionForAll = decision.Action;
        return decision.Action;
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
        if (source.Kind == EntryKind.File) operation.DropBytesFromTotal(PlannedBytes(source.Size));
        operation.CompleteFile();
    }

    /// <summary>The bytes a file of <paramref name="size"/> adds to the total: written once, and read back once more
    /// when verifying.</summary>
    private long PlannedBytes(long size) => verifyContent ? size * 2 : size;

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
    /// <see cref="ReplaceWithPartAsync"/>). Writing and verifying stop at the next block when <paramref name="ct"/>
    /// is cancelled. On failure or cancellation the part file is removed and the attempt's bytes are taken
    /// back.</summary>
    /// <exception cref="ContentMismatchException">Verification found the copy differs from the source.</exception>
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
                    // The platform copies the whole file in one call: cancellation reaches it through ct, but a
                    // pause takes effect only once this file is done (at the next checkpoint).
                    var progress = new RunningTotal(Count);
                    await nativeCopy.CopyFileAsync(source.Path.Path, part.Path, progress, ct).ConfigureAwait(false);
                }
                else
                {
                    await CopyStreamAsync(source.Path, part, target, Count, ct).ConfigureAwait(false);
                }
                TopUp(source.Size); // keep BytesDone in step with the scanned total

                if (verifyContent)
                {
                    await VerifyAsync(source.Path, part, target, Count, ct).ConfigureAwait(false);
                    TopUp(PlannedBytes(source.Size));
                }
                if (checkSize) await CheckSizeAsync(source.Path, part, target).ConfigureAwait(false);

                await ApplyMetadataAsync(source, part, target, CancellationToken.None).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        catch
        {
            operation.AddBytesDone(-copied);
            throw;
        }

        void TopUp(long expected)
        {
            if (copied < expected) Count(expected - copied);
        }
    }

    /// <summary>Compares the sizes the providers report now for the written <paramref name="part"/> and its source
    /// (not the size seen while scanning: the source may have changed since).</summary>
    /// <exception cref="ContentMismatchException">The sizes differ.</exception>
    private async Task CheckSizeAsync(VfsPath source, VfsPath part, IFileSystemProvider target)
    {
        var io = CancellationToken.None;
        var expected = await providers.For(source).GetEntryAsync(source, io).ConfigureAwait(false)
                       ?? throw new VfsNotFoundException($"'{source.Path}' does not exist.");
        var actual = await target.GetEntryAsync(part, io).ConfigureAwait(false)
                     ?? throw new VfsNotFoundException($"'{part.Path}' does not exist.");
        if (actual.Size != expected.Size)
            throw new ContentMismatchException(
                $"The copy of '{source.Path}' has {actual.Size} bytes; the original has {expected.Size}.");
    }

    /// <summary>Reads <paramref name="source"/> and <paramref name="part"/> side by side, block by block (pausing
    /// and cancelling between blocks), counting the source's bytes, and compares their SHA-256.</summary>
    /// <exception cref="ContentMismatchException">The two differ.</exception>
    private async Task VerifyAsync(VfsPath source, VfsPath part, IFileSystemProvider target, Action<long> count,
        CancellationToken ct)
    {
        var expectedBuffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        var actualBuffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            await using var expected = await providers.For(source).OpenReadAsync(source, ct).ConfigureAwait(false);
            await using var actual = await target.OpenReadAsync(part, ct).ConfigureAwait(false);
            using var expectedHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var actualHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            while (true)
            {
                await operation.WaitWhilePausedAsync(ct).ConfigureAwait(false);
                var expectedRead = await expected.ReadAtLeastAsync(expectedBuffer.AsMemory(0, BufferSize), BufferSize,
                    throwOnEndOfStream: false, ct).ConfigureAwait(false);
                var actualRead = await actual.ReadAtLeastAsync(actualBuffer.AsMemory(0, BufferSize), BufferSize,
                    throwOnEndOfStream: false, ct).ConfigureAwait(false);
                if (expectedRead == 0 && actualRead == 0) break;
                expectedHash.AppendData(expectedBuffer, 0, expectedRead);
                actualHash.AppendData(actualBuffer, 0, actualRead);
                count(expectedRead);
            }
            if (!expectedHash.GetHashAndReset().AsSpan().SequenceEqual(actualHash.GetHashAndReset()))
                throw new ContentMismatchException($"The copy of '{source.Path}' differs from the original.");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(expectedBuffer);
            ArrayPool<byte>.Shared.Return(actualBuffer);
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
        var part = SiblingPath(destination, PartSuffix);
        try
        {
            await write(part).ConfigureAwait(false);
            await MoveIntoPlaceAsync(part, destination, target, replace).ConfigureAwait(false);
        }
        catch
        {
            await DeleteQuietlyAsync(part, target).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Renames <paramref name="from"/> to <paramref name="destination"/>. With <paramref name="replace"/>,
    /// the existing destination is first set aside as <c>.{name}.tandem-old</c> and deleted only once
    /// <paramref name="from"/> is in place; if that rename fails, the backup is put back. <paramref name="from"/>
    /// itself is never removed here. Not cancellable.</summary>
    private static async Task MoveIntoPlaceAsync(VfsPath from, VfsPath destination, IFileSystemProvider target,
        bool replace)
    {
        var ct = CancellationToken.None;
        if (!replace)
        {
            await target.MoveAsync(from, destination, ct).ConfigureAwait(false);
            return;
        }

        var backup = SiblingPath(destination, BackupSuffix);
        // A backup left by an earlier crash is stale: the destination it backed up is still in place.
        await DeleteQuietlyAsync(backup, target).ConfigureAwait(false);
        await target.MoveAsync(destination, backup, ct).ConfigureAwait(false);
        try
        {
            await target.MoveAsync(from, destination, ct).ConfigureAwait(false);
        }
        catch
        {
            await RestoreQuietlyAsync(backup, destination, target).ConfigureAwait(false);
            throw;
        }
        // The new item is in place; a backup that cannot be removed is harmless and cleared by the next overwrite.
        await DeleteQuietlyAsync(backup, target).ConfigureAwait(false);
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

    private async Task CopyStreamAsync(VfsPath source, VfsPath part, IFileSystemProvider target, Action<long> count,
        CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            await using var input = await providers.For(source).OpenReadAsync(source, ct).ConfigureAwait(false);
            await using var output = await target.OpenWriteAsync(part, ct).ConfigureAwait(false);
            while (true)
            {
                await operation.WaitWhilePausedAsync(ct).ConfigureAwait(false);
                var read = await input.ReadAsync(buffer.AsMemory(0, BufferSize), ct).ConfigureAwait(false);
                if (read == 0) break;
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
