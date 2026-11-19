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
    private ProgressWindow? _progress;

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
        NativeMenu.SetMenu(this, MainMenu.Build(main));
        ApplyPlacement();

        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyUpEvent, (_, e) => _modifiers = e.KeyModifiers, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(TextInputEvent, OnTextInput, RoutingStrategies.Bubble);
        CommandLineBox.AddHandler(KeyDownEvent, OnCommandLineKeyDown, RoutingStrategies.Tunnel);
        foreach (var panel in new[] { LeftPanel, RightPanel })
            panel.List.PageSizeChanged += (_, rows) => main.PageSize = rows;

        main.PropertyChanged += OnMainPropertyChanged;
        main.CommandLineFocusRequested += OnCommandLineFocusRequested;
        main.FavoritesMenuRequested += OnFavoritesMenuRequested;
        main.NoticeRequested += OnNoticeRequested;
        main.Shell.OpenWithMenuRequested += OnOpenWithMenuRequested;
        main.Operations.PropertyChanged += OnOperationsPropertyChanged;
        PositionChanged += (_, _) => RecordPlacement();
        SizeChanged += (_, _) => RecordPlacement();
        Splitter.DragCompleted += (_, _) => RecordPlacement();
        Opened += (_, _) =>
        {
            _placementApplied = true;
            FocusActivePanel();
        };
        Closing += (_, _) =>
        {
            RecordPlacement();
            main.Workspace.Save();
            main.OnWindowClosing();
        };
        Closed += (_, _) => Detach();
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

    private void OnCommandLineKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
        {
            e.Handled = true;
            FocusActivePanel();
        }
    }

    private void OnMainPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ActiveSide) && !KeyRouting.IsTextEditor(FocusManager?.GetFocusedElement()))
            FocusActivePanel();
    }

    private void FocusActivePanel()
    {
        if (_main is null) return;
        (_main.Workspace.ActiveSide == PanelSide.Left ? LeftPanel : RightPanel).FocusList();
    }

    private void OnCommandLineFocusRequested(object? sender, EventArgs e) => CommandLineBox.Focus();

    private void OnFavoritesMenuRequested(object? sender, EventArgs e)
    {
        if (_main is not { } main) return;
        var flyout = new MenuFlyout();
        foreach (var path in main.Workspace.Favorites.Items)
            flyout.Items.Add(new MenuItem { Header = path, Command = main.OpenFavoriteCommand, CommandParameter = path });
        if (flyout.Items.Count == 0)
            flyout.Items.Add(new MenuItem { Header = Loc.Get("Menu_FavoritesEmpty"), IsEnabled = false });
        flyout.Items.Add(new Separator());
        var add = new MenuItem { Header = Loc.Get("Menu_FavoritesAdd"), IsEnabled = !main.IsCurrentFavorite };
        add.Click += (_, _) => main.AddCurrentToFavorites();
        var remove = new MenuItem { Header = Loc.Get("Menu_FavoritesRemove"), IsEnabled = main.IsCurrentFavorite };
        remove.Click += (_, _) => main.RemoveCurrentFromFavorites();
        flyout.Items.Add(add);
        flyout.Items.Add(remove);
        flyout.ShowAt((main.Workspace.ActiveSide == PanelSide.Left ? LeftPanel : RightPanel).PathRowControl);
    }

    /// <summary>"Open With" from the main menu: the applications for the cursor item, at its row.</summary>
    private void OnOpenWithMenuRequested(object? sender, EventArgs e)
    {
        if (_main is not { } main) return;
        (main.Workspace.ActiveSide == PanelSide.Left ? LeftPanel : RightPanel).List
            .ShowMenuAtCursor(ContextMenuModel.OpenWithItems(main));
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
    }
}
