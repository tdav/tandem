using Avalonia.Controls;

namespace Tandem.App.Views.Dialogs;

/// <summary>The progress window, bound to <see cref="ViewModels.OperationsViewModel"/>. The main window opens and
/// closes it as <see cref="ViewModels.OperationsViewModel.IsWindowVisible"/> says.</summary>
public partial class ProgressWindow : Window
{
    public ProgressWindow()
    {
        InitializeComponent();
    }
}
