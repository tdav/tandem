using System.Buffers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Tandem.Core.Platform;
using Tandem.Core.Vfs;

namespace Tandem.Core.Operations;

internal enum CopyOutcome { Copied, DirectoryCreated, DirectoryMerged, Skipped }

/// <summary>
/// The copying half of copy and move: copying the plan made by <see cref="CopyPlanner"/> one item at a time, and
/// single renames on one volume. Progress goes to the owning <see cref="FileOperation"/>. A file is written through
/// <see cref="SafeReplace"/> under <c>.{name}.tandem-part</c> next to its destination, given the source's
/// modification time (and mode, when both sides support modes), then renamed into place; when it replaces an
/// existing file, the old file is set aside as <c>.{name}.tandem-old</c> only after the part file is complete, and
/// deleted once the new file is in place (or put back if that fails).
/// <para>An existing destination file is a conflict, put to <see cref="IOperationInteraction.ResolveConflictAsync"/>;
/// an existing directory is merged into. An I/O error on an item (including a file meeting a directory or the other
/// way round) is put to the planner's <see cref="CopyPlanner.Errors"/>. "Apply to all" and "skip all" answers last
/// until the engine is discarded. Skipping a directory skips everything inside it, recorded as one item.</para>
/// <para>Cancellation and pausing are checked between items and between the blocks of a file being written or
/// verified, also by the platform's own copy (<see cref="INativeCopy"/>), which is handed the operation's pause
/// checkpoint; a cancelled or failed file leaves no part file behind. With the planner's
/// <see cref="CopyPlanner.VerifyContent"/>, each written file and its source are read back and their SHA-256 compared
/// before the file is moved into place; the verification bytes count in the totals, doubling them. With
/// <paramref name="checkSize"/> (used by move), a written file whose size differs from the source's, both read
/// afresh once it is written, is an item error too, raised before the file is moved into place.</para>
/// </summary>
/// <para><paramref name="itemWritten"/>, when given, hears each file and each folder the engine wrote — a folder it
/// created, not one it merged into — with the path it is in place at, right after that item (links are not told).</para>
internal sealed class CopyEngine(CopyPlanner planner, ProviderRegistry providers, FileOperation operation,
    IOperationInteraction interaction, INativeCopy? nativeCopy, bool checkSize = false,
    Action<VfsPath>? itemWritten = null)
{
    private const int BufferSize = 1024 * 1024;

    // The engine runs one item at a time on the operation's thread; these are not shared.
    private ConflictAction? _conflictActionForAll;

    private ItemErrors Errors => planner.Errors;


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
                if (outcomes[i] is CopyOutcome.Copied or CopyOutcome.DirectoryCreated &&
                    item.Source.Kind != EntryKind.Symlink)
                    itemWritten?.Invoke(writtenTo);
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
        if (existing is not null && await planner.IsSameEntryAsync(source.Path, destination, io).ConfigureAwait(false))
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
            if (source.LinkTarget is null)
                throw new VfsAccessDeniedException($"The target of the link '{source.Path.Path}' cannot be read.");
            if (replace)
                await SafeReplace.WriteAsync(target, destination, replace: true,
                    part => target.CreateSymlinkAsync(part, source.LinkTarget, io)).ConfigureAwait(false);
            else
                await target.CreateSymlinkAsync(destination, source.LinkTarget, io).ConfigureAwait(false);
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
        if (existing is not null && await planner.IsSameEntryAsync(sourcePath, destination, io).ConfigureAwait(false))
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

        await SafeReplace.MoveIntoPlaceAsync(provider, sourcePath, destination, replace).ConfigureAwait(false);
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
        if (source.Kind == EntryKind.File) operation.DropBytesFromTotal(planner.PlannedBytes(source.Size));
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

    /// <summary>Gives a directory created by <see cref="CopyItemAsync"/> the source's time and mode and, local to
    /// local with the platform's copy, the rest of its metadata (extended attributes such as Finder tags, ACL,
    /// flags). A failure is an item error, asked about like any other; true when the metadata was applied, false when
    /// the user skipped the directory (recorded).</summary>
    public Task<bool> ApplyDirectoryMetadataAsync(CopyItem item, CancellationToken ct)
    {
        var target = providers.For(item.Destination);
        return Errors.RunAsync(item.Source.Path, async () =>
        {
            await ApplyMetadataAsync(item.Source, item.Destination, target, CancellationToken.None)
                .ConfigureAwait(false);
            // Last: the flags it copies (a locked folder) would stop the time and mode from being set.
            if (UsesNativeCopy(item.Source.Path, item.Destination))
                await nativeCopy!.CopyDirectoryMetadataAsync(item.Source.Path.Path, item.Destination.Path,
                    CancellationToken.None).ConfigureAwait(false);
        }, ct);
    }

    /// <summary>Writes <paramref name="source"/> to <paramref name="destination"/> through a part file. With
    /// <paramref name="replace"/>, the existing destination is replaced only once the part file is complete (see
    /// <see cref="SafeReplace.WriteAsync"/>). Writing and verifying stop at the next block when <paramref name="ct"/>
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

        var native = UsesNativeCopy(source.Path, destination);
        try
        {
            await SafeReplace.WriteAsync(target, destination, replace, async part =>
            {
                if (native)
                {
                    // The platform copies the whole file in one call; cancellation reaches it through ct and a
                    // pause through the checkpoint it passes between blocks.
                    var progress = new RunningTotal(Count);
                    await nativeCopy!.CopyFileAsync(source.Path.Path, part.Path, progress,
                        operation.WaitWhilePausedAsync, ct).ConfigureAwait(false);
                }
                else
                {
                    await CopyStreamAsync(source.Path, part, target, Count, ct).ConfigureAwait(false);
                }
                TopUp(source.Size); // keep BytesDone in step with the scanned total

                if (planner.VerifyContent)
                {
                    await VerifyAsync(source.Path, part, target, Count, ct).ConfigureAwait(false);
                    TopUp(planner.PlannedBytes(source.Size));
                }
                if (checkSize) await CheckSizeAsync(source.Path, part, target).ConfigureAwait(false);

                await ApplyMetadataAsync(source, part, target, CancellationToken.None).ConfigureAwait(false);
            }).ConfigureAwait(false);
            // The platform copy left the Finder lock (and the like) off the part file so that it could be finished;
            // the file in place gets it now.
            if (native)
                await nativeCopy!.CopyFlagsAsync(source.Path.Path, destination.Path, CancellationToken.None)
                    .ConfigureAwait(false);
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

    private bool UsesNativeCopy(VfsPath source, VfsPath destination) =>
        nativeCopy is not null && IsLocal(source) && IsLocal(destination);

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
