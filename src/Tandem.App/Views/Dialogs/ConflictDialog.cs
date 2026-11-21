using Avalonia.Controls;
using Tandem.App.Resources;
using Tandem.App.Services;
using Tandem.App.ViewModels.Dialogs;
using Tandem.Core.Operations;
using Tandem.Core.Vfs;

namespace Tandem.App.Views.Dialogs;

/// <summary>The destination exists: both files' size and date, the choices and "apply to all". Closing the window
/// any other way cancels the operation.</summary>
public sealed class ConflictDialog : DialogWindow
{
    /// <param name="compare">When given, opens the comparison of the two files (the source, the existing file) modal
    /// to this dialog. Not used yet: the "Compare…" button comes with the file comparison.</param>
    public ConflictDialog(ConflictDialogViewModel vm, Func<FileEntry, FileEntry, Window, Task>? compare = null)
        : base(vm.Title)
    {
        Compare = compare;
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

    /// <summary>The comparison this dialog was given (<see cref="IDialogService.CompareFiles"/>), or null.</summary>
    public Func<FileEntry, FileEntry, Window, Task>? Compare { get; }
}
