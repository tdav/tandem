using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tandem.App.ViewModels;

namespace Tandem.App.Views;

/// <summary>The rows of one panel. Mouse: a click moves the cursor and activates the side, Cmd+click toggles the
/// row's mark, a double click opens the row, a right click moves the cursor and raises
/// <see cref="RowContextRequested"/>.</summary>
public partial class FileListView : UserControl
{
    public FileListView()
    {
        InitializeComponent();
        Rows.AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        // The highlight only ever changes through the panel's cursor; keep the focused row on it as well.
        // When the rows are replaced the focused row may be recycled and the focus lost; it then returns to the
        // cursor's row (for the active side), so no key can fall through to the window's focus navigation.
        Rows.SelectionChanged += (_, _) =>
        {
            if (!OwnsFocus()) return;
            if (!FocusCursorRow())
                Dispatcher.UIThread.Post(() =>
                {
                    if (OwnsFocus()) FocusCursorRow();
                }, DispatcherPriority.Loaded);
        };
        Rows.SizeChanged += (_, e) => PageSizeChanged?.Invoke(this,
            Math.Max(1, (int)(e.NewSize.Height / FileListColumns.RowHeightValue) - 1));
    }

    /// <summary>A right click on a row (its index); the context menu comes with the file commands (Task 22).</summary>
    public event EventHandler<int>? RowContextRequested;

    /// <summary>How many rows fit in the list, for PageUp/PageDown.</summary>
    public event EventHandler<int>? PageSizeChanged;

    /// <summary>Gives the keyboard focus to the list, on the cursor's row when it is shown.</summary>
    public void FocusList()
    {
        if (FocusCursorRow()) return;
        Rows.Focus();
        // The rows may just have been replaced and not be realized yet: try again once layout has run.
        Dispatcher.UIThread.Post(() =>
        {
            var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
            if (focused is null || ReferenceEquals(focused, Rows)) FocusCursorRow();
        }, DispatcherPriority.Loaded);
    }

    /// <summary>The list has the focus, or nobody has and this is the active side's list.</summary>
    private bool OwnsFocus() =>
        Rows.IsKeyboardFocusWithin ||
        (DataContext is SideViewModel { IsActive: true } &&
         TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is null);

    private bool FocusCursorRow() =>
        Rows.SelectedIndex >= 0 && Rows.ContainerFromIndex(Rows.SelectedIndex) is { } row && row.Focus();

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not SideViewModel side) return;
        var point = e.GetCurrentPoint(Rows);
        var item = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true);
        var index = item is null ? -1 : Rows.IndexFromContainer(item);
        if (index < 0)
        {
            side.Activate(); // the scroll bar or the empty area below the rows
            FocusList();
            return;
        }

        e.Handled = true;
        var cmd = e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (point.Properties.IsRightButtonPressed)
        {
            side.ClickRow(index, toggleMark: false);
            RowContextRequested?.Invoke(this, index);
        }
        else if (point.Properties.IsLeftButtonPressed)
        {
            if (e.ClickCount >= 2 && !cmd)
                _ = side.OpenRowAsync(index);
            else
                side.ClickRow(index, toggleMark: cmd);
        }
        FocusList();
    }
}
