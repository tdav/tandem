using Tandem.App.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Tandem.App.ViewModels.Net;

namespace Tandem.App.Views.Net;

public sealed partial class TerminalView : UserControl
{
    private TerminalViewModel? _model;
    private bool _attached;

    public TerminalView()
    {
        InitializeComponent();
        Terminal.SizeChanged += (_, _) => UpdateGrid();
        AddHandler(KeyDownEvent, OnTerminalKeyDown, RoutingStrategies.Tunnel);
    }

    internal void EnableKeepOnTop(Window window)
    {
        KeepOnTop.IsVisible = true;
        KeepOnTop.IsCheckedChanged += (_, _) => window.Topmost = KeepOnTop.IsChecked == true;
        KeepOnTop.Click += (_, _) => FocusTerminal();
    }

    internal void FocusTerminal() => Terminal.Focus();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        AttachModel();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        AttachModel();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        AttachModel();
        base.OnDetachedFromVisualTree(e);
    }

    private void AttachModel()
    {
        var next = _attached ? DataContext as TerminalViewModel : null;
        if (ReferenceEquals(_model, next)) return;
        Terminal.Detach();
        _model = next;
        if (next is not null)
        {
            next.AttachClipboard(TopLevel.GetTopLevel(this));
            Terminal.Attach(next);
            UpdateGrid();
        }
    }

    private void UpdateGrid()
    {
        if (_model is not { } model || Terminal.Bounds.Width <= 0 || Terminal.Bounds.Height <= 0) return;
        var grid = Terminal.GridSize;
        model.ShowAtSize(grid.Columns, grid.Rows);
    }

    private async void OnTerminalKeyDown(object? sender, KeyEventArgs e)
    {
        if (_model is not { } model) return;
        if (PlatformKeys.IsWindows)
        {
            // Ctrl belongs to the remote shell (Ctrl+C interrupts); the clipboard is Ctrl+Shift+C/V, as in Windows
            // Terminal.
            if (e.KeyModifiers != (KeyModifiers.Control | KeyModifiers.Shift) || e.Key is not (Key.C or Key.V)) return;
            e.Handled = true;
        }
        else
        {
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Meta)) return;
            e.Handled = true;
            if (e.KeyModifiers != KeyModifiers.Meta) return;
        }
        if (e.Key == Key.C) await model.CopySelectionAsync();
        else if (e.Key == Key.V) await model.PasteAsync();
    }
}
