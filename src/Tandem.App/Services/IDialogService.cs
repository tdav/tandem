using Tandem.Core.Operations;
using Tandem.Core.Vfs;

namespace Tandem.App.Services;

/// <summary>Checks text typed in a dialog when the user accepts it: a localized error to show (the dialog then stays
/// open) or null to accept.</summary>
public delegate Task<string?> TextValidator(string text);

/// <summary>The copy/move dialog's question. <paramref name="SingleName"/> names the item when there is one;
/// <paramref name="Destination"/> is the proposed text; <paramref name="Validate"/> checks the typed text before the
/// dialog closes.</summary>
public sealed record CopyMoveRequest(bool IsMove, int ItemCount, string? SingleName, string Destination,
    TextValidator? Validate = null);

/// <summary>What the user accepted: the typed text, "verify content after copying" and "start now, do not wait in
/// the queue".</summary>
public sealed record CopyMoveAnswer(string Destination, bool VerifyContent, bool RunImmediately);

/// <summary>
/// The dialogs the file commands and running operations show. Called on the UI thread. Each completes when the
/// dialog closes; a cancelled dialog answers null, false, <see cref="ConflictAction.Cancel"/> or
/// <see cref="ErrorAction.Abort"/>.
/// </summary>
public interface IDialogService
{
    Task<CopyMoveAnswer?> AskCopyMoveAsync(CopyMoveRequest request);

    /// <summary>A name (for a new folder); <paramref name="titleKey"/> is the resource key of the title.</summary>
    Task<string?> AskNameAsync(string titleKey, string initial, TextValidator? validate = null);

    /// <summary>Asks before deleting. <paramref name="permanent"/>: the items will not go to the Trash, and the dialog
    /// says it cannot be undone; its default button is then Cancel.</summary>
    Task<bool> ConfirmDeleteAsync(int itemCount, string? singleName, bool permanent);

    /// <summary>A mask to mark (<paramref name="select"/>) or unmark rows by, starting from
    /// <paramref name="initial"/>.</summary>
    Task<string?> AskMaskAsync(bool select, string initial);

    /// <summary>The destination of <paramref name="source"/> exists. <paramref name="ct"/> is cancelled when the
    /// question is no longer needed (the operation was cancelled or the app is closing): the dialog closes.</summary>
    Task<ConflictDecision> AskConflictAsync(FileEntry source, FileEntry existing, CancellationToken ct);

    /// <summary>An item failed: <paramref name="message"/> says what happened in words, <paramref name="detail"/>
    /// is the system's own message. <paramref name="ct"/> as for <see cref="AskConflictAsync"/>.</summary>
    Task<ErrorAction> AskErrorAsync(VfsPath item, string message, string? detail, CancellationToken ct);

    /// <summary>The items an operation left alone, and why.</summary>
    Task ShowSummaryAsync(IReadOnlyList<SkippedItem> skipped);

    Task ShowMessageAsync(string text);
}
