using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Tandem.App.Input;
using Tandem.App.Resources;
using Tandem.App.ViewModels;
using Tandem.App.Views.Dialogs;
using AppWindowState = Tandem.Core.Settings.WindowState;

namespace Tandem.App.Views;

/// <summary>
/// The main window. Keys go to <see cref="MainViewModel.HandleKeyAsync"/> before any control sees them (so the file
/// list never moves its own selection), unless a text editor has the focus and the chord belongs to it
/// (<see cref="KeyRouting"/>); text typed outside editors is quick search. The placement and the split between the
/// panels are restored from the workspace and saved back on close.
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel? _main;
    private readonly IReadOnlyList<PixelRect>? _screens;
    private KeyModifiers _modifiers;
    private (PixelPoint Position, double Width, double Height)? _normalBounds;
    private bool _placementApplied;
    private bool _closeConfirmed;
    private bool _confirmingClose;
    private ProgressWindow? _progress;
    private double _outputHeight = DefaultOutputHeight;

    /// <summary>The output pane's height when it first opens.</summary>
    public const double DefaultOutputHeight = 180;

    /// <summary>The panels keep at least this height when the output pane is resized.</summary>
    private const double MinPanelsHeight = 120;

    /// <summary>For the designer only.</summary>
    public MainWindow()
    {
        InitializeComponent();
    }

    /// <param name="main">A loaded main view model.</param>
    /// <param name="screens">The screens' working areas in pixels, to check the saved placement against; the
    /// system's screens when null.</param>
    public MainWindow(MainViewModel main, IReadOnlyList<PixelRect>? screens = null) : this()
    {
        ArgumentNullException.ThrowIfNull(main);
        _main = main;
        _screens = screens;
        DataContext = main;
        App.ApplyTheme(main.Theme);
        NativeMenu.SetMenu(this, MainMenu.Build(main));
        ApplyPlacement();

        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyUpEvent, (_, e) => _modifiers = e.KeyModifiers, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(TextInputEvent, OnTextInput, RoutingStrategies.Bubble);
        CommandLineView.ReturnFocusRequested += OnCommandLineReturnFocusRequested;
        CommandLineView.Failed += OnCommandLineFailed;
        WorkArea.RowDefinitions[0].MinHeight = MinPanelsHeight;
        foreach (var panel in new[] { LeftPanel, RightPanel })
            panel.List.PageSizeChanged += (_, rows) => main.PageSize = rows;

        main.PropertyChanged += OnMainPropertyChanged;
        main.CommandLineFocusRequested += OnCommandLineFocusRequested;
        main.FavoritesMenuRequested += OnFavoritesMenuRequested;
        main.NoticeRequested += OnNoticeRequested;
        main.Shell.OpenWithMenuRequested += OnOpenWithMenuRequested;
        main.Operations.PropertyChanged += OnOperationsPropertyChanged;
        main.CommandLine.PropertyChanged += OnCommandLinePropertyChanged;
        PositionChanged += (_, _) => RecordPlacement();
        SizeChanged += (_, _) => RecordPlacement();
        Splitter.DragCompleted += (_, _) => RecordPlacement();
        Opened += (_, _) =>
        {
            _placementApplied = true;
            FocusActivePanel();
        };
        Closing += OnClosing;
        Closed += (_, _) => Detach();
    }

    /// <summary>Closing the window, by the user or by quitting the app (Cmd+Q, the Dock, the system: the app's
    /// shutdown closes this window first). While file operations run or an editor window is open, the close is held
    /// back and <see cref="MainViewModel.ConfirmCloseAsync"/> closes the editors (asking about unsaved changes) and asks
    /// about the operations; once both are settled the window closes again for real; a "keep" anywhere keeps it. Then the placement and the workspace are saved.</summary>
    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_main is not { } main) return;
        if (!_closeConfirmed && (main.HasOperations || main.HasOpenWindows))
        {
            e.Cancel = true;
            if (!_confirmingClose) _ = ConfirmCloseAsync(main);
            return;
        }
        RecordPlacement();
        main.Workspace.Save();
        main.OnWindowClosing();
    }

    private async Task ConfirmCloseAsync(MainViewModel main)
    {
        _confirmingClose = true;
        try
        {
            if (!await main.ConfirmCloseAsync()) return;
            _closeConfirmed = true;
            Close();
        }
        catch (Exception failure)
        {
            main.ReportFailure(failure);
        }
        finally
        {
            _confirmingClose = false;
        }
    }

    /// <summary>The left panel's share of the panels' width.</summary>
    public double SplitRatio
    {
        get
        {
            var (left, right) = (Panels.ColumnDefinitions[0], Panels.ColumnDefinitions[2]);
            var (leftWidth, rightWidth) = left.ActualWidth + right.ActualWidth > 0
                ? (left.ActualWidth, right.ActualWidth)
                : (left.Width.Value, right.Width.Value);
            return leftWidth + rightWidth > 0 ? leftWidth / (leftWidth + rightWidth) : WindowPlacement.DefaultSplitRatio;
        }
    }

    private void ApplyPlacement()
    {
        var screens = _screens ?? Screens.All.Select(s => s.WorkingArea).ToList();
        var placement = WindowPlacement.Restore(_main!.Workspace.Window, screens, Screens.Primary?.Scaling ?? 1);
        Width = placement.Width;
        Height = placement.Height;
        if (placement.Position is { } position)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = position;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        if (placement.Maximized) WindowState = Avalonia.Controls.WindowState.Maximized;
        Panels.ColumnDefinitions[0].Width = new GridLength(placement.SplitRatio, GridUnitType.Star);
        Panels.ColumnDefinitions[2].Width = new GridLength(1 - placement.SplitRatio, GridUnitType.Star);
    }

    /// <summary>Puts the current placement into the workspace (saved with it); while maximized, the bounds from
    /// before maximizing are kept.</summary>
    private void RecordPlacement()
    {
        if (_main is null || !_placementApplied) return;
        if (WindowState == Avalonia.Controls.WindowState.Normal) _normalBounds = (Position, Width, Height);
        if (_normalBounds is not { } bounds) return;
        _main.Workspace.Window = new AppWindowState(bounds.Position.X, bounds.Position.Y, bounds.Width, bounds.Height,
            WindowState == Avalonia.Controls.WindowState.Maximized, SplitRatio);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        _modifiers = e.KeyModifiers;
        if (_main is null || e.Handled) return;
        var chord = KeyRouting.ChordFor(e.Key, e.PhysicalKey, e.KeyModifiers);
        if (KeyRouting.IsTextEditor(FocusManager?.GetFocusedElement()) && !KeyRouting.ShouldRouteWhileEditing(chord))
            return;
        var handling = _main.HandleKeyAsync(chord);
        // A command still running was found and started: the key is used.
        if (!handling.IsCompleted || handling.Result) e.Handled = true;
    }

    private void OnTextInput(object? sender, TextInputEventArgs e)
    {
        if (_main is null || e.Text is not { Length: > 0 } text) return;
        if (!KeyRouting.AcceptsQuickSearch(_modifiers) || KeyRouting.IsTextEditor(FocusManager?.GetFocusedElement()))
            return;
        if (_main.HandleTextInput(text)) e.Handled = true;
    }

    private void OnCommandLineReturnFocusRequested(object? sender, EventArgs e) => FocusActivePanel();

    private void OnCommandLineFailed(object? sender, Exception failure) => _main?.ReportFailure(failure);

    private void OnCommandLinePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CommandLineViewModel.IsOutputVisible)) UpdateOutputPane();
    }

    /// <summary>Opens or closes the output pane above the command line; its height (as the splitter left it) is kept
    /// for the next time it opens.</summary>
    private void UpdateOutputPane()
    {
        if (_main is null) return;
        var visible = _main.CommandLine.IsOutputVisible;
        var row = WorkArea.RowDefinitions[2];
        if (visible == OutputPane.IsVisible) return;
        if (!visible && OutputPane.Bounds.Height > 0) _outputHeight = OutputPane.Bounds.Height;
        OutputPane.IsVisible = visible;
        OutputSplitter.IsVisible = visible;
        row.Height = new GridLength(visible ? _outputHeight : 0);
    }

    private void OnMainPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ActiveSide) && !KeyRouting.IsTextEditor(FocusManager?.GetFocusedElement()))
            FocusActivePanel();
        else if (e.PropertyName == nameof(MainViewModel.Theme) && _main is { } themed)
            App.ApplyTheme(themed.Theme);
        // The menu shows each command's first chord: its items take the new key map's.
        else if (e.PropertyName == nameof(MainViewModel.KeyMap) && _main is { } main &&
                 NativeMenu.GetMenu(this) is { } menu)
            MainMenu.ApplyShortcuts(menu, main.KeyMap);
    }

    private void FocusActivePanel()
    {
        if (_main is null) return;
        (_main.Workspace.ActiveSide == PanelSide.Left ? LeftPanel : RightPanel).FocusList();
    }

    /// <summary>Cmd+L: the field takes the focus; nothing happens while the command line is hidden or disabled.</summary>
    private void OnCommandLineFocusRequested(object? sender, EventArgs e) => CommandLineView.FocusField();

    private void OnFavoritesMenuRequested(object? sender, EventArgs e)
    {
        if (_main is not { } main) return;
        var flyout = new MenuFlyout();
        foreach (var path in main.Workspace.Favorites.Items)
            flyout.Items.Add(new MenuItem
            {
                Header = LiteralLabel.For(path), Command = main.OpenFavoriteCommand, CommandParameter = path,
            });
        if (flyout.Items.Count == 0)
            flyout.Items.Add(new MenuItem { Header = Loc.Get("Menu_FavoritesEmpty"), IsEnabled = false });
        flyout.Items.Add(new Separator());
        var add = new MenuItem { Header = Loc.Get("Menu_FavoritesAdd"), IsEnabled = !main.IsCurrentFavorite };
        add.Click += (_, _) => main.AddCurrentToFavorites();
        var remove = new MenuItem { Header = Loc.Get("Menu_FavoritesRemove"), IsEnabled = main.IsCurrentFavorite };
        remove.Click += (_, _) => main.RemoveCurrentFromFavorites();
        flyout.Items.Add(add);
        flyout.Items.Add(remove);
        FavoritesMenu = flyout;
        flyout.ShowAt((main.Workspace.ActiveSide == PanelSide.Left ? LeftPanel : RightPanel).PathRowControl);
    }

    /// <summary>"Open With" from the main menu: the applications for the cursor item, at its row, once the query
    /// (off the UI thread) has answered.</summary>
    private async void OnOpenWithMenuRequested(object? sender, EventArgs e)
    {
        if (_main is not { } main) return;
        try
        {
            // Null: the cursor moved to another item meanwhile; its apps are not shown at the new row.
            if (await ContextMenuModel.OpenWithItemsAsync(main) is not { } items || !IsVisible) return;
            (main.Workspace.ActiveSide == PanelSide.Left ? LeftPanel : RightPanel).List.ShowMenuAtCursor(items);
        }
        catch (Exception failure)
        {
            main.ReportFailure(failure);
        }
    }

    /// <summary>The progress window: shown while the operations model says so, modeless and owned by this window.
    /// The user closing it only hides it; the operations go on.</summary>
    private void OnOperationsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(OperationsViewModel.IsWindowVisible) || _main is not { } main) return;
        if (main.Operations.IsWindowVisible && _progress is null && IsVisible)
        {
            var progress = new ProgressWindow { DataContext = main.Operations };
            progress.Closed += (_, _) =>
            {
                if (_progress == progress) _progress = null;
                main.Operations.IsWindowVisible = false;
            };
            _progress = progress;
            progress.Show(this);
        }
        else if (!main.Operations.IsWindowVisible && _progress is { } shown)
        {
            _progress = null;
            shown.Close();
        }
    }

    /// <summary>The favourites menu last opened; for tests.</summary>
    public MenuFlyout? FavoritesMenu { get; private set; }

    /// <summary>The progress window while it is shown; for tests.</summary>
    public ProgressWindow? Progress => _progress;

    private void OnNoticeRequested(object? sender, string text) =>
        _ = MessageDialog.ShowAsync(this, Title ?? "", text);

    private void Detach()
    {
        if (_main is not { } main) return;
        main.PropertyChanged -= OnMainPropertyChanged;
        main.CommandLineFocusRequested -= OnCommandLineFocusRequested;
        main.FavoritesMenuRequested -= OnFavoritesMenuRequested;
        main.NoticeRequested -= OnNoticeRequested;
        main.Shell.OpenWithMenuRequested -= OnOpenWithMenuRequested;
        main.Operations.PropertyChanged -= OnOperationsPropertyChanged;
        main.CommandLine.PropertyChanged -= OnCommandLinePropertyChanged;
        CommandLineView.ReturnFocusRequested -= OnCommandLineReturnFocusRequested;
        CommandLineView.Failed -= OnCommandLineFailed;
    }
}
