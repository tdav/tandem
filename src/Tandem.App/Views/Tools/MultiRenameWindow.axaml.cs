using Avalonia.Controls;

namespace Tandem.App.Views.Tools;

public sealed partial class MultiRenameWindow : Window
{
    public MultiRenameWindow()
    {
        InitializeComponent();
        Opened += (_, _) => NameField.Focus();
    }
}
