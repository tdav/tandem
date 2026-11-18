using Tandem.Core.Operations;
using Tandem.Core.Vfs;

namespace Tandem.Core.Tests.Operations;

/// <summary>Answers questions from scripted queues and records them; a question without a scripted answer
/// throws, so a test fails loudly when the operation asks something unexpected.</summary>
public sealed class ScriptedInteraction : IOperationInteraction
{
    private readonly Lock _lock = new();
    private readonly Queue<ConflictDecision> _conflictAnswers = new();
    private readonly Queue<ErrorAction> _errorAnswers = new();
    private readonly List<(FileEntry Source, FileEntry Existing)> _conflicts = [];
    private readonly List<(VfsPath Item, Exception Error)> _errors = [];

    public ScriptedInteraction OnConflict(ConflictAction action, bool applyToAll = false)
    {
        lock (_lock) _conflictAnswers.Enqueue(new ConflictDecision(action, applyToAll));
        return this;
    }

    public ScriptedInteraction OnError(ErrorAction action)
    {
        lock (_lock) _errorAnswers.Enqueue(action);
        return this;
    }

    public IReadOnlyList<(FileEntry Source, FileEntry Existing)> Conflicts
    {
        get { lock (_lock) return _conflicts.ToList(); }
    }

    public IReadOnlyList<(VfsPath Item, Exception Error)> Errors
    {
        get { lock (_lock) return _errors.ToList(); }
    }

    public Task<ConflictDecision> ResolveConflictAsync(FileEntry source, FileEntry existing, CancellationToken ct)
    {
        lock (_lock)
        {
            _conflicts.Add((source, existing));
            if (!_conflictAnswers.TryDequeue(out var answer))
                throw new InvalidOperationException($"Unexpected conflict question for '{source.Path.Path}'.");
            return Task.FromResult(answer);
        }
    }

    public Task<ErrorAction> ResolveErrorAsync(VfsPath item, Exception error, CancellationToken ct)
    {
        lock (_lock)
        {
            _errors.Add((item, error));
            if (!_errorAnswers.TryDequeue(out var answer))
                throw new InvalidOperationException($"Unexpected error question for '{item.Path}': {error.Message}");
            return Task.FromResult(answer);
        }
    }
}
