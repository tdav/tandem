using Tandem.Core.Vfs;

namespace Tandem.Core.Operations;

public enum ConflictAction { Overwrite, OverwriteOlder, Skip, Rename, Cancel }

public readonly record struct ConflictDecision(ConflictAction Action, bool ApplyToAll);

public enum ErrorAction { Retry, Skip, SkipAll, Abort }

/// <summary>The questions an operation asks the user while it runs. Called from the operation's background
/// thread; the implementation marshals to the UI as needed.</summary>
public interface IOperationInteraction
{
    /// <summary>The destination of <paramref name="source"/> already exists as <paramref name="existing"/>.</summary>
    Task<ConflictDecision> ResolveConflictAsync(FileEntry source, FileEntry existing, CancellationToken ct);

    /// <summary>Processing <paramref name="item"/> failed with <paramref name="error"/>.</summary>
    Task<ErrorAction> ResolveErrorAsync(VfsPath item, Exception error, CancellationToken ct);
}
