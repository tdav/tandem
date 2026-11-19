using System.Runtime.CompilerServices;
using Tandem.Core.Operations;
using Tandem.Core.Vfs;

namespace Tandem.App.Services;

/// <summary>
/// Puts the operations' questions (name conflicts, item errors) to the user through <see cref="IDialogService"/>.
/// One instance serves every operation, so only one question is on screen at a time; the others wait in the order
/// they arrived.
/// </summary>
/// <remarks>
/// Questions arrive on operation threads and are posted to the UI thread given at construction. A question never
/// leaves its operation hanging: when the operation is cancelled, or <see cref="Close"/> is called (the window is
/// closing), it is answered at once — a conflict with <see cref="ConflictAction.Cancel"/>, an error with
/// <see cref="ErrorAction.Abort"/> — and a dialog showing it is told to close. A dialog that fails gives the same
/// answers, and the failure goes to the handler passed in.
/// </remarks>
public sealed class DialogOperationInteraction : IOperationInteraction
{
    private static readonly ConflictDecision CancelDecision = new(ConflictAction.Cancel, false);

    private readonly IDialogService _dialogs;
    private readonly Action<Exception>? _onFailure;
    private readonly SynchronizationContext _ui;
    private readonly CancellationTokenSource _closed = new();
    private readonly Queue<Func<Task>> _waiting = new(); // UI thread only
    private bool _asking; // UI thread only
    private readonly ConditionalWeakTable<Exception, object> _userAborts = new();

    /// <summary>Created on the UI thread, whose context the questions are posted to.</summary>
    public DialogOperationInteraction(IDialogService dialogs, Action<Exception>? onFailure = null)
    {
        ArgumentNullException.ThrowIfNull(dialogs);
        _dialogs = dialogs;
        _onFailure = onFailure;
        _ui = SynchronizationContext.Current
              ?? throw new InvalidOperationException("The interaction must be created on the UI thread.");
    }

    public Task<ConflictDecision> ResolveConflictAsync(FileEntry source, FileEntry existing, CancellationToken ct) =>
        AskAsync(ct, CancelDecision, token => _dialogs.AskConflictAsync(source, existing, token));

    public Task<ErrorAction> ResolveErrorAsync(VfsPath item, Exception error, CancellationToken ct) =>
        AskAsync(ct, ErrorAction.Abort, async token =>
        {
            var action = await _dialogs.AskErrorAsync(item, OperationTexts.ForError(item, error), error.Message, token);
            // A dialog closed by its token (operation cancelled, window closing) answers Abort too; that is not
            // the user's choice, so the failure must still be reported.
            if (action == ErrorAction.Abort && !token.IsCancellationRequested) _userAborts.AddOrUpdate(error, error);
            return action;
        });

    /// <summary>Whether the user chose Abort in the dialog for <paramref name="error"/> (the engine then ends the
    /// operation with that very exception, so there is nothing more to tell). The Abort given without asking — the
    /// window closing, the operation cancelled, a failing dialog — is not recorded. Any thread.</summary>
    public bool WasAbortedByUser(Exception error) => _userAborts.TryGetValue(error, out _);

    /// <summary>Answers every pending question as cancelled and every later one at once, without a dialog. Any
    /// thread.</summary>
    public void Close()
    {
        try
        {
            _closed.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // already closed
        }
    }

    /// <summary>Any thread.</summary>
    private Task<T> AskAsync<T>(CancellationToken ct, T fallback, Func<CancellationToken, Task<T>> ask)
    {
        if (ct.IsCancellationRequested || _closed.IsCancellationRequested) return Task.FromResult(fallback);

        var answer = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _closed.Token);
        var registration = linked.Token.Register(() => answer.TrySetResult(fallback));
        _ = answer.Task.ContinueWith(_ =>
        {
            registration.Dispose();
            linked.Dispose();
        }, TaskScheduler.Default);

        async Task Show()
        {
            if (answer.Task.IsCompleted) return; // answered while waiting: never shown
            try
            {
                var shown = ask(linked.Token);
                // The answer may come first (cancelled while shown); the dialog then closes on its own token.
                await Task.WhenAny(shown, answer.Task);
                if (shown.IsCompletedSuccessfully) answer.TrySetResult(shown.Result);
                else if (shown.IsFaulted) Fail(shown.Exception!.GetBaseException());
                else if (shown.IsCanceled) answer.TrySetResult(fallback);
            }
            catch (Exception e)
            {
                Fail(e);
            }

            void Fail(Exception e)
            {
                answer.TrySetResult(fallback);
                _onFailure?.Invoke(e);
            }
        }

        try
        {
            _ui.Post(_ => Enqueue(Show), null);
        }
        catch (Exception)
        {
            answer.TrySetResult(fallback); // the UI is gone
        }
        return answer.Task;
    }

    /// <summary>UI thread.</summary>
    private void Enqueue(Func<Task> show)
    {
        _waiting.Enqueue(show);
        if (!_asking) _ = AskWaitingAsync();
    }

    /// <summary>UI thread. Shows the waiting questions one after another until none is left.</summary>
    private async Task AskWaitingAsync()
    {
        _asking = true;
        try
        {
            while (_waiting.TryDequeue(out var show)) await show();
        }
        finally
        {
            _asking = false;
        }
    }
}
