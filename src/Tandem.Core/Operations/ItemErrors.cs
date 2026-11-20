using System.Runtime.ExceptionServices;
using Tandem.Core.Vfs;

namespace Tandem.Core.Operations;

/// <summary>Puts per-item errors to the user and remembers a "skip all" answer for the rest of the operation.
/// Shared by copy, move and delete (and anything else that works item by item) so they answer errors the same
/// way.</summary>
public sealed class ItemErrors(FileOperation operation, IOperationInteraction interaction)
{
    // Used from the operation's single worker thread only.
    private bool _skipAll;

    /// <summary>Errors that concern one item and can be retried or skipped; anything else (cancellation, a bug, an
    /// error raised by the interaction itself) ends the operation.</summary>
    public static bool IsItemError(Exception e) => e is IOException or UnauthorizedAccessException;

    /// <summary>Puts <paramref name="error"/> on <paramref name="item"/> to the user (unless "skip all" was
    /// chosen). Returns true to retry; false when the item is skipped, which is recorded.</summary>
    /// <exception cref="Exception"><paramref name="error"/> itself, rethrown, when the user aborts.</exception>
    public async Task<bool> ShouldRetryAsync(VfsPath item, Exception error, CancellationToken ct)
    {
        var action = _skipAll
            ? ErrorAction.Skip
            : await interaction.ResolveErrorAsync(item, error, ct).ConfigureAwait(false);
        switch (action)
        {
            case ErrorAction.Retry:
                return true;
            case ErrorAction.Abort:
                ExceptionDispatchInfo.Throw(error);
                return false; // not reached
            case ErrorAction.SkipAll:
                _skipAll = true;
                break;
        }
        operation.AddSkipped(new SkippedItem(item, SkipReason.Error, error.Message));
        return false;
    }

    /// <summary>Runs <paramref name="attempt"/> until it succeeds (true) or the user skips the item (false),
    /// asking after each item error.</summary>
    public async Task<bool> RunAsync(VfsPath item, Func<Task> attempt, CancellationToken ct)
    {
        while (true)
        {
            try
            {
                await attempt().ConfigureAwait(false);
                return true;
            }
            catch (Exception e) when (IsItemError(e))
            {
                if (!await ShouldRetryAsync(item, e, ct).ConfigureAwait(false)) return false;
                ct.ThrowIfCancellationRequested();
            }
        }
    }
}
