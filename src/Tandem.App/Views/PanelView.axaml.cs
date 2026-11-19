using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Tandem.App.Input;
using Tandem.App.Resources;
using Tandem.App.ViewModels;

namespace Tandem.App.Views;

/// <summary>One side of the window: volume bar, tabs, path row, column headers, file list and status row. Its data
/// context is a <see cref="SideViewModel"/>.</summary>
public partial class PanelView : UserControl
{
    private SideViewModel? _side;

    public PanelView()
    {
        InitializeComponent();
        PathEditor.AddHandler(KeyDownEvent, OnPathEditorKeyDown, RoutingStrategies.Tunnel);
        PathEditor.LostFocus += OnPathEditorLostFocus;
        foreach (var button in new[] { BackButton, ForwardButton })
        {
            if (button.ContextFlyout is MenuFlyout flyout) flyout.Opening += (_, _) => FillHistoryMenu(flyout);
            button.Holding += (_, e) =>
            {
                if (e.HoldingState == HoldingState.Started) button.ContextFlyout?.ShowAt(button);
            };
        }
        PathRow.AddHandler(PointerPressedEvent, (_, _) => _side?.Activate(), RoutingStrategies.Tunnel);
    }

    /// <summary>The control the favourites menu opens at.</summary>
    public Control PathRowControl => PathRow;

    public FileListView List => FileList;

    public void FocusList() => FileList.FocusList();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_side is not null) _side.PropertyChanged -= OnSidePropertyChanged;
        _side = DataContext as SideViewModel;
        if (_side is not null) _side.PropertyChanged += OnSidePropertyChanged;
    }

    private void OnSidePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SideViewModel.IsEditingPath) || _side is not { } side) return;
        if (side.IsEditingPath)
        {
            // The editor becomes visible with this change; focus it once it is.
            Dispatcher.UIThread.Post(() =>
            {
                if (!side.IsEditingPath) return;
                PathEditor.Focus();
                PathEditor.SelectAll();
            });
        }
        else if (side.IsActive)
        {
            // The editor may already be hidden (and so have lost the focus) when this runs; give the focus back to
            // the list unless it went to another editor.
            var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
            if (focused is null || ReferenceEquals(focused, PathEditor) || !KeyRouting.IsTextEditor(focused))
                FocusList();
        }
    }

    private void OnPathEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (_side is not { } side || e.KeyModifiers != KeyModifiers.None) return;
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                _ = side.CommitPathEditAsync();
                break;
            case Key.Escape:
                e.Handled = true;
                side.CancelPathEdit();
                break;
        }
    }

    private void OnPathEditorLostFocus(object? sender, RoutedEventArgs e)
    {
        // Focus moved elsewhere in the window (not the window losing activation): the edit is abandoned.
        Dispatcher.UIThread.Post(() =>
        {
            if (_side is not { IsEditingPath: true } side) return;
            var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
            if (focused is not null && !ReferenceEquals(focused, PathEditor)) side.CancelPathEdit();
        });
    }

    private void FillHistoryMenu(MenuFlyout flyout)
    {
        flyout.Items.Clear();
        if (_side is not { } side) return;
        foreach (var path in side.HistoryEntries)
            flyout.Items.Add(new MenuItem
            {
                Header = path.Path,
                Command = side.OpenHistoryEntryCommand,
                CommandParameter = path,
            });
        if (flyout.Items.Count == 0)
            flyout.Items.Add(new MenuItem { Header = Loc.Get("Menu_HistoryEmpty"), IsEnabled = false });
    }
}
