using Tandem.Core.Vfs;

namespace Tandem.Core.Operations;

/// <summary>One thing to copy: a source entry and the path it becomes.</summary>
internal sealed record CopyItem(FileEntry Source, VfsPath Destination);

/// <summary>
/// The planning half of copy and move: validating the request (targets, the destination directory behind its links,
/// items that would land on themselves) and scanning the sources into a flat plan (directories before their
/// contents), reporting the totals to the owning <see cref="FileOperation"/>. A directory that cannot be listed is
/// put to <paramref name="errors"/>. With <paramref name="verifyContent"/>, every file's bytes count twice in the
/// totals (written once, read back once). Pausing and cancellation are checked between scanned entries.
/// </summary>
internal sealed class CopyPlanner(ProviderRegistry providers, FileOperation operation, ItemErrors errors,
    bool verifyContent)
{
    private const int MaxLinkHops = 32;

    /// <summary>The error questions, with the "skip all" answer shared by everything this planner's operation
    /// does.</summary>
    public ItemErrors Errors => errors;

    /// <summary>Whether written files are read back and compared with their sources.</summary>
    public bool VerifyContent => verifyContent;

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

    /// <summary>The files of a plan, in plan order: what the copy will read, for
    /// <see cref="BatchScope.PrepareReadAsync"/>.</summary>
    public static IReadOnlyList<VfsPath> FilesToRead(IReadOnlyList<CopyItem> items) =>
        items.Where(item => item.Source.Kind == EntryKind.File).Select(item => item.Source.Path).ToList();

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

    /// <summary>The bytes a file of <paramref name="size"/> adds to the total: written once, and read back once more
    /// when verifying.</summary>
    public long PlannedBytes(long size) => verifyContent ? size * 2 : size;

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
                if (!await errors.ShouldRetryAsync(directory, e, ct).ConfigureAwait(false)) return null;
            }
        }
    }
}
