using Avalonia;
using Avalonia.Controls;
using Tandem.App.Resources;
using Tandem.App.ViewModels;

namespace Tandem.App.Views;

/// <summary>
/// The output pane detached into its own window, on the same <see cref="CommandLineViewModel"/> as the docked one,
/// so output, Kill and Clear work the same. Closing the window docks the pane again; closing the pane (its Close
/// button) closes the window too.
/// </summary>
public sealed class OutputWindow : Window
{
    private readonly CommandLineViewModel _model;

    public OutputWindow(CommandLineViewModel model)
    {
        _model = model;
        Title = Loc.Get("Output_Title");
        MinWidth = 240;
        MinHeight = 120;
        DataContext = model;
        Content = new OutputPane { DataContext = model };
        Closed += (_, _) => model.DockOutputCommand.Execute(null);
    }

    /// <summary>Where the window was last, to be saved: position in pixels, size in device-independent units.</summary>
    public (PixelPoint Position, double Width, double Height)? NormalBounds { get; private set; }

    /// <summary>Remembers the bounds while the window is in its normal state.</summary>
    public void RecordBounds()
    {
        if (WindowState == WindowState.Normal && IsVisible) NormalBounds = (Position, Width, Height);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WidthProperty || change.Property == HeightProperty) RecordBounds();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        RecordBounds();
        base.OnClosing(e);
    }

    public CommandLineViewModel Model => _model;
}
