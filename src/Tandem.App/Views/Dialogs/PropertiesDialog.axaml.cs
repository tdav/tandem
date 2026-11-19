using Avalonia.Controls;
using Avalonia.Interactivity;
using Tandem.App.ViewModels;

namespace Tandem.App.Views.Dialogs;

/// <summary>The properties dialog (Cmd+I), bound to <see cref="PropertiesViewModel"/>. Apply runs
/// <see cref="PropertiesViewModel.ApplyAsync"/> and closes with true when it succeeds; on failure the dialog stays open
/// with the reason. Cancel closes with null.</summary>
public partial class PropertiesDialog : Window
{
    /// <summary>For the designer only.</summary>
    public PropertiesDialog()
    {
        InitializeComponent();
    }

    public PropertiesDialog(PropertiesViewModel properties) : this()
    {
        DataContext = properties;
        Opened += (_, _) => Apply.Focus();
    }

    private async void OnApply(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not PropertiesViewModel properties) return;
        Apply.IsEnabled = false;
        try
        {
            if (await properties.ApplyAsync()) Close(true);
        }
        finally
        {
            Apply.IsEnabled = true;
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
