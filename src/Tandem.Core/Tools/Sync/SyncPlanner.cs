using Tandem.Core.Listing;
using Tandem.Core.Operations;
using Tandem.Core.Tools.Compare;
using Tandem.Core.Vfs;

namespace Tandem.Core.Tools.Sync;

public static class SyncPlanner
{
    public static async Task<SyncComparison> CompareAsync(ProviderRegistry providers, VfsPath leftRoot,
        VfsPath rightRoot, SyncOptions options, IProgress<SyncProgress>? progress, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Tolerance, TimeSpan.Zero);
        ct.ThrowIfCancellationRequested();
        var leftProvider = providers.For(leftRoot);
        var rightProvider = providers.For(rightRoot);
        if (leftRoot.IsSameOrDescendantOf(rightRoot) || rightRoot.IsSameOrDescendantOf(leftRoot) ||
            (ReferenceEquals(leftProvider, rightProvider) &&
             (await ContainsAsync(leftProvider, leftRoot, rightRoot, ct).ConfigureAwait(false) ||
              await ContainsAsync(leftProvider, rightRoot, leftRoot, ct).ConfigureAwait(false))))
            throw new ArgumentException("Synchronization roots overlap.");

        var items = new List<SyncItem>();
        var unreadable = new List<string>();
        var mask = new FileMask(options.Mask);
        int folders = 0, files = 0;
        long bytes = 0;
        await WalkAsync(leftRoot, rightRoot, string.Empty, true, true).ConfigureAwait(false);
        return new(leftRoot, rightRoot, options, items.AsReadOnly(), unreadable.AsReadOnly());

        async Task<bool> WalkAsync(VfsPath left, VfsPath right, string relative, bool hasLeft, bool hasRight)
        {
            ct.ThrowIfCancellationRequested();
            var leftEntries = hasLeft ? await ListAsync(leftProvider, left).ConfigureAwait(false) : [];
            var rightEntries = hasRight ? await ListAsync(rightProvider, right).ConfigureAwait(false) : [];
            if (leftEntries is null || rightEntries is null)
            {
                unreadable.Add(relative.Length == 0 ? "/" : relative);
                return false;
            }
            folders++;
            Report();

            foreach (var (l, r) in DirectoryComparer.Pair(leftEntries.Where(Visible), rightEntries.Where(Visible)))
            {
                ct.ThrowIfCancellationRequested();
                var name = (l ?? r)!.Name;
                var relativePath = relative.Length == 0 ? name : relative + "/" + name;
                var leftPath = l?.Path ?? left.Combine(name);
                var rightPath = r?.Path ?? right.Combine(name);
                var lDirectory = l?.Kind == EntryKind.Directory;
                var rDirectory = r?.Kind == EntryKind.Directory;
                if (lDirectory || rDirectory)
                {
                    if (l is not null && r is not null && lDirectory != rDirectory)
                        Add(relativePath, leftPath, rightPath, l, r, SyncState.KindMismatch);
                    else if (options.Subfolders &&
                             await WalkAsync(leftPath, rightPath, relativePath, lDirectory, rDirectory)
                                 .ConfigureAwait(false) && lDirectory != rDirectory)
                        Add(relativePath, leftPath, rightPath, l, r, lDirectory ? SyncState.LeftOnly : SyncState.RightOnly);
                    continue;
                }

                if (!mask.IsMatch(name)) continue;
                var state = await StateAsync(l, r).ConfigureAwait(false);
                files++;
                Report();
                Add(relativePath, leftPath, rightPath, l, r, state);
            }
            return leftEntries.Count == 0 && rightEntries.Count == 0;
        }

        bool Visible(FileEntry entry) => (options.IncludeHidden || !entry.IsHidden) && !SafeReplace.IsServiceName(entry.Name);

        async Task<List<FileEntry>?> ListAsync(IFileSystemProvider provider, VfsPath directory)
        {
            try
            {
                var entries = new List<FileEntry>();
                await foreach (var entry in provider.EnumerateAsync(directory, ct).ConfigureAwait(false))
                    entries.Add(entry);
                return entries;
            }
            catch (Exception error) when (ItemErrors.IsItemError(error))
            {
                return null;
            }
        }

        async Task<SyncState> StateAsync(FileEntry? left, FileEntry? right)
        {
            if (left is null) return SyncState.RightOnly;
            if (right is null) return SyncState.LeftOnly;
            if (left.Kind != right.Kind) return SyncState.KindMismatch;
            var time = left.Modified - right.Modified;
            var withinTolerance = time.Duration() <= options.Tolerance;
            bool equal;
            if (left.Kind == EntryKind.Symlink)
            {
                equal = string.Equals(left.LinkTarget, right.LinkTarget, StringComparison.Ordinal);
            }
            else if (left.Size != right.Size)
            {
                equal = false;
            }
            else if (options.Mode == SyncCompareMode.Content)
            {
                await using var first = await leftProvider.OpenReadAsync(left.Path, ct).ConfigureAwait(false);
                await using var second = await rightProvider.OpenReadAsync(right.Path, ct).ConfigureAwait(false);
                var previous = bytes;
                IProgress<long>? readerProgress = progress is null ? null : new ByteProgress(value =>
                {
                    bytes = previous + value;
                    Report();
                });
                equal = await BinaryDiff.AreEqualAsync(first, second, readerProgress, null, ct).ConfigureAwait(false);
            }
            else
            {
                equal = withinTolerance;
            }

            if (equal) return SyncState.Equal;
            if (withinTolerance) return SyncState.Different;
            return time > TimeSpan.Zero ? SyncState.LeftNewer : SyncState.RightNewer;
        }

        void Add(string relative, VfsPath leftPath, VfsPath rightPath, FileEntry? left, FileEntry? right, SyncState state)
        {
            var actions = new List<SyncAction> { SyncAction.None };
            if (state != SyncState.KindMismatch)
            {
                if (left is not null && CanCopy(left, rightProvider)) actions.Add(SyncAction.CopyToRight);
                if (right is not null && CanCopy(right, leftProvider)) actions.Add(SyncAction.CopyToLeft);
                if (right is null && Writable(leftProvider)) actions.Add(SyncAction.DeleteLeft);
                if (left is null && Writable(rightProvider)) actions.Add(SyncAction.DeleteRight);
            }
            var preferred = state switch
            {
                SyncState.LeftNewer or SyncState.LeftOnly => SyncAction.CopyToRight,
                SyncState.RightNewer or SyncState.RightOnly => SyncAction.CopyToLeft,
                _ => SyncAction.None,
            };
            items.Add(new(relative, left, right, state, actions.Contains(preferred) ? preferred : SyncAction.None,
                actions.AsReadOnly()) { LeftPath = leftPath, RightPath = rightPath });
        }

        void Report() => progress?.Report(new(folders, files, bytes));
    }

    private static bool Writable(IFileSystemProvider provider) => provider.Capabilities.HasFlag(ProviderCapabilities.Write);

    private static bool CanCopy(FileEntry entry, IFileSystemProvider provider) =>
        Writable(provider) && (entry.Kind != EntryKind.Symlink ||
            provider.Capabilities.HasFlag(ProviderCapabilities.Symlinks) && entry.LinkTarget is not null);

    private static async Task<bool> ContainsAsync(IFileSystemProvider provider, VfsPath root, VfsPath child,
        CancellationToken ct)
    {
        for (VfsPath? current = child; current is { } path; current = path.Parent)
            if (await provider.IsSameEntryAsync(root, path, ct).ConfigureAwait(false))
                return true;
        return false;
    }

    private sealed class ByteProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }
}
