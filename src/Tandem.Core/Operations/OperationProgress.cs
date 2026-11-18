using Tandem.Core.Vfs;

namespace Tandem.Core.Operations;

public enum OperationState { Queued, Scanning, Running, Paused, Completed, Cancelled, Failed }

/// <summary>A snapshot of an operation's progress. "Files" counts every non-directory item (files and links);
/// <paramref name="Remaining"/> is null while the speed is unknown.</summary>
public sealed record OperationProgress(string? CurrentItem, long FilesDone, long FilesTotal,
    long BytesDone, long BytesTotal, double BytesPerSecond, TimeSpan? Remaining);

/// <summary>Why an item was left alone.</summary>
public enum SkipReason
{
    /// <summary>The item failed and the user chose to skip it (<see cref="SkippedItem.Detail"/> has the error).</summary>
    Error,

    /// <summary>The item is a symbolic link and the destination cannot hold links.</summary>
    SymlinksNotSupported,
}

/// <summary>An item the operation left alone, and why. <paramref name="Detail"/> is the underlying error message,
/// when there is one; the interface words the reason itself.</summary>
public sealed record SkippedItem(VfsPath Path, SkipReason Reason, string? Detail = null);

public sealed record OperationResult(OperationState State, IReadOnlyList<SkippedItem> Skipped);
