using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Tandem.App.Resources;
using Tandem.App.ViewModels.Tools;
using Tandem.App.Views.Dialogs;
using Tandem.Core.Tools.Checksums;

namespace Tandem.App.Views.Tools;

public sealed class ChecksumCreateDialog : DialogWindow
{
    public ChecksumCreateDialog(ChecksumCreateViewModel model) : base(Loc.Get("Checksum_CreateTitle"))
    {
        DataContext = model;
        var algorithm = new ComboBox
        {
            Name = "Algorithm",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = Enum.GetValues<ChecksumAlgorithm>(),
            ItemTemplate = new FuncDataTemplate<ChecksumAlgorithm>((value, _) => new TextBlock { Text = Loc.Enum(value) }),
        };
        algorithm.Bind(ComboBox.SelectedItemProperty,
            DialogBinding<ChecksumCreateViewModel>.Property(nameof(model.Algorithm), m => m.Algorithm, (m, value) => m.Algorithm = value));
        var name = new TextBox { Name = "Name" };
        name.Bind(TextBox.TextProperty,
            DialogBinding<ChecksumCreateViewModel>.Property(nameof(model.Name), m => m.Name, (m, value) => m.Name = value));
        var error = ErrorText("Error");
        model.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(model.Error)) ShowError(error, model.Error); };
        var create = MakeButton(Loc.Get("Checksum_Create"), isDefault: true, name: "Create");
        create.Command = model.AcceptCommand;
        var cancel = CancelButton();
        cancel.Command = model.CancelCommand;
        SetContent(name, [Text(Loc.Get("Checksum_Algorithm")), algorithm, Text(Loc.Get("Checksum_FileName")), name, error],
            create, cancel);
    }
}

public sealed class ChecksumResultsWindow : Window
{
    public ChecksumResultsWindow(ChecksumResultsViewModel model)
    {
        DataContext = model;
        Title = Loc.Get("Checksum_ResultsTitle");
        Width = 780;
        Height = 440;
        MinWidth = 480;
        MinHeight = 220;
        var layout = new DockPanel { Margin = new Thickness(16), LastChildFill = true };
        var summary = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(summary, Dock.Top);
        summary.Children.Add(new TextBlock { Text = model.Summary });
        if (model.UnreadableLineNumbers.Count > 0)
            summary.Children.Add(new TextBlock
            {
                Text = Loc.Format("Checksum_UnreadableLines", string.Join(", ", model.UnreadableLineNumbers)),
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            });
        layout.Children.Add(summary);
        var list = new ListBox
        {
            ItemsSource = model.Rows,
            ItemTemplate = new FuncDataTemplate<VerifyResult>((result, _) =>
            {
                var row = new StackPanel { Spacing = 3 };
                if (result is null) return row;
                row.Children.Add(new TextBlock
                {
                    Text = Loc.Format("Checksum_ResultRow", result.RelativePath, Loc.Enum(result.Status)),
                });
                if (result.Actual is not null && result.Status == VerifyStatus.Mismatch)
                    row.Children.Add(new TextBlock
                    {
                        Text = Loc.Format("Checksum_MismatchDetails", result.Expected, result.Actual),
                        Classes = { "secondary" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    });
                return row;
            }),
        };
        layout.Children.Add(list);
        Content = layout;
    }
}
