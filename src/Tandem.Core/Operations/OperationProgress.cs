using Tandem.Core.Vfs;

namespace Tandem.Core.Operations;

public enum OperationState { Queued, Scanning, Running, Paused, Completed, Cancelled, Failed }

/// <summary>A snapshot of an operation's progress. "Files" counts every non-directory item (files and links);
/// <paramref name="Remaining"/> is null while the speed is unknown.</summary>
public sealed record OperationProgress(string? CurrentItem, long FilesDone, long FilesTotal,
    long BytesDone, long BytesTotal, double BytesPerSecond, TimeSpan? Remaining);

/// <summary>An item the operation left alone, and why.</summary>
public sealed record SkippedItem(VfsPath Path, string Reason);

public sealed record OperationResult(OperationState State, IReadOnlyList<SkippedItem> Skipped);
