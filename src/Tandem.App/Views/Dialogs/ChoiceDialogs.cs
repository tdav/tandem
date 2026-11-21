using Avalonia.Controls;
using Avalonia.Layout;
using Tandem.App.Resources;
using Tandem.App.Services;
using Tandem.App.ViewModels.Dialogs;
using Tandem.Core.Operations;

namespace Tandem.App.Views.Dialogs;

/// <summary>F8/Shift+F8: the result is true to delete. For a permanent delete Cancel is the default button, so Enter
/// does not delete.</summary>
public sealed class ConfirmDeleteDialog : DialogWindow
{
    public ConfirmDeleteDialog(ConfirmDeleteDialogViewModel vm) : base(vm.Title)
    {
        var delete = MakeButton(vm.PrimaryButtonText, isDefault: !vm.CancelIsDefault, name: "Delete");
        delete.Click += (_, _) => Close(true);
        var cancel = CancelButton(isDefault: vm.CancelIsDefault);
        cancel.Click += (_, _) => Close(false);
        var body = new List<Control> { Text(vm.Message, name: "Message") };
        if (vm.Warning is { } warning) body.Add(Text(warning, name: "Warning"));
        SetContent(vm.CancelIsDefault ? cancel : delete, body, delete, cancel);
    }
}

/// <summary>Quitting while file operations run: true to stop them and quit. "Stay" is the default button, and closing
/// the dialog any other way stays too.</summary>
public sealed class ConfirmQuitDialog : DialogWindow
{
    public ConfirmQuitDialog(int operationCount) : base(Loc.Get("Quit_Title"))
    {
        var quit = MakeButton(Loc.Get("Button_StopAndQuit"), isDefault: false, name: "StopAndQuit");
        quit.Click += (_, _) => Close(true);
        var stay = MakeButton(Loc.Get("Button_Stay"), isDefault: true, isCancel: true, name: "Stay");
        stay.Click += (_, _) => Close(false);
        SetContent(stay, [Text(Loc.Format("Quit_Message", Loc.Count("Count_Operations", operationCount)),
            name: "Message")], quit, stay);
    }
}

/// <summary>An editor with unsaved changes is closing: Save (the default button), Don't Save, or Cancel (also Esc and
/// closing the dialog), which keeps the editor open.</summary>
public sealed class SaveChangesDialog : DialogWindow
{
    public SaveChangesDialog(string fileName) : base(Loc.Get("SaveChanges_Title"))
    {
        var save = MakeButton(Loc.Get("Button_Save"), isDefault: true, name: "Save");
        save.Click += (_, _) => Close(SaveChangesAnswer.Save);
        var discard = MakeButton(Loc.Get("Button_DontSave"), name: "Discard");
        discard.Click += (_, _) => Close(SaveChangesAnswer.Discard);
        var cancel = CancelButton();
        cancel.Click += (_, _) => Close(SaveChangesAnswer.Cancel);
        SetContent(save, [Text(Loc.Format("SaveChanges_Message", fileName), name: "Message")], discard, cancel, save);
    }
}

/// <summary>An editor's question before saving (<see cref="EditorSaveQuestion"/>): true to go on. "Cancel" is the
/// default button, so Enter does not change the file; Esc and closing the dialog cancel too.</summary>
public sealed class EditorSaveDialog : DialogWindow
{
    public EditorSaveDialog(EditorSaveQuestion question, string fileName, string encoding)
        : base(Loc.Format("EditorSave_Title", fileName))
    {
        var go = MakeButton(Loc.Get(question switch
        {
            EditorSaveQuestion.OverwriteChangedFile => "Button_OverwriteFile",
            EditorSaveQuestion.ReplaceUnsavableBytes => "Button_SaveWithReplacement",
            _ => "Button_SaveInUtf8",
        }), name: "Confirm");
        go.Click += (_, _) => Close(true);
        var cancel = CancelButton(isDefault: true);
        cancel.Click += (_, _) => Close(false);
        var message = Loc.Format("EditorSaveQuestion_" + question, fileName, encoding);
        SetContent(cancel, [Text(message, name: "Message")], go, cancel);
    }
}

/// <summary>The settings window: a recorded shortcut belongs to another command. True to take it from that command;
/// "Cancel" is the default button, and Esc and closing the dialog cancel too.</summary>
public sealed class ReassignShortcutDialog : DialogWindow
{
    public ReassignShortcutDialog(string chord, string ownerLabel) : base(Loc.Get("Settings_Reassign_Title"))
    {
        var reassign = MakeButton(Loc.Get("Button_Reassign"), name: "Reassign");
        reassign.Click += (_, _) => Close(true);
        var cancel = CancelButton(isDefault: true);
        cancel.Click += (_, _) => Close(false);
        SetContent(cancel, [Text(Loc.Format("Settings_Reassign_Message", chord, ownerLabel), name: "Message")],
            reassign, cancel);
    }
}

/// <summary>The destination exists: both files' size and date, the choices and "apply to all". Closing the window
/// any other way cancels the operation.</summary>
public sealed class ConflictDialog : DialogWindow
{
    public ConflictDialog(ConflictDialogViewModel vm) : base(vm.Title)
    {
        var all = new CheckBox { Name = "ApplyToAll", Content = Loc.Get("Conflict_ApplyToAll") };
        all.IsCheckedChanged += (_, _) => vm.ApplyToAll = all.IsChecked == true;
        var buttons = vm.Actions.Select(action =>
        {
            var button = MakeButton(Loc.Enum(action), isDefault: action == vm.DefaultAction,
                isCancel: action == ConflictAction.Cancel, name: action.ToString());
            button.Click += (_, _) => Close(vm.Decide(action));
            return button;
        }).ToArray();
        SetContent(buttons.Single(b => b.IsDefault),
            [Text(vm.Message), Text(vm.SourceDetails, secondary: true), Text(vm.ExistingDetails, secondary: true), all],
            buttons);
    }
}

/// <summary>An item failed: retry (reconnect and retry, when the connection was lost), skip, skip all or abort;
/// closing the window aborts.</summary>
public sealed class ErrorDialog : DialogWindow
{
    public ErrorDialog(ErrorDialogViewModel vm) : base(vm.Title)
    {
        var buttons = vm.Actions.Select(action =>
        {
            var button = MakeButton(vm.Label(action), isDefault: action == ErrorAction.Retry,
                isCancel: action == ErrorAction.Abort, name: action.ToString());
            button.Click += (_, _) => Close(action);
            return button;
        }).ToArray();
        var body = new List<Control> { Text(vm.Message, name: "Message") };
        if (vm.Detail is { } detail) body.Add(Text(detail, secondary: true, name: "Detail"));
        SetContent(buttons[0], body, buttons);
    }
}

/// <summary>What an operation skipped, one line per item with the reason.</summary>
public sealed class SummaryDialog : DialogWindow
{
    public SummaryDialog(SummaryDialogViewModel vm) : base(vm.Title)
    {
        var lines = new StackPanel { Spacing = 6 };
        foreach (var line in vm.Lines)
        {
            var item = new StackPanel { Spacing = 1 };
            item.Children.Add(Text(line.Path));
            item.Children.Add(Text(line.Detail is null ? line.Reason : $"{line.Reason} ({line.Detail})", secondary: true));
            lines.Children.Add(item);
        }
        var scroll = new ScrollViewer
        {
            Content = lines, MaxHeight = 320, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalAlignment = VerticalAlignment.Top,
        };
        var ok = MakeButton(Loc.Get("Button_OK"), isDefault: true, isCancel: true, name: "Ok");
        ok.Click += (_, _) => Close();
        SetContent(ok, [Text(vm.Message), scroll], ok);
    }
}
