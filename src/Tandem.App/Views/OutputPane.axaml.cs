using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tandem.App.ViewModels;

namespace Tandem.App.Views;

/// <summary>
/// The command line's output pane, above the command line. The text is plain, monospace, selectable and copyable
/// (Cmd+C); Ctrl+C kills a running command. New output keeps the view at the end unless the user has scrolled up,
/// and a selection survives output being appended.
/// </summary>
public partial class OutputPane : UserControl
{
    /// <summary>How close to the end, in pixels, still counts as "at the end".</summary>
    private const double EndTolerance = 4;

    private CommandLineViewModel? _model;

    public OutputPane()
    {
        InitializeComponent();
        OutputText.AddHandler(KeyDownEvent, OnTextKeyDown, RoutingStrategies.Tunnel);
    }

    public TextBox Text => OutputText;

    /// <summary>The text's scroll viewer, from the text box's template; null before it is applied.</summary>
    public ScrollViewer? Scroller => OutputText.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_model is not null) _model.PropertyChanged -= OnModelPropertyChanged;
        _model = DataContext as CommandLineViewModel;
        if (_model is not null) _model.PropertyChanged += OnModelPropertyChanged;
        ShowOutput();
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CommandLineViewModel.Output)) ShowOutput();
    }

    private void ShowOutput()
    {
        var text = _model?.Output ?? "";
        var old = OutputText.Text ?? "";
        if (text == old) return;

        var scroller = Scroller;
        var atEnd = scroller is null ||
                    scroller.Offset.Y >= scroller.Extent.Height - scroller.Viewport.Height - EndTolerance;
        var offset = scroller?.Offset;
        var (start, end) = (OutputText.SelectionStart, OutputText.SelectionEnd);

        OutputText.Text = text;
        // Appended to: the selected text is still where it was.
        if (start != end && old.Length > 0 && text.StartsWith(old, StringComparison.Ordinal))
        {
            OutputText.SelectionStart = start;
            OutputText.SelectionEnd = end;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (Scroller is not { } current) return;
            // Only the vertical position follows; the horizontal one stays where the user put it.
            if (atEnd)
                current.Offset = current.Offset.WithY(Math.Max(0, current.Extent.Height - current.Viewport.Height));
            else if (offset is { } kept)
                current.Offset = kept;
        }, DispatcherPriority.Loaded);
    }

    private void OnTextKeyDown(object? sender, KeyEventArgs e)
    {
        if (CommandLineView.TryKill(_model, e)) e.Handled = true;
    }
}
