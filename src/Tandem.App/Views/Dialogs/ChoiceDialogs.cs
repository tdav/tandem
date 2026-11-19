using Avalonia.Controls;
using Avalonia.Layout;
using Tandem.App.Resources;
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

/// <summary>An item failed: retry, skip, skip all or abort; closing the window aborts.</summary>
public sealed class ErrorDialog : DialogWindow
{
    public ErrorDialog(ErrorDialogViewModel vm) : base(vm.Title)
    {
        var buttons = vm.Actions.Select(action =>
        {
            var button = MakeButton(Loc.Enum(action), isDefault: action == ErrorAction.Retry,
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
