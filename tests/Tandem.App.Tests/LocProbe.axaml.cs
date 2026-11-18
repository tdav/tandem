using Avalonia.Controls;

namespace Tandem.App.Tests;

/// <summary>Markup that uses <c>{loc:Tr}</c>, so the XAML compiler checks the extension is usable.</summary>
public partial class LocProbe : UserControl
{
    public LocProbe()
    {
        InitializeComponent();
    }
}
