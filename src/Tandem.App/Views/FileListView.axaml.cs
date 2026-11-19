using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tandem.App.ViewModels;

namespace Tandem.App.Views;

/// <summary>The rows of one panel. Mouse: a click moves the cursor and activates the side, Cmd+click toggles the
/// row's mark, a double click opens the row, a right click moves the cursor there and opens the row's context menu;
/// a right click below the rows opens the folder's context menu and leaves the cursor alone.</summary>
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

    /// <summary>The context menu open now, if any; for tests.</summary>
    public ContextMenu? OpenMenu { get; private set; }

    /// <summary>Opens <paramref name="items"/> as a context menu at the cursor's row (or the list), as the main
    /// menu's "Open With" does.</summary>
    public void ShowMenuAtCursor(IReadOnlyList<ContextMenuItem> items)
    {
        var target = Rows.SelectedIndex >= 0 ? Rows.ContainerFromIndex(Rows.SelectedIndex) : null;
        ShowMenu(items, target ?? Rows, PlacementMode.BottomEdgeAlignedLeft);
    }

    private void ShowMenu(IReadOnlyList<ContextMenuItem> items, Control target, PlacementMode placement)
    {
        if (items.Count == 0) return;
        OpenMenu?.Close();
        var menu = ContextMenuView.Build(items);
        menu.Placement = placement;
        menu.Closed += (_, _) =>
        {
            if (OpenMenu != menu) return;
            OpenMenu = null;
            FocusList();
        };
        OpenMenu = menu;
        menu.Open(target);
    }

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

    /// <summary>Focuses the cursor's row; while that row is being renamed its editor keeps the focus.</summary>
    private bool FocusCursorRow() =>
        Rows.SelectedItem is EntryRowViewModel { IsRenaming: true } ||
        (Rows.SelectedIndex >= 0 && Rows.ContainerFromIndex(Rows.SelectedIndex) is { } row && row.Focus());

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not SideViewModel side) return;
        // Clicks inside the rename editor belong to it (caret, selection).
        if ((e.Source as Visual)?.FindAncestorOfType<TextBox>(includeSelf: true) is not null) return;
        var point = e.GetCurrentPoint(Rows);
        var item = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true);
        var index = item is null ? -1 : Rows.IndexFromContainer(item);
        if (index < 0)
        {
            // The scroll bar or the empty area below the rows; a right click below the rows is the folder's menu.
            var onScrollBar = (e.Source as Visual)?.FindAncestorOfType<ScrollBar>(includeSelf: true) is not null;
            if (point.Properties.IsRightButtonPressed && !onScrollBar)
            {
                e.Handled = true;
                var menu = side.ContextMenuForEmptySpace();
                FocusList();
                ShowMenu(menu, Rows, PlacementMode.Pointer);
                return;
            }
            side.Activate();
            FocusList();
            return;
        }

        e.Handled = true;
        var cmd = e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (point.Properties.IsRightButtonPressed)
        {
            // The cursor goes to the row first, so the menu acts on the row under the pointer.
            var menu = side.ContextMenuForRow(index);
            FocusList();
            ShowMenu(menu, item!, PlacementMode.Pointer);
            return;
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
