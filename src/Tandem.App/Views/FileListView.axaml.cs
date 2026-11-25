using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tandem.App.Input;
using Tandem.App.ViewModels;
using Tandem.Core.Settings;
using Tandem.Core.Vfs;
using Tandem.Core.Vfs.Local;

namespace Tandem.App.Views;

/// <summary>The rows of one panel. Mouse: a click moves the cursor and activates the side, Cmd+click toggles the
/// row's mark, a double click opens the row, a right click moves the cursor there and opens the row's context menu;
/// a right click below the rows opens the folder's context menu and leaves the cursor alone.
/// Drag and drop: pressing a row of a local panel and moving past a few pixels drags its items (the marked ones when
/// the row is marked) to the other panel or to Finder, offering copy and move; files dragged onto the list from
/// Finder or the other panel go, through the copy/move dialog, into the folder row under the pointer or the panel's
/// folder; the dialog asks whether to copy or move. The target is highlighted while the drag is over it.
/// Look: the side's list layout (font, row height, columns) and the window's colour rules for file rows.</summary>
public partial class FileListView : UserControl
{
    private const string DropTargetClass = "dropTarget";
    private const string FileColorClassPrefix = "file";

    private readonly DragGesture _gesture = new();
    private PointerPressedEventArgs? _pressed;
    private int _pressedIndex = -1;
    private IDataTransfer? _draggedData;
    private IReadOnlyList<string>? _draggedPaths;
    private SideViewModel? _side;

    public FileListView()
    {
        InitializeComponent();
        DragStarter = StartSystemDragAsync;
        Rows.AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        Rows.AddHandler(PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        Rows.AddHandler(PointerReleasedEvent, (_, _) => _gesture.Release(), RoutingStrategies.Tunnel,
            handledEventsToo: true);
        Rows.AddHandler(PointerCaptureLostEvent, (_, _) =>
        {
            if (!_gesture.IsDragging) _gesture.Cancel();
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        Rows.AddHandler(DragDrop.DragEnterEvent, OnDragOver);
        Rows.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        Rows.AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        Rows.AddHandler(DragDrop.DropEvent, OnDrop);
        // A row container reused for another item (scrolling, new rows) must not keep a drop highlight.
        Rows.ContainerClearing += (_, e) => e.Container.Classes.Remove(DropTargetClass);
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
        Rows.SizeChanged += (_, _) => RaisePageSize();
        // Rows matching a colour rule: set when a container takes a row, and on every shown row when the rules change.
        Rows.ContainerPrepared += (_, e) => PaintRow(e.Container);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_side is not null)
        {
            _side.PropertyChanged -= OnSidePropertyChanged;
            _side.Layout.PropertyChanged -= OnLayoutPropertyChanged;
        }
        _side = DataContext as SideViewModel;
        if (_side is not null)
        {
            _side.PropertyChanged += OnSidePropertyChanged;
            _side.Layout.PropertyChanged += OnLayoutPropertyChanged;
        }
        PaintRows();
    }

    private void OnSidePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SideViewModel.FileColors)) PaintRows();
    }

    private void OnLayoutPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ListLayoutViewModel.RowHeight)) RaisePageSize();
    }

    /// <summary>The rows that fit in the list, less one, by the layout's row height.</summary>
    private void RaisePageSize()
    {
        var rowHeight = _side?.Layout.RowHeight ?? AppSettings.DefaultRowHeight;
        if (Rows.Bounds.Height <= 0 || rowHeight <= 0) return;
        PageSizeChanged?.Invoke(this, Math.Max(1, (int)(Rows.Bounds.Height / rowHeight) - 1));
    }

    private void PaintRows()
    {
        foreach (var container in Rows.GetRealizedContainers()) PaintRow(container);
    }

    /// <summary>Gives the row's container the class of its colour rule ("file" + colour name), or none; the styles
    /// take the brush from the theme, so a change of theme needs no repaint.</summary>
    private void PaintRow(Control container)
    {
        var colorName = _side is not null && container.DataContext is EntryRowViewModel { Entry: { } entry }
            ? _side.FileColors.ColorNameFor(entry)
            : null;
        var wanted = colorName is null ? null : FileColorClassPrefix + colorName;
        foreach (var name in AppSettings.ColorNames)
        {
            var cls = FileColorClassPrefix + name;
            if (cls != wanted) container.Classes.Remove(cls);
        }
        if (wanted is not null && !container.Classes.Contains(wanted)) container.Classes.Add(wanted);
    }

    /// <summary>Hands the dragged items to the system's drag and drop, for the press that started it; tests replace
    /// it, as a real drag cannot run headless.</summary>
    public Func<PointerPressedEventArgs, IReadOnlyList<FileEntry>, Task> DragStarter { get; set; }

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
        _gesture.Cancel(); // only a plain single press on a row below may start a drag
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
            // A plain single press may become a drag; Cmd+click and double clicks stay what they were.
            if (e.ClickCount == 1 && !cmd)
            {
                _gesture.Press(point.Position);
                _pressed = e;
                _pressedIndex = index;
            }
        }
        FocusList();
    }

    // ---- Dragging out --------------------------------------------------------------------------------------

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_gesture.IsPressed || _gesture.IsDragging) return;
        var point = e.GetCurrentPoint(Rows);
        if (!point.Properties.IsLeftButtonPressed)
        {
            _gesture.Cancel(); // the release happened somewhere we did not see
            return;
        }
        if (!_gesture.Move(point.Position) || _pressed is not { } pressed || DataContext is not SideViewModel side)
            return;
        var items = side.DragItems(_pressedIndex);
        if (items.Count == 0)
        {
            _gesture.Cancel();
            return;
        }
        _ = RunDragAsync(side, pressed, items);
    }

    private async Task RunDragAsync(SideViewModel side, PointerPressedEventArgs pressed, IReadOnlyList<FileEntry> items)
    {
        try
        {
            await DragStarter(pressed, items);
        }
        catch (Exception e)
        {
            side.ReportFailure(e);
        }
        finally
        {
            _gesture.Cancel();
            if (ReferenceEquals(_pressed, pressed)) _pressed = null;
        }
    }

    /// <summary>The items as files and folders of the system's drag, copy or move as the target chooses; what
    /// Finder then does with them is Finder's business.</summary>
    private async Task StartSystemDragAsync(PointerPressedEventArgs pressed, IReadOnlyList<FileEntry> items)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var data = new DataTransfer();
        foreach (var entry in items)
        {
            var path = entry.Path.ToOsPath();
            IStorageItem? item = entry.IsDirectoryLike
                ? await storage.TryGetFolderFromPathAsync(path)
                : await storage.TryGetFileFromPathAsync(path);
            if (item is null) return; // gone meanwhile: no drag
            data.Add(DataTransferItem.CreateFile(item));
        }
        if (!_gesture.IsDragging) return; // released while the items were looked up
        await DragDrop.DoDragDropAsync(pressed, data, DragDropEffects.Copy | DragDropEffects.Move);
    }

    // ---- Dropping in ---------------------------------------------------------------------------------------

    /// <summary>The local paths of the files dragged; null when anything dragged is not a local file or folder.
    /// Read once per drag.</summary>
    private IReadOnlyList<string>? DraggedPaths(IDataTransfer data)
    {
        if (ReferenceEquals(data, _draggedData)) return _draggedPaths;
        _draggedData = data;
        _draggedPaths = LocalPathsOf(data);
        return _draggedPaths;
    }

    /// <summary>The local paths of every item of <paramref name="data"/>; null when there is none, or any item is not
    /// a file or folder with a local path (a web link, text, a file promise).</summary>
    public static IReadOnlyList<string>? LocalPathsOf(IDataTransfer data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var items = data.Items;
        if (items.Count == 0) return null;
        var paths = new List<string>(items.Count);
        foreach (var item in items)
        {
            if (!item.Contains(DataFormat.File) || item.TryGetFile()?.TryGetLocalPath() is not { Length: > 0 } path)
                return null;
            paths.Add(path);
        }
        return paths;
    }

    /// <summary>
    /// Where a drop with these event arguments would go, the effect to report, and whether Move is to be pre-selected
    /// in the dialog. Null when the drop is not allowed there, a dialog of the app is open, or the platform offers no
    /// operation at all.
    /// </summary>
    /// <remarks>
    /// The effect reported to the source is Copy whenever it is offered (else the first offered of Move, Link): this
    /// app copies or moves by itself after the user confirms, and a source must never act on a Move (delete, refresh)
    /// for a drop the user may still cancel. A specific effect missing from the offer never refuses a valid drop.
    /// Cmd is only a hint for the pre-selected action: on macOS a Cmd-drag offers no operation Avalonia maps (AppKit
    /// filters the mask to "generic"), and the key may not reach this app while another one owns the drag.
    /// </remarks>
    private (IReadOnlyList<string> Paths, DropTarget Target, DragDropEffects Effect, bool PreferMove)? Evaluate(
        DragEventArgs e)
    {
        var offered = e.DragEffects & (DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
        if (offered == DragDropEffects.None || IsDialogOpen()) return null;
        if (DataContext is not SideViewModel side || DraggedPaths(e.DataTransfer) is not { } paths) return null;
        var item = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true);
        var index = item is null ? -1 : Rows.IndexFromContainer(item);
        if (side.DropTargetFor(paths, index) is not { } target) return null;
        var effect = offered.HasFlag(DragDropEffects.Copy) ? DragDropEffects.Copy
            : offered.HasFlag(DragDropEffects.Move) ? DragDropEffects.Move
            : DragDropEffects.Link;
        return (paths, target, effect, e.KeyModifiers.HasFlag(KeyModifiers.Meta));
    }

    /// <summary>A dialog of the app (owned by its window; the progress window is not a dialog) is open.</summary>
    private bool IsDialogOpen() =>
        TopLevel.GetTopLevel(this) is Window window &&
        window.OwnedWindows.Any(w => w.IsVisible && w is not Dialogs.ProgressWindow);

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var drop = Evaluate(e);
        e.DragEffects = drop?.Effect ?? DragDropEffects.None;
        e.Handled = true;
        Highlight(drop?.Target);
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        Highlight(null);
        _draggedData = null;
        _draggedPaths = null;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        var drop = Evaluate(e);
        Highlight(null);
        _draggedData = null;
        _draggedPaths = null;
        e.DragEffects = drop?.Effect ?? DragDropEffects.None;
        e.Handled = true;
        if (drop is not { } accepted || DataContext is not SideViewModel side) return;
        // The dialog opens after the system's drop has returned, never inside it.
        // The user chooses copy or move there; Cmd reported at the drop pre-selects Move.
        Dispatcher.UIThread.Post(() => _ = side.DropAsync(accepted.Paths, accepted.Target, accepted.PreferMove));
    }

    /// <summary>Highlights the target's folder row, or the whole list for the panel's own folder; null clears. Run
    /// on every drag-over, so a highlight follows rows that moved under the pointer.</summary>
    private void Highlight(DropTarget? target)
    {
        var row = target?.RowIndex is { } index ? Rows.ContainerFromIndex(index) : null;
        foreach (var container in Rows.GetRealizedContainers())
            if (!ReferenceEquals(container, row)) container.Classes.Remove(DropTargetClass);
        row?.Classes.Add(DropTargetClass);
        Rows.Classes.Set(DropTargetClass, target is { RowIndex: null });
    }
}
