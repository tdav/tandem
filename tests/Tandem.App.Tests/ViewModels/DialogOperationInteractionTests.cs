using System.Collections.Concurrent;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Tandem.App.Resources;
using Tandem.App.Services;
using Tandem.App.Tests.Views;
using Tandem.Core.Operations;
using Tandem.Core.Vfs;

namespace Tandem.App.Tests.ViewModels;

[Collection(LanguageCollection.Name)]
public class DialogOperationInteractionTests : LanguageTestBase
{
    private static FileEntry File(string path) =>
        new(VfsPath.Local(path), EntryKind.File, 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, null, false);

    /// <summary>Holds every question open until the test answers it; records the order and the thread.</summary>
    private sealed class HeldDialogs : ScriptedDialogs
    {
        public ConcurrentQueue<(string Name, TaskCompletionSource<ConflictDecision> Answer, CancellationToken Ct)>
            Conflicts { get; } = new();

        public ConcurrentQueue<(string Message, string? Detail)> Errors { get; } = new();

        public int Open;
        public int MaxOpen;
        public bool AllOnUiThread = true;
        public Exception? ThrowOnError;

        public override async Task<ConflictDecision> AskConflictAsync(FileEntry source, FileEntry existing,
            CancellationToken ct)
        {
            AllOnUiThread &= Dispatcher.UIThread.CheckAccess();
            MaxOpen = Math.Max(MaxOpen, Interlocked.Increment(ref Open));
            var answer = new TaskCompletionSource<ConflictDecision>();
            Conflicts.Enqueue((source.Name, answer, ct));
            try
            {
                return await answer.Task;
            }
            finally
            {
                Interlocked.Decrement(ref Open);
            }
        }

        public override Task<ErrorAction> AskErrorAsync(VfsPath item, string message, string? detail,
            bool connectionLost, CancellationToken ct)
        {
            AllOnUiThread &= Dispatcher.UIThread.CheckAccess();
            if (ThrowOnError is { } error) throw error;
            Errors.Enqueue((message, detail));
            return Task.FromResult(ErrorAction.Skip);
        }
    }

    private static Task<ConflictDecision> AskFromBackground(DialogOperationInteraction interaction, string name,
        CancellationToken ct = default) =>
        Task.Run(() => interaction.ResolveConflictAsync(File("/src/" + name), File("/dst/" + name), ct));

    [AvaloniaFact]
    public async Task Interaction_serializes_concurrent_questions()
    {
        var dialogs = new HeldDialogs();
        var interaction = new DialogOperationInteraction(dialogs);

        var first = AskFromBackground(interaction, "a");
        await TestApp.WaitUntil(() => dialogs.Conflicts.Count == 1);
        var second = AskFromBackground(interaction, "b");
        await Task.Delay(100);
        Assert.Single(dialogs.Conflicts); // "b" waits while "a" is shown

        dialogs.Conflicts.First().Answer.SetResult(new ConflictDecision(ConflictAction.Skip, false));
        Assert.Equal(ConflictAction.Skip, (await first).Action);
        await TestApp.WaitUntil(() => dialogs.Conflicts.Count == 2);
        dialogs.Conflicts.Last().Answer.SetResult(new ConflictDecision(ConflictAction.Overwrite, true));

        Assert.Equal(new ConflictDecision(ConflictAction.Overwrite, true), await second);
        Assert.Equal(1, dialogs.MaxOpen);
        Assert.True(dialogs.AllOnUiThread);
    }

    [AvaloniaFact]
    public async Task Waiting_questions_are_shown_in_arrival_order()
    {
        var dialogs = new HeldDialogs();
        var interaction = new DialogOperationInteraction(dialogs);
        var first = AskFromBackground(interaction, "a");
        await TestApp.WaitUntil(() => dialogs.Conflicts.Count == 1);

        // One background thread asks "b" then "c": they arrive in that order.
        var later = await Task.Run(() => new[]
        {
            interaction.ResolveConflictAsync(File("/src/b"), File("/dst/b"), CancellationToken.None),
            interaction.ResolveConflictAsync(File("/src/c"), File("/dst/c"), CancellationToken.None),
        });
        for (var shown = 1; shown <= 3; shown++)
        {
            await TestApp.WaitUntil(() => dialogs.Conflicts.Count == shown);
            dialogs.Conflicts.Last().Answer.SetResult(new ConflictDecision(ConflictAction.Skip, false));
        }
        await Task.WhenAll(later.Append(first));

        Assert.Equal(["a", "b", "c"], dialogs.Conflicts.Select(c => c.Name));
    }

    [AvaloniaFact]
    public async Task Cancelling_the_operation_answers_its_waiting_question_without_showing_it()
    {
        var dialogs = new HeldDialogs();
        var interaction = new DialogOperationInteraction(dialogs);
        var first = AskFromBackground(interaction, "a");
        await TestApp.WaitUntil(() => dialogs.Conflicts.Count == 1);
        using var cts = new CancellationTokenSource();
        var second = AskFromBackground(interaction, "b", cts.Token);
        await Task.Delay(50);

        await cts.CancelAsync();

        Assert.Equal(ConflictAction.Cancel, (await second.WaitAsync(TimeSpan.FromSeconds(5))).Action);
        dialogs.Conflicts.First().Answer.SetResult(new ConflictDecision(ConflictAction.Skip, false));
        await first;
        await Task.Delay(50);
        Assert.Single(dialogs.Conflicts); // "b" was never shown
    }

    [AvaloniaFact]
    public async Task Cancelling_the_operation_answers_the_question_being_shown()
    {
        var dialogs = new HeldDialogs();
        var interaction = new DialogOperationInteraction(dialogs);
        using var cts = new CancellationTokenSource();
        var asked = AskFromBackground(interaction, "a", cts.Token);
        await TestApp.WaitUntil(() => dialogs.Conflicts.Count == 1);

        await cts.CancelAsync();

        Assert.Equal(ConflictAction.Cancel, (await asked.WaitAsync(TimeSpan.FromSeconds(5))).Action);
        Assert.True(dialogs.Conflicts.First().Ct.IsCancellationRequested); // the dialog is told to close
    }

    [AvaloniaFact]
    public async Task Closing_answers_every_pending_question_and_later_ones_at_once()
    {
        var dialogs = new HeldDialogs();
        var interaction = new DialogOperationInteraction(dialogs);
        var shown = AskFromBackground(interaction, "a");
        await TestApp.WaitUntil(() => dialogs.Conflicts.Count == 1);
        var waiting = AskFromBackground(interaction, "b");
        await Task.Delay(50);

        interaction.Close();

        Assert.Equal(ConflictAction.Cancel, (await shown.WaitAsync(TimeSpan.FromSeconds(5))).Action);
        Assert.Equal(ConflictAction.Cancel, (await waiting.WaitAsync(TimeSpan.FromSeconds(5))).Action);
        var error = await Task.Run(() =>
            interaction.ResolveErrorAsync(VfsPath.Local("/x"), new IOException("x"), CancellationToken.None));
        Assert.Equal(ErrorAction.Abort, error);
        Assert.Empty(dialogs.Errors);
    }

    public static TheoryData<Exception, string> Errors => new()
    {
        { new VfsAccessDeniedException("raw"), "OperationError_AccessDenied" },
        { new UnauthorizedAccessException("raw"), "OperationError_AccessDenied" },
        { new VfsNotFoundException("raw"), "OperationError_NotFound" },
        { new VfsAlreadyExistsException("raw"), "OperationError_AlreadyExists" },
        { new ContentMismatchException("raw"), "OperationError_ContentMismatch" },
        { new IOException("raw", 28), "OperationError_DiskFull" },
        { new IOException("raw"), "OperationError_IO" },
        { new InvalidOperationException("raw"), "OperationError_Other" },
        { new VfsConnectionLostException("raw"), "OperationError_ConnectionLost" },
    };

    [AvaloniaTheory]
    [MemberData(nameof(Errors))]
    public async Task Error_text_comes_from_the_exception_type_with_the_raw_message_as_detail(Exception error,
        string key)
    {
        var dialogs = new HeldDialogs();
        var interaction = new DialogOperationInteraction(dialogs);

        var action = await Task.Run(() => interaction.ResolveErrorAsync(VfsPath.Local("/d/a.txt"), error,
            CancellationToken.None));

        Assert.Equal(ErrorAction.Skip, action);
        var (message, detail) = Assert.Single(dialogs.Errors);
        Assert.Equal(Loc.Format(key, "/d/a.txt"), message);
        Assert.Equal("raw", detail);
        Assert.True(dialogs.AllOnUiThread);
    }

    [AvaloniaFact]
    public async Task Connection_lost_error_is_asked_as_reconnect()
    {
        var dialogs = new ScriptedDialogs();
        dialogs.ErrorAnswers.Enqueue(ErrorAction.Retry);
        var interaction = new DialogOperationInteraction(dialogs);

        var action = await Task.Run(() => interaction.ResolveErrorAsync(VfsPath.Local("/d/a.txt"),
            new VfsConnectionLostException("reset by peer"), CancellationToken.None));

        Assert.Equal(ErrorAction.Retry, action);
        var request = Assert.Single(dialogs.ErrorRequests);
        Assert.True(request.ConnectionLost);
        Assert.Equal(Loc.Format("OperationError_ConnectionLost", "/d/a.txt"), request.Message);
        Assert.Equal("reset by peer", request.Detail);
    }

    [AvaloniaFact]
    public async Task Other_io_error_is_not_asked_as_reconnect()
    {
        var dialogs = new ScriptedDialogs();
        dialogs.ErrorAnswers.Enqueue(ErrorAction.Skip);
        dialogs.ErrorAnswers.Enqueue(ErrorAction.Skip);
        var interaction = new DialogOperationInteraction(dialogs);

        await Task.Run(() => interaction.ResolveErrorAsync(VfsPath.Local("/a"), new IOException("x"),
            CancellationToken.None));
        await Task.Run(() => interaction.ResolveErrorAsync(VfsPath.Local("/b"), new VfsNotFoundException("y"),
            CancellationToken.None));

        Assert.Equal([false, false], dialogs.ErrorRequests.Select(r => r.ConnectionLost));
    }

    [AvaloniaFact]
    public async Task Only_an_abort_the_user_chose_is_remembered()
    {
        var dialogs = new ScriptedDialogs();
        dialogs.ErrorAnswers.Enqueue(ErrorAction.Abort);
        dialogs.ErrorAnswers.Enqueue(ErrorAction.Skip);
        var interaction = new DialogOperationInteraction(dialogs);
        var chosen = new IOException("chosen");
        var skipped = new IOException("skipped");
        var afterClose = new IOException("after close");

        Assert.Equal(ErrorAction.Abort, await Task.Run(() =>
            interaction.ResolveErrorAsync(VfsPath.Local("/a"), chosen, CancellationToken.None)));
        await Task.Run(() => interaction.ResolveErrorAsync(VfsPath.Local("/b"), skipped, CancellationToken.None));
        interaction.Close();
        Assert.Equal(ErrorAction.Abort, await Task.Run(() =>
            interaction.ResolveErrorAsync(VfsPath.Local("/c"), afterClose, CancellationToken.None)));

        Assert.True(interaction.WasAbortedByUser(chosen));
        Assert.False(interaction.WasAbortedByUser(skipped));
        Assert.False(interaction.WasAbortedByUser(afterClose));
        Assert.False(interaction.WasAbortedByUser(new IOException("never asked")));
    }

    /// <summary>Shows an error dialog until its token closes it, then answers Abort, as the real dialog closed by
    /// its token does.</summary>
    private sealed class ClosedByTokenDialogs : ScriptedDialogs
    {
        public TaskCompletionSource Shown { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Returned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<ErrorAction> AskErrorAsync(VfsPath item, string message, string? detail,
            bool connectionLost, CancellationToken ct)
        {
            Shown.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                // the dialog was closed by its token
            }
            Returned.TrySetResult();
            return ErrorAction.Abort;
        }
    }

    [AvaloniaFact]
    public async Task An_abort_from_a_dialog_closed_by_its_token_is_not_the_users()
    {
        var dialogs = new ClosedByTokenDialogs();
        var interaction = new DialogOperationInteraction(dialogs);
        using var cts = new CancellationTokenSource();
        var error = new IOException("x");
        var asked = Task.Run(() => interaction.ResolveErrorAsync(VfsPath.Local("/a"), error, cts.Token));
        await dialogs.Shown.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await cts.CancelAsync();

        Assert.Equal(ErrorAction.Abort, await asked.WaitAsync(TimeSpan.FromSeconds(5)));
        await dialogs.Returned.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(50); // let the dialog's continuation run on the UI thread
        Assert.False(interaction.WasAbortedByUser(error));
    }

    [AvaloniaFact]
    public async Task A_failing_dialog_aborts_instead_of_hanging_and_is_reported()
    {
        var failures = new List<Exception>();
        var dialogs = new HeldDialogs { ThrowOnError = new InvalidOperationException("boom") };
        var interaction = new DialogOperationInteraction(dialogs, failures.Add);

        var action = await Task.Run(() =>
            interaction.ResolveErrorAsync(VfsPath.Local("/a"), new IOException("x"), CancellationToken.None));

        Assert.Equal(ErrorAction.Abort, action);
        Assert.Equal("boom", Assert.Single(failures).Message);
    }
}
