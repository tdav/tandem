using Avalonia.Controls;
using Tandem.App.ViewModels;
using Tandem.Archives;
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

/// <summary>The pack dialog's question: what is packed (<paramref name="SingleName"/> names the one item), the proposed
/// archive file (<paramref name="Destination"/>, its extension that of the first of <paramref name="Formats"/>), the
/// formats to choose from, and a check of the typed text before the dialog closes.</summary>
public sealed record PackRequest(int ItemCount, string? SingleName, string Destination,
    IReadOnlyList<ArchiveFormat> Formats, TextValidator? Validate = null);

/// <summary>What the user accepted in the pack dialog: the archive file's text, its format, "start now".</summary>
public sealed record PackAnswer(string Destination, ArchiveFormat Format, bool RunImmediately);

/// <summary>The unpack dialog's question: how many archives (<paramref name="SingleName"/> names the one), the
/// proposed destination folder, whether "each archive into a folder of its own" starts checked, and a check of the
/// typed text.</summary>
public sealed record UnpackRequest(int ArchiveCount, string? SingleName, string Destination, bool SeparateFolders,
    TextValidator? Validate = null);

/// <summary>What the user accepted in the unpack dialog.</summary>
public sealed record UnpackAnswer(string Destination, bool SeparateFolders, bool RunImmediately);

/// <summary>The answer to "save the changes?" when an editor with unsaved changes closes.</summary>
public enum SaveChangesAnswer { Save, Discard, Cancel }

/// <summary>What an editor asks before it saves, when saving would change more than the user typed or overwrite
/// someone else's change.</summary>
public enum EditorSaveQuestion
{
    /// <summary>The file changed on disk since the editor read or saved it: overwrite it?</summary>
    OverwriteChangedFile,

    /// <summary>The file has bytes its encoding cannot give back unchanged: save with them replaced?</summary>
    ReplaceUnsavableBytes,

    /// <summary>The text has characters the file's encoding cannot hold: save in UTF-8 instead?</summary>
    SaveAsUtf8,
}

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
    /// is the system's own message. With <paramref name="connectionLost"/> (the source lost its connection) the
    /// retry button reads "Reconnect and Retry"; the answer is still <see cref="ErrorAction.Retry"/>.
    /// <paramref name="ct"/> as for <see cref="AskConflictAsync"/>.</summary>
    Task<ErrorAction> AskErrorAsync(VfsPath item, string message, string? detail, bool connectionLost,
        CancellationToken ct);

    /// <summary>The items an operation left alone, and why.</summary>
    Task ShowSummaryAsync(IReadOnlyList<SkippedItem> skipped);

    /// <summary>The properties dialog of one item; its Apply button runs <see cref="PropertiesViewModel.ApplyAsync"/>
    /// and closes the dialog when that succeeds. True when the changes were applied, false when cancelled.</summary>
    Task<bool> ShowPropertiesAsync(PropertiesViewModel properties);

    /// <summary>The app is asked to quit while <paramref name="operationCount"/> file operations are running or
    /// waiting: true to stop them and quit. "Stay" is the default button.</summary>
    Task<bool> ConfirmQuitAsync(int operationCount);

    /// <summary>An editor with unsaved changes to <paramref name="fileName"/> is closing: save them, discard them, or
    /// keep the editor open (<see cref="SaveChangesAnswer.Cancel"/>, also when the dialog is closed otherwise).
    /// Modal to <paramref name="owner"/> (the editor's window) when given, else to the main window.</summary>
    Task<SaveChangesAnswer> AskSaveChangesAsync(string fileName, Window? owner = null);

    /// <summary>An editor's <paramref name="question"/> before saving <paramref name="fileName"/>, which is in
    /// <paramref name="encoding"/>: true to go on. "Cancel" is the default button; closing the dialog answers false.
    /// Modal to <paramref name="owner"/> when given, else to the main window.</summary>
    Task<bool> ConfirmEditorSaveAsync(EditorSaveQuestion question, string fileName, string encoding,
        Window? owner = null);

    /// <summary>The settings window: the shortcut <paramref name="chord"/> belongs to the command labelled
    /// <paramref name="ownerLabel"/>; true to take it from that command. "Cancel" is the default button; closing the
    /// dialog answers false. Modal to <paramref name="owner"/> when given, else to the main window.</summary>
    Task<bool> ConfirmReassignShortcutAsync(string chord, string ownerLabel, Window? owner = null);

    /// <summary>A message; <paramref name="detail"/>, when given, is secondary (e.g. the system's own words).</summary>
    Task ShowMessageAsync(string text, string? detail = null);

    /// <summary>Alt+F5: where to pack the items and in which format.</summary>
    Task<PackAnswer?> AskPackAsync(PackRequest request);

    /// <summary>Alt+F9: where to unpack the archives.</summary>
    Task<UnpackAnswer?> AskUnpackAsync(UnpackRequest request);

    /// <summary>The password of the archive <paramref name="archiveName"/>, typed in a field that hides it; null when
    /// the user declines. <paramref name="previousWasWrong"/>: the dialog says the last one did not fit.
    /// <paramref name="ct"/> closes the dialog (declined).</summary>
    Task<string?> AskPasswordAsync(string archiveName, bool previousWasWrong, CancellationToken ct);
}
