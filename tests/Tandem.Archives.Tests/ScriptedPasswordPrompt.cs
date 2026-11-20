using Tandem.Core.Vfs;

namespace Tandem.Archives.Tests;

/// <summary>Answers password questions from a scripted queue (null = the user declines) and records them; a question
/// without a scripted answer throws, so a test fails loudly when it is asked once too often.</summary>
internal sealed class ScriptedPasswordPrompt : IArchivePasswordPrompt
{
    private readonly Lock _lock = new();
    private readonly Queue<string?> _answers = new();
    private readonly List<(VfsPath Archive, bool PreviousWasWrong)> _questions = [];

    public ScriptedPasswordPrompt Answer(params string?[] answers)
    {
        lock (_lock)
            foreach (var answer in answers)
                _answers.Enqueue(answer);
        return this;
    }

    public IReadOnlyList<(VfsPath Archive, bool PreviousWasWrong)> Questions
    {
        get
        {
            lock (_lock) return _questions.ToList();
        }
    }

    public IReadOnlyList<bool> PreviousWasWrong => Questions.Select(q => q.PreviousWasWrong).ToList();

    public Task<string?> AskAsync(VfsPath archive, bool previousWasWrong, CancellationToken ct)
    {
        lock (_lock)
        {
            _questions.Add((archive, previousWasWrong));
            if (!_answers.TryDequeue(out var answer))
                throw new InvalidOperationException($"Unexpected password question for '{archive.Path}'.");
            return Task.FromResult(answer);
        }
    }
}
