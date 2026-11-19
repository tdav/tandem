using Tandem.App.Services;
using Tandem.Core.Operations;
using Tandem.Core.Vfs;

namespace Tandem.App.Tests.Views;

/// <summary>
/// Answers dialogs from scripted queues and records every request. An answer the dialog's validator refuses is
/// recorded in <see cref="ValidationErrors"/> and the next scripted answer is tried, as a user correcting the text in
/// a dialog that stays open would. A dialog without a scripted answer is cancelled (and recorded in
/// <see cref="Unscripted"/>), so a test never hangs on a question it did not expect.
/// </summary>
internal class ScriptedDialogs : IDialogService
{
    private readonly Lock _lock = new();

    public Queue<CopyMoveAnswer?> CopyMoveAnswers { get; } = new();
    public Queue<string?> NameAnswers { get; } = new();
    public Queue<bool> DeleteAnswers { get; } = new();
    public Queue<string?> MaskAnswers { get; } = new();
    public Queue<ConflictDecision> ConflictAnswers { get; } = new();
    public Queue<ErrorAction> ErrorAnswers { get; } = new();

    public List<CopyMoveRequest> CopyMoveRequests { get; } = [];
    public List<string> NameRequests { get; } = [];
    public List<(int Count, string? SingleName, bool Permanent)> DeleteRequests { get; } = [];
    public List<(bool Select, string Initial)> MaskRequests { get; } = [];
    public List<(FileEntry Source, FileEntry Existing)> ConflictRequests { get; } = [];
    public List<(VfsPath Item, string Message, string? Detail)> ErrorRequests { get; } = [];
    public List<IReadOnlyList<SkippedItem>> Summaries { get; } = [];
    public List<string> Messages { get; } = [];
    public List<(string Text, string? Detail)> MessagesWithDetail { get; } = [];
    public List<string> ValidationErrors { get; } = [];
    public List<string> Unscripted { get; } = [];

    /// <summary>Every dialog asked for, in order, by kind ("copyMove", "name", "delete", "mask", "conflict", "error",
    /// "summary", "message").</summary>
    public List<string> Log { get; } = [];

    /// <summary>As a scripted destination: accept the text the dialog proposed.</summary>
    public const string Proposed = "\0proposed";

    /// <summary>When set, <see cref="ShowSummaryAsync"/> throws it.</summary>
    public Exception? ThrowOnSummary { get; set; }

    /// <summary>Scripts the copy/move dialog's next answer.</summary>
    public ScriptedDialogs AnswerCopyMove(string destination = Proposed, bool verify = false,
        bool runImmediately = true)
    {
        lock (_lock) CopyMoveAnswers.Enqueue(new CopyMoveAnswer(destination, verify, runImmediately));
        return this;
    }

    public virtual async Task<CopyMoveAnswer?> AskCopyMoveAsync(CopyMoveRequest request)
    {
        Record("copyMove", () => CopyMoveRequests.Add(request));
        while (true)
        {
            if (!TryTake(CopyMoveAnswers, "copyMove", out var answer) || answer is null) return null;
            if (answer.Destination == Proposed) answer = answer with { Destination = request.Destination };
            if (request.Validate is { } validate && await validate(answer.Destination) is { } error)
            {
                lock (_lock) ValidationErrors.Add(error);
                continue;
            }
            return answer;
        }
    }

    public virtual async Task<string?> AskNewFolderNameAsync(string initial, TextValidator? validate = null)
    {
        Record("name", () => NameRequests.Add(initial));
        while (true)
        {
            if (!TryTake(NameAnswers, "name", out var answer) || answer is null) return null;
            if (validate is not null && await validate(answer) is { } error)
            {
                lock (_lock) ValidationErrors.Add(error);
                continue;
            }
            return answer;
        }
    }

    public virtual Task<bool> ConfirmDeleteAsync(int itemCount, string? singleName, bool permanent)
    {
        Record("delete", () => DeleteRequests.Add((itemCount, singleName, permanent)));
        return Task.FromResult(TryTake(DeleteAnswers, "delete", out var answer) && answer);
    }

    public virtual Task<string?> AskMaskAsync(bool select, string initial)
    {
        Record("mask", () => MaskRequests.Add((select, initial)));
        return Task.FromResult(TryTake(MaskAnswers, "mask", out var answer) ? answer : null);
    }

    public virtual Task<ConflictDecision> AskConflictAsync(FileEntry source, FileEntry existing, CancellationToken ct)
    {
        Record("conflict", () => ConflictRequests.Add((source, existing)));
        return Task.FromResult(TryTake(ConflictAnswers, "conflict", out var answer)
            ? answer
            : new ConflictDecision(ConflictAction.Cancel, false));
    }

    public virtual Task<ErrorAction> AskErrorAsync(VfsPath item, string message, string? detail, CancellationToken ct)
    {
        Record("error", () => ErrorRequests.Add((item, message, detail)));
        return Task.FromResult(TryTake(ErrorAnswers, "error", out var answer) ? answer : ErrorAction.Abort);
    }

    public virtual Task ShowSummaryAsync(IReadOnlyList<SkippedItem> skipped)
    {
        Record("summary", () => Summaries.Add(skipped));
        return ThrowOnSummary is { } error ? Task.FromException(error) : Task.CompletedTask;
    }

    public virtual Task ShowMessageAsync(string text, string? detail = null)
    {
        Record("message", () =>
        {
            Messages.Add(text);
            MessagesWithDetail.Add((text, detail));
        });
        return Task.CompletedTask;
    }

    private void Record(string kind, Action add)
    {
        lock (_lock)
        {
            Log.Add(kind);
            add();
        }
    }

    private bool TryTake<T>(Queue<T> answers, string kind, out T answer)
    {
        lock (_lock)
        {
            if (answers.TryDequeue(out answer!)) return true;
            Unscripted.Add(kind);
            return false;
        }
    }
}
