using Avalonia.Controls;
using Tandem.App.Resources;
using Tandem.App.ViewModels.Dialogs;

namespace Tandem.App.Views.Dialogs;

/// <summary>F5/F6: where to copy or move, with "verify content" and "start now". The result is the answer, or null
/// when cancelled; a refused path keeps the dialog open with the reason under the field.</summary>
public sealed class CopyMoveDialog : DialogWindow
{
    public CopyMoveDialog(CopyMoveDialogViewModel vm) : base(vm.Title)
    {
        var field = new TextBox { Name = "Destination", Text = vm.DestinationText, MinWidth = 420 };
        var error = ErrorText("Error");
        var verify = new CheckBox { Name = "Verify", Content = Loc.Get("CopyMove_Verify"), IsChecked = vm.VerifyContent };
        var now = new CheckBox
        {
            Name = "RunImmediately", Content = Loc.Get("CopyMove_RunImmediately"), IsChecked = vm.RunImmediately,
        };
        field.TextChanged += (_, _) => vm.DestinationText = field.Text ?? "";
        verify.IsCheckedChanged += (_, _) => vm.VerifyContent = verify.IsChecked == true;
        now.IsCheckedChanged += (_, _) => vm.RunImmediately = now.IsChecked == true;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CopyMoveDialogViewModel.Error)) ShowError(error, vm.Error);
        };

        var ok = MakeButton(vm.PrimaryButtonText, isDefault: true, name: "Ok");
        ok.Click += async (_, _) =>
        {
            ok.IsEnabled = false;
            try
            {
                if (await vm.AcceptAsync() is { } answer) Close(answer);
            }
            finally
            {
                ok.IsEnabled = true;
            }
        };
        var cancel = CancelButton();
        cancel.Click += (_, _) => Close(null);
        Opened += (_, _) => field.SelectAll();
        SetContent(field, [Text(vm.Prompt), field, error, verify, now], ok, cancel);
    }
}

/// <summary>F7: the new folder's name.</summary>
public sealed class NameDialog : DialogWindow
{
    public NameDialog(NameDialogViewModel vm, string prompt, string primaryText) : base(vm.Title)
    {
        var field = new TextBox { Name = "Name", Text = vm.Text, MinWidth = 360 };
        var error = ErrorText("Error");
        field.TextChanged += (_, _) => vm.Text = field.Text ?? "";
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(NameDialogViewModel.Error)) ShowError(error, vm.Error);
        };
        var ok = MakeButton(primaryText, isDefault: true, name: "Ok");
        ok.Click += async (_, _) =>
        {
            ok.IsEnabled = false;
            try
            {
                if (await vm.AcceptAsync() is { } name) Close(name);
            }
            finally
            {
                ok.IsEnabled = true;
            }
        };
        var cancel = CancelButton();
        cancel.Click += (_, _) => Close(null);
        Opened += (_, _) => field.SelectAll();
        SetContent(field, [Text(prompt), field, error], ok, cancel);
    }
}

/// <summary>Select or deselect by mask.</summary>
public sealed class MaskDialog : DialogWindow
{
    public MaskDialog(MaskDialogViewModel vm, bool select) : base(vm.Title)
    {
        var field = new TextBox { Name = "Mask", Text = vm.Text, MinWidth = 300 };
        var error = ErrorText("Error");
        field.TextChanged += (_, _) => vm.Text = field.Text ?? "";
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MaskDialogViewModel.Error)) ShowError(error, vm.Error);
        };
        var ok = MakeButton(Loc.Get(select ? "Button_Select" : "Button_Deselect"), isDefault: true, name: "Ok");
        ok.Click += (_, _) =>
        {
            if (vm.Accept() is { } mask) Close(mask);
        };
        var cancel = CancelButton();
        cancel.Click += (_, _) => Close(null);
        Opened += (_, _) => field.SelectAll();
        SetContent(field, [field, Text(vm.Hint, secondary: true), error], ok, cancel);
    }
}
