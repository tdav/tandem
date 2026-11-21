using Tandem.Core.Vfs;

namespace Tandem.Core.Tools.Sync;

public enum SyncCompareMode { SizeAndDate, Content }
public sealed record SyncOptions(SyncCompareMode Mode = SyncCompareMode.SizeAndDate, bool Subfolders = true,
    string Mask = "*", bool IncludeHidden = true)
{
    public TimeSpan Tolerance { get; init; } = TimeSpan.FromSeconds(2);
}

public enum SyncState { Equal, LeftNewer, RightNewer, Different, LeftOnly, RightOnly, KindMismatch }
public enum SyncAction { None, CopyToRight, CopyToLeft, DeleteLeft, DeleteRight }
public sealed record SyncItem(string RelativePath, FileEntry? Left, FileEntry? Right, SyncState State,
    SyncAction DefaultAction, IReadOnlyList<SyncAction> AllowedActions)
{
    // Keep each side's spelling even when an ancestor's Unicode form differs.
    internal VfsPath? LeftPath { get; init; }
    internal VfsPath? RightPath { get; init; }
}

public sealed record SyncComparison(VfsPath LeftRoot, VfsPath RightRoot, SyncOptions Options,
    IReadOnlyList<SyncItem> Items, IReadOnlyList<string> UnreadableFolders);
public readonly record struct SyncProgress(int FoldersScanned, int FilesCompared, long BytesCompared);
