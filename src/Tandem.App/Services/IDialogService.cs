using Tandem.App.ViewModels;
using Tandem.Core.Operations;
using Tandem.Core.Vfs;

namespace Tandem.App.Services;

/// <summary>Checks text typed in a dialog when the user accepts it: a localized error to show (the dialog then stays
/// open) or null to accept.</summary>
public delegate Task<string?> TextValidator(string text);

/// <summary>The copy/move dialog's question. <paramref name="SingleName"/> names the item when there is one;
/// <paramref name="Destination"/> is the proposed text; <paramref name="Validate"/> checks the typed text before the
/// dialog closes. With <paramref name="OffersBoth"/> (a drop) the dialog offers Copy, Move and Cancel, and
/// <paramref name="IsMove"/> only says which of the two is pre-selected; otherwise (F5/F6) it is the one action.</summary>
public sealed record CopyMoveRequest(bool IsMove, int ItemCount, string? SingleName, string Destination,
    TextValidator? Validate = null, bool OffersBoth = false);

/// <summary>What the user accepted: the typed text, "verify content after copying", "start now, do not wait in
/// the queue", and the action chosen (<paramref name="IsMove"/>; always the request's own for F5/F6).</summary>
public sealed record CopyMoveAnswer(string Destination, bool VerifyContent, bool RunImmediately, bool IsMove = false);

/// <summary>
/// The dialogs the file commands and running operations show. Called on the UI thread. Each completes when the
/// dialog closes; a cancelled dialog answers null, false, <see cref="ConflictAction.Cancel"/> or
/// <see cref="ErrorAction.Abort"/>.
/// </summary>
public interface IDialogService
{
    Task<CopyMoveAnswer?> AskCopyMoveAsync(CopyMoveRequest request);

    /// <summary>The name of a new folder ("a/b" makes nested folders), starting from <paramref name="initial"/>.</summary>
    Task<string?> AskNewFolderNameAsync(string initial, TextValidator? validate = null);

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

    /// <summary>The properties dialog of one item; its Apply button runs <see cref="PropertiesViewModel.ApplyAsync"/>
    /// and closes the dialog when that succeeds. True when the changes were applied, false when cancelled.</summary>
    Task<bool> ShowPropertiesAsync(PropertiesViewModel properties);

    /// <summary>The app is asked to quit while <paramref name="operationCount"/> file operations are running or
    /// waiting: true to stop them and quit. "Stay" is the default button.</summary>
    Task<bool> ConfirmQuitAsync(int operationCount);

    /// <summary>A message; <paramref name="detail"/>, when given, is secondary (e.g. the system's own words).</summary>
    Task ShowMessageAsync(string text, string? detail = null);
}
