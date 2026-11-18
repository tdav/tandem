using System.Buffers;
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
/// given the source's modification time (and mode, when both sides support modes), then renamed into place.
/// </summary>
internal sealed class CopyEngine(ProviderRegistry providers, FileOperation operation, INativeCopy? nativeCopy)
{
    public const string PartSuffix = ".tandem-part";
    private const int BufferSize = 1024 * 1024;
    private const int MaxLinkHops = 32;

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

    /// <summary>Walks the sources (links are not followed) and reports the totals to the operation.</summary>
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
            items.Add(new CopyItem(entry, destination));
            switch (entry.Kind)
            {
                case EntryKind.Directory:
                    await foreach (var child in providers.For(entry.Path).EnumerateAsync(entry.Path, ct)
                                       .ConfigureAwait(false))
                        await AddAsync(child, destination.Combine(child.Name)).ConfigureAwait(false);
                    break;
                case EntryKind.File:
                    files++;
                    bytes += entry.Size;
                    break;
                case EntryKind.Symlink:
                    files++;
                    break;
            }
        }
    }

    /// <summary>Copies one plan item. A directory's own times and mode are applied later, by
    /// <see cref="ApplyDirectoryMetadataAsync"/>, once its contents are written.</summary>
    /// <exception cref="VfsAlreadyExistsException">The destination exists (and is not a directory to merge into).</exception>
    public async Task<CopyOutcome> CopyItemAsync(CopyItem item)
    {
        // Cancellation inside an item is not supported yet: the operation stops between items.
        var ct = CancellationToken.None;
        var source = item.Source;
        var target = providers.For(item.Destination);
        operation.BeginItem(source.Path.Path);

        var existing = await target.GetEntryAsync(item.Destination, ct).ConfigureAwait(false);
        if (source.Kind == EntryKind.Directory)
        {
            if (existing is null)
            {
                await target.CreateDirectoryAsync(item.Destination, ct).ConfigureAwait(false);
                return CopyOutcome.DirectoryCreated;
            }
            if (existing.Kind == EntryKind.Directory) return CopyOutcome.DirectoryMerged;
        }
        if (existing is not null)
            throw new VfsAlreadyExistsException($"'{item.Destination.Path}' already exists.");

        if (source.Kind == EntryKind.Symlink)
        {
            if (!target.Capabilities.HasFlag(ProviderCapabilities.Symlinks))
            {
                operation.AddSkipped(new SkippedItem(source.Path, SkipReason.SymlinksNotSupported));
                operation.CompleteFile();
                return CopyOutcome.Skipped;
            }
            await target.CreateSymlinkAsync(item.Destination, source.LinkTarget!, ct).ConfigureAwait(false);
            operation.CompleteFile();
            return CopyOutcome.Copied;
        }

        await CopyFileAsync(source, item.Destination, target, ct).ConfigureAwait(false);
        operation.CompleteFile();
        return CopyOutcome.Copied;
    }

    /// <summary>Gives a directory created by <see cref="CopyItemAsync"/> the source's time and mode.</summary>
    public async Task ApplyDirectoryMetadataAsync(CopyItem item)
    {
        var target = providers.For(item.Destination);
        await ApplyMetadataAsync(item.Source, item.Destination, target, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task CopyFileAsync(FileEntry source, VfsPath destination, IFileSystemProvider target,
        CancellationToken ct)
    {
        var part = destination.Parent!.Value.Combine("." + destination.Name + PartSuffix);
        var copied = 0L;
        void Count(long bytes)
        {
            copied += bytes;
            operation.AddBytesDone(bytes);
        }

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
        await target.MoveAsync(part, destination, ct).ConfigureAwait(false);
    }

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
