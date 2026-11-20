using Avalonia.Controls;
using Tandem.App.Resources;
using Tandem.App.ViewModels.Dialogs;

namespace Tandem.App.Views.Dialogs;

/// <summary>Alt+F5: the archive file, its format and "start now". The result is the answer, or null when cancelled; a
/// refused text keeps the dialog open with the reason under the field.</summary>
public sealed class PackDialog : DialogWindow
{
    public PackDialog(PackDialogViewModel vm) : base(vm.Title)
    {
        var field = new TextBox { Name = "Destination", Text = vm.DestinationText, MinWidth = 420 };
        var error = ErrorText("Error");
        var formats = new ComboBox
        {
            Name = "Format",
            ItemsSource = vm.Formats.Select(PackDialogViewModel.FormatLabel).ToArray(),
            SelectedIndex = 0,
            MinWidth = 140,
        };
        var now = new CheckBox
        {
            Name = "RunImmediately", Content = Loc.Get("CopyMove_RunImmediately"), IsChecked = vm.RunImmediately,
        };
        field.TextChanged += (_, _) => vm.DestinationText = field.Text ?? "";
        formats.SelectionChanged += (_, _) =>
        {
            if (formats.SelectedIndex >= 0) vm.Format = vm.Formats[formats.SelectedIndex];
        };
        now.IsCheckedChanged += (_, _) => vm.RunImmediately = now.IsChecked == true;
        vm.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(PackDialogViewModel.Error):
                    ShowError(error, vm.Error);
                    break;
                case nameof(PackDialogViewModel.DestinationText) when field.Text != vm.DestinationText:
                    field.Text = vm.DestinationText;
                    break;
            }
        };

        var ok = MakeButton(Loc.Get("Archive_PackButton"), isDefault: true, name: "Ok");
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
        SetContent(field, [Text(vm.Prompt), field, error, Text(Loc.Get("Archive_Format")), formats, now], ok, cancel);
    }
}

/// <summary>Alt+F9: the destination folder, "each archive into a folder of its own" and "start now".</summary>
public sealed class UnpackDialog : DialogWindow
{
    public UnpackDialog(UnpackDialogViewModel vm) : base(vm.Title)
    {
        var field = new TextBox { Name = "Destination", Text = vm.DestinationText, MinWidth = 420 };
        var error = ErrorText("Error");
        var separate = new CheckBox
        {
            Name = "SeparateFolders", Content = Loc.Get("Archive_SeparateFolders"), IsChecked = vm.SeparateFolders,
        };
        var now = new CheckBox
        {
            Name = "RunImmediately", Content = Loc.Get("CopyMove_RunImmediately"), IsChecked = vm.RunImmediately,
        };
        field.TextChanged += (_, _) => vm.DestinationText = field.Text ?? "";
        separate.IsCheckedChanged += (_, _) => vm.SeparateFolders = separate.IsChecked == true;
        now.IsCheckedChanged += (_, _) => vm.RunImmediately = now.IsChecked == true;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(UnpackDialogViewModel.Error)) ShowError(error, vm.Error);
        };

        var ok = MakeButton(Loc.Get("Archive_UnpackButton"), isDefault: true, name: "Ok");
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
        SetContent(field, [Text(vm.Prompt), field, error, separate, now], ok, cancel);
    }
}

/// <summary>An archive's password, in a field that shows only bullets; after a wrong password it says so. The result
/// is the password, or null when cancelled.</summary>
public sealed class PasswordDialog : DialogWindow
{
    public PasswordDialog(PasswordDialogViewModel vm) : base(vm.Title)
    {
        var field = new TextBox { Name = "Password", PasswordChar = '•', RevealPassword = false, MinWidth = 300 };
        field.TextChanged += (_, _) => vm.Password = field.Text ?? "";
        var wrong = ErrorText("Wrong");
        ShowError(wrong, vm.WrongPassword);
        var ok = MakeButton(Loc.Get("Button_OK"), isDefault: true, name: "Ok");
        ok.Click += (_, _) => Close(vm.Accept());
        var cancel = CancelButton();
        cancel.Click += (_, _) => Close(null);
        SetContent(field, [Text(vm.Prompt), wrong, field], ok, cancel);
    }
}
