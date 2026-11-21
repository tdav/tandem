using Avalonia.Controls;
using Avalonia.Threading;
using Tandem.App.Resources;
using Tandem.App.ViewModels;
using Tandem.App.ViewModels.Dialogs;
using Tandem.App.Views;
using Tandem.App.Views.Dialogs;
using Tandem.Core.Operations;
using Tandem.Core.Vfs;

namespace Tandem.App.Services;

/// <summary>
/// Shows the file-command dialogs as windows modal to <see cref="Owner"/> (the main window). While there is no
/// visible owner (before the window opens or after it closes) a question is answered as cancelled without showing
/// anything; a message is still shown on its own. UI thread only.
/// </summary>
public sealed class DialogService : IDialogService
{
    /// <summary>The window the dialogs belong to; set by the app once the main window exists.</summary>
    public Window? Owner { get; set; }

    public Func<FileEntry, FileEntry, Window, Task>? CompareFiles { get; set; }

    public Task<CopyMoveAnswer?> AskCopyMoveAsync(CopyMoveRequest request) =>
        ShowAsync<CopyMoveAnswer>(new CopyMoveDialog(new CopyMoveDialogViewModel(request)));

    public Task<string?> AskNewFolderNameAsync(string initial, TextValidator? validate = null) =>
        ShowAsync<string>(new NameDialog(new NameDialogViewModel("NewFolder_Title", initial, validate),
            Loc.Get("NewFolder_Prompt"), Loc.Get("Button_Create")));

    public async Task<bool> ConfirmDeleteAsync(int itemCount, string? singleName, bool permanent) =>
        await ShowAsync<object>(new ConfirmDeleteDialog(new ConfirmDeleteDialogViewModel(itemCount, singleName,
            permanent))) is true;

    public Task<string?> AskMaskAsync(bool select, string initial) =>
        ShowAsync<string>(new MaskDialog(new MaskDialogViewModel(select, initial), select));

    public async Task<ConflictDecision> AskConflictAsync(FileEntry source, FileEntry existing, CancellationToken ct) =>
        await ShowAsync<object>(new ConflictDialog(new ConflictDialogViewModel(source, existing), CompareFiles), ct) is
            ConflictDecision decision
            ? decision
            : new ConflictDecision(ConflictAction.Cancel, false);

    public async Task<ErrorAction> AskErrorAsync(VfsPath item, string message, string? detail, bool connectionLost,
        CancellationToken ct) =>
        await ShowAsync<object>(new ErrorDialog(new ErrorDialogViewModel(item, message, detail, connectionLost)), ct) is
            ErrorAction action
            ? action
            : ErrorAction.Abort;

    public Task ShowSummaryAsync(IReadOnlyList<SkippedItem> skipped) =>
        ShowAsync<object>(new SummaryDialog(new SummaryDialogViewModel(skipped)));

    public async Task<bool> ShowPropertiesAsync(PropertiesViewModel properties) =>
        await ShowAsync<object>(new PropertiesDialog(properties)) is true;

    public async Task<bool> ConfirmQuitAsync(int operationCount) =>
        await ShowAsync<object>(new ConfirmQuitDialog(operationCount)) is true;

    public async Task<SaveChangesAnswer> AskSaveChangesAsync(string fileName, Window? owner = null) =>
        await ShowAsync<object>(new SaveChangesDialog(fileName), owner: owner) is SaveChangesAnswer answer
            ? answer
            : SaveChangesAnswer.Cancel;

    public async Task<bool> ConfirmEditorSaveAsync(EditorSaveQuestion question, string fileName, string encoding,
        Window? owner = null) =>
        await ShowAsync<object>(new EditorSaveDialog(question, fileName, encoding), owner: owner) is true;

    public async Task<bool> ConfirmReassignShortcutAsync(string chord, string ownerLabel, Window? owner = null) =>
        await ShowAsync<object>(new ReassignShortcutDialog(chord, ownerLabel), owner: owner) is true;

    public Task<PackAnswer?> AskPackAsync(PackRequest request) =>
        ShowAsync<PackAnswer>(new PackDialog(new PackDialogViewModel(request)));

    public Task<UnpackAnswer?> AskUnpackAsync(UnpackRequest request) =>
        ShowAsync<UnpackAnswer>(new UnpackDialog(new UnpackDialogViewModel(request)));

    public Task<string?> AskPasswordAsync(string archiveName, bool previousWasWrong, CancellationToken ct) =>
        ShowAsync<string>(new PasswordDialog(new PasswordDialogViewModel(archiveName, previousWasWrong)), ct);

    public Task ShowMessageAsync(string text, string? detail = null) =>
        MessageDialog.ShowAsync(Owner, "Tandem", string.IsNullOrWhiteSpace(detail) ? text : text + "\n\n" + detail);

    /// <summary>The dialog's result, modal to <paramref name="owner"/> or else <see cref="Owner"/>; default when that
    /// window is not shown or <paramref name="ct"/> closes the dialog.</summary>
    private async Task<T?> ShowAsync<T>(Window dialog, CancellationToken ct = default, Window? owner = null)
        where T : class
    {
        if ((owner ?? Owner) is not { IsVisible: true } shownOwner || ct.IsCancellationRequested) return null;
        await using var registration = ct.Register(() => Dispatcher.UIThread.Post(() => dialog.Close()));
        return await dialog.ShowDialog<T?>(shownOwner);
    }
}
