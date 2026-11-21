using Avalonia.Controls;
using Tandem.App.Services;
using Tandem.App.ViewModels;
using Tandem.App.ViewModels.Dialogs;
using Tandem.Archives;
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
    /// <summary>For a dialog that offers both actions (a drop): which one each answer chose; null takes the
    /// pre-selected one, as Enter does.</summary>
    private readonly Queue<bool?> _copyMoveChoices = new();

    /// <summary>When set, the copy/move dialog stays "open" until it completes.</summary>
    public TaskCompletionSource? CopyMoveGate { get; set; }
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
    public List<(VfsPath Item, string Message, string? Detail, bool ConnectionLost)> ErrorRequests { get; } = [];
    public List<IReadOnlyList<SkippedItem>> Summaries { get; } = [];
    public List<string> Messages { get; } = [];
    public List<(string Text, string? Detail)> MessagesWithDetail { get; } = [];
    public List<PropertiesViewModel> PropertiesRequests { get; } = [];
    public List<string> ValidationErrors { get; } = [];
    public List<string> Unscripted { get; } = [];
    public Queue<bool> QuitAnswers { get; } = new();
    public List<int> QuitRequests { get; } = [];
    public Queue<SaveChangesAnswer> SaveChangesAnswers { get; } = new();
    /// <summary>The file name of each "save changes?" question.</summary>
    public List<string> SaveChangesRequests { get; } = [];
    /// <summary>The window each "save changes?" question was modal to (null: the main window).</summary>
    public List<object?> SaveChangesOwners { get; } = [];
    public Queue<bool> EditorSaveAnswers { get; } = new();
    /// <summary>Each save question of an editor: which, the file name, the encoding and the window it was modal to.</summary>
    public List<(EditorSaveQuestion Question, string FileName, string Encoding, object? Owner)> EditorSaveRequests
    {
        get;
    } = [];

    public Queue<bool> ReassignAnswers { get; } = new();
    /// <summary>Each "this shortcut is taken: reassign?" question: the chord, the label of the command that has it,
    /// and the window it was modal to.</summary>
    public List<(string Chord, string Owner, object? Window)> ReassignRequests { get; } = [];
    /// <summary>Pack answers: the text (or <see cref="Proposed"/>) after choosing the format (null keeps the
    /// first), run through the real <see cref="PackDialogViewModel"/>; null cancels.</summary>
    public Queue<(string Destination, ArchiveFormat? Format, bool RunImmediately)?> PackAnswers { get; } = new();
    public List<PackRequest> PackRequests { get; } = [];
    /// <summary>The text the pack dialog showed after each scripted format choice, before the text was typed.</summary>
    public List<string> PackTextsAfterFormat { get; } = [];
    public Queue<UnpackAnswer?> UnpackAnswers { get; } = new();
    public List<UnpackRequest> UnpackRequests { get; } = [];
    /// <summary>Passwords to type; null declines. Unscripted, the dialog is declined.</summary>
    public Queue<string?> PasswordAnswers { get; } = new();
    public List<(string Archive, bool PreviousWasWrong)> PasswordRequests { get; } = [];
    /// <summary>For each password question, whether it was asked on the UI thread.</summary>
    public List<bool> PasswordsOnUiThread { get; } = [];

    /// <summary>Every dialog asked for, in order, by kind ("copyMove", "name", "delete", "mask", "conflict", "error",
    /// "summary", "message", "properties", "quit", "saveChanges", "editorSave", "reassign", "pack", "unpack",
    /// "password").</summary>
    public List<string> Log { get; } = [];

    /// <summary>As a scripted destination: accept the text the dialog proposed.</summary>
    public const string Proposed = "\0proposed";

    /// <summary>What the properties dialog does with its model (e.g. edit and apply); closes unchanged when null.</summary>
    public Func<PropertiesViewModel, Task<bool>>? OnProperties { get; set; }

    /// <summary>When set, <see cref="ShowSummaryAsync"/> throws it.</summary>
    public Exception? ThrowOnSummary { get; set; }

    /// <summary>Scripts the copy/move dialog's next answer.</summary>
    /// <param name="move">For a dialog offering both actions: Move (true) or Copy (false) chosen; null takes the
    /// pre-selected action. A keyboard dialog always answers its own action.</param>
    public ScriptedDialogs AnswerCopyMove(string destination = Proposed, bool verify = false,
        bool runImmediately = true, bool? move = null)
    {
        lock (_lock)
        {
            CopyMoveAnswers.Enqueue(new CopyMoveAnswer(destination, verify, runImmediately));
            _copyMoveChoices.Enqueue(move);
        }
        return this;
    }

    public virtual async Task<CopyMoveAnswer?> AskCopyMoveAsync(CopyMoveRequest request)
    {
        Record("copyMove", () => CopyMoveRequests.Add(request));
        if (CopyMoveGate is { } gate) await gate.Task;
        while (true)
        {
            bool? choice = null;
            lock (_lock) _copyMoveChoices.TryDequeue(out choice);
            if (!TryTake(CopyMoveAnswers, "copyMove", out var answer) || answer is null) return null;
            if (answer.Destination == Proposed) answer = answer with { Destination = request.Destination };
            answer = answer with { IsMove = request.OffersBoth ? choice ?? request.IsMove : request.IsMove };
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

    public virtual Task<ErrorAction> AskErrorAsync(VfsPath item, string message, string? detail, bool connectionLost,
        CancellationToken ct)
    {
        Record("error", () => ErrorRequests.Add((item, message, detail, connectionLost)));
        return Task.FromResult(TryTake(ErrorAnswers, "error", out var answer) ? answer : ErrorAction.Abort);
    }

    public virtual Task ShowSummaryAsync(IReadOnlyList<SkippedItem> skipped)
    {
        Record("summary", () => Summaries.Add(skipped));
        return ThrowOnSummary is { } error ? Task.FromException(error) : Task.CompletedTask;
    }

    /// <summary>Unscripted, it stays (the dialog's default).</summary>
    public virtual Task<bool> ConfirmQuitAsync(int operationCount)
    {
        Record("quit", () => QuitRequests.Add(operationCount));
        return Task.FromResult(TryTake(QuitAnswers, "quit", out var answer) && answer);
    }

    /// <summary>Unscripted, it cancels (as closing the dialog does).</summary>
    public virtual Task<SaveChangesAnswer> AskSaveChangesAsync(string fileName, Window? owner = null)
    {
        Record("saveChanges", () =>
        {
            SaveChangesRequests.Add(fileName);
            SaveChangesOwners.Add(owner);
        });
        return Task.FromResult(TryTake(SaveChangesAnswers, "saveChanges", out var answer)
            ? answer
            : SaveChangesAnswer.Cancel);
    }

    /// <summary>Unscripted, it answers no (the dialog's default).</summary>
    public virtual Task<bool> ConfirmEditorSaveAsync(EditorSaveQuestion question, string fileName,
        string encoding, Window? owner = null)
    {
        Record("editorSave", () => EditorSaveRequests.Add((question, fileName, encoding, owner)));
        return Task.FromResult(TryTake(EditorSaveAnswers, "editorSave", out var answer) && answer);
    }

    /// <summary>Unscripted, it answers no (the dialog's default).</summary>
    public virtual Task<bool> ConfirmReassignShortcutAsync(string chord, string ownerLabel, Window? owner = null)
    {
        Record("reassign", () => ReassignRequests.Add((chord, ownerLabel, owner)));
        return Task.FromResult(TryTake(ReassignAnswers, "reassign", out var answer) && answer);
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

    public virtual Task<bool> ShowPropertiesAsync(PropertiesViewModel properties)
    {
        Record("properties", () => PropertiesRequests.Add(properties));
        return OnProperties?.Invoke(properties) ?? Task.FromResult(false);
    }

    public ScriptedDialogs AnswerPack(string destination = Proposed, ArchiveFormat? format = null,
        bool runImmediately = true)
    {
        lock (_lock) PackAnswers.Enqueue((destination, format, runImmediately));
        return this;
    }

    public ScriptedDialogs AnswerUnpack(string destination = Proposed, bool? separateFolders = null,
        bool runImmediately = true)
    {
        lock (_lock) UnpackAnswers.Enqueue(new UnpackAnswer(destination, separateFolders ?? false, runImmediately));
        _unpackKeepsSeparate.Enqueue(separateFolders is null);
        return this;
    }

    private readonly Queue<bool> _unpackKeepsSeparate = new();

    public virtual async Task<PackAnswer?> AskPackAsync(PackRequest request)
    {
        Record("pack", () => PackRequests.Add(request));
        var vm = new PackDialogViewModel(request);
        while (true)
        {
            if (!TryTake(PackAnswers, "pack", out var scripted) || scripted is not { } answer) return null;
            if (answer.Format is { } format) vm.Format = format;
            lock (_lock) PackTextsAfterFormat.Add(vm.DestinationText);
            if (answer.Destination != Proposed) vm.DestinationText = answer.Destination;
            vm.RunImmediately = answer.RunImmediately;
            if (await vm.AcceptAsync() is { } accepted) return accepted;
            lock (_lock) ValidationErrors.Add(vm.Error!);
        }
    }

    public virtual async Task<UnpackAnswer?> AskUnpackAsync(UnpackRequest request)
    {
        Record("unpack", () => UnpackRequests.Add(request));
        while (true)
        {
            if (!TryTake(UnpackAnswers, "unpack", out var answer) || answer is null) return null;
            bool keep;
            lock (_lock) keep = _unpackKeepsSeparate.Dequeue();
            if (answer.Destination == Proposed) answer = answer with { Destination = request.Destination };
            if (keep) answer = answer with { SeparateFolders = request.SeparateFolders };
            if (request.Validate is { } validate && await validate(answer.Destination) is { } error)
            {
                lock (_lock) ValidationErrors.Add(error);
                continue;
            }
            return answer;
        }
    }

    public virtual Task<string?> AskPasswordAsync(string archiveName, bool previousWasWrong, CancellationToken ct)
    {
        Record("password", () =>
        {
            PasswordRequests.Add((archiveName, previousWasWrong));
            PasswordsOnUiThread.Add(Avalonia.Threading.Dispatcher.UIThread.CheckAccess());
        });
        return Task.FromResult(TryTake(PasswordAnswers, "password", out var answer) ? answer : null);
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
