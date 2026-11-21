using Avalonia.Controls;
using Tandem.App.Services;

namespace Tandem.App.Views.Dialogs;

/// <summary>The one window of every <see cref="ConfirmModel"/>: the question, its detail, the go-ahead button and
/// Cancel (the default button unless the model says otherwise; Esc cancels either way). The buttons answer through the
/// model, which <see cref="ToolWindows"/> turns into the dialog's result.</summary>
public sealed class ConfirmDialog : DialogWindow
{
    public ConfirmDialog(ConfirmModel model) : base("Tandem")
    {
        ArgumentNullException.ThrowIfNull(model);
        DataContext = model;
        var confirm = MakeButton(model.ConfirmLabel, isDefault: !model.CancelIsDefault, name: "Confirm");
        confirm.Click += (_, _) => model.Confirm();
        var cancel = CancelButton(isDefault: model.CancelIsDefault);
        cancel.Click += (_, _) => model.Cancel();
        var body = new List<Control> { Text(model.Text, name: "Message") };
        if (model.Detail is { } detail) body.Add(Text(detail, secondary: true, name: "Detail"));
        SetContent(model.CancelIsDefault ? cancel : confirm, body, confirm, cancel);
    }
}
