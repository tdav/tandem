using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Tandem.Core.Settings;
using Tandem.Core.Vfs;

namespace Tandem.App.ViewModels;

public enum PanelSide { Left, Right }

/// <summary>
/// The two sides of the window with their tabs, which side is active, the favourites and the view options, kept
/// across restarts through a <see cref="JsonStateStore"/>.
/// </summary>
/// <remarks>
/// Created and called on the UI thread. <see cref="LoadAsync()"/> must complete before the sides or panels are used.
/// After a change to the tabs, folders, sorting, favourites, view options, the language or the active side, the
/// state is saved <see cref="AutosaveDelay"/> later; changes in between are saved together, so saves happen at most
/// once per delay. <see cref="Dispose"/> drops a pending save: the owner calls <see cref="Save"/> when the window closes.
/// </remarks>
public sealed partial class WorkspaceViewModel : ObservableObject, IDisposable
{
    public static readonly TimeSpan AutosaveDelay = TimeSpan.FromSeconds(2);

    private readonly JsonStateStore _store;
    private readonly string _homeDirectory;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    private PanelSide _activeSide;
    private ViewState _view = new();
    private bool _showHidden;
    private string? _language;
    private bool _loaded;
    private bool _restoring;
    private CancellationTokenSource? _autosaveCts;
    private bool _disposed;

    /// <param name="delay">Waits before an automatic save; <see cref="Task.Delay(TimeSpan, CancellationToken)"/>
    /// when null. Tests pass one they complete themselves.</param>
    public WorkspaceViewModel(ProviderRegistry providers, JsonStateStore store, string homeDirectory,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrEmpty(homeDirectory);
        _store = store;
        _homeDirectory = homeDirectory;
        _delay = delay ?? Task.Delay;

        Left = new PanelTabsViewModel(providers);
        Right = new PanelTabsViewModel(providers);
        Left.StateChanged += OnSideStateChanged;
        Right.StateChanged += OnSideStateChanged;
        Left.PropertyChanged += OnSidePropertyChanged;
        Right.PropertyChanged += OnSidePropertyChanged;
        Favorites.Changed += (_, _) => ScheduleSave();
    }

    /// <summary>The user's home folder, where tabs fall back to and where "~" points.</summary>
    public string HomeDirectory => _homeDirectory;

    public PanelTabsViewModel Left { get; }

    public PanelTabsViewModel Right { get; }

    public PanelSide ActiveSide
    {
        get => _activeSide;
        set
        {
            if (!SetProperty(ref _activeSide, value)) return;
            RaisePanelsChanged();
            ScheduleSave();
        }
    }

    /// <summary>The panel of the active tab of the active side.</summary>
    public PanelViewModel ActivePanel => Side(_activeSide).Active.Panel;

    /// <summary>The panel of the active tab of the other side.</summary>
    public PanelViewModel PassivePanel => Side(Other(_activeSide)).Active.Panel;

    public FavoritesViewModel Favorites { get; } = new();

    public ViewState View
    {
        get => _view;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (SetProperty(ref _view, value)) ScheduleSave();
        }
    }

    /// <summary>Whether hidden entries are listed, in every panel of both sides, tabs opened later included.</summary>
    public bool ShowHidden
    {
        get => _showHidden;
        set
        {
            if (!SetProperty(ref _showHidden, value)) return;
            Left.ShowHidden = value;
            Right.ShowHidden = value;
            ScheduleSave();
        }
    }

    /// <summary>The window placement to save: the loaded one until the window sets its own.</summary>
    public WindowState? Window { get; set; }

    /// <summary>The interface language to use from the next start: "en", "ru", or null for the system language.
    /// Changing it does not change the running app's language.</summary>
    public string? Language
    {
        get => _language;
        set
        {
            if (SetProperty(ref _language, value)) ScheduleSave();
        }
    }

    /// <summary>Why the last save failed; null after a successful one.</summary>
    [ObservableProperty]
    public partial IOException? LastSaveError { get; private set; }

    public void SwitchPanel() => ActiveSide = Other(_activeSide);

    /// <summary>Exchanges the tabs of the two sides, each tab with its panel and history; the same side of the
    /// screen stays active.</summary>
    /// <remarks>Atomic for observers: no notification is raised until both sides hold their final tabs, so
    /// <see cref="ActivePanel"/> and <see cref="PassivePanel"/> are never seen as the same panel.</remarks>
    public Task SwapPanelsAsync()
    {
        PanelTabsViewModel.SwapContents(Left, Right);
        RaisePanelsChanged();
        return Task.CompletedTask;
    }

    /// <summary>Opens the active panel's folder in the other side's active tab.</summary>
    public Task MirrorToOtherAsync()
    {
        var path = ActivePanel.CurrentPath;
        return path == default ? Task.CompletedTask : PassivePanel.NavigateToAsync(path);
    }

    /// <summary>Reads the saved state (the default one on first run or when it is unreadable) and opens its tabs on
    /// both sides; folders that cannot be opened fall back as <see cref="PanelTabsViewModel.RestoreAsync"/> says,
    /// at worst to the home folder.</summary>
    public async Task LoadAsync()
    {
        var store = _store;
        var home = _homeDirectory;
        var state = await Task.Run(() => store.Load(home));
        await LoadAsync(state);
    }

    /// <summary>Opens <paramref name="state"/>, already read from the store (at start-up it is read once, to choose
    /// the language before anything else); otherwise as <see cref="LoadAsync()"/>.</summary>
    public async Task LoadAsync(AppState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (_disposed) return;

        _restoring = true;
        try
        {
            CancelAutosave();
            Favorites.Load(state.Favorites);
            View = state.View;
            ShowHidden = state.ShowHidden;
            ActiveSide = state.LeftIsActive ? PanelSide.Left : PanelSide.Right;
            Window ??= state.Window; // the window may already have set a newer one
            Language = state.Language;

            var fallback = VfsPath.Local(_homeDirectory);
            await Task.WhenAll(Left.RestoreAsync(state.Left, fallback), Right.RestoreAsync(state.Right, fallback));
            if (_disposed) return;
            _loaded = true;
            RaisePanelsChanged();
        }
        finally
        {
            _restoring = false;
        }
    }

    /// <summary>Writes the current state now, replacing a pending automatic save. Does nothing before
    /// <see cref="LoadAsync()"/> has completed. Never throws for a failed write: it ends up in
    /// <see cref="LastSaveError"/>.</summary>
    public void Save()
    {
        CancelAutosave();
        if (!_loaded || _disposed) return;
        var state = new AppState(AppState.CurrentVersion, Left.ToState(), Right.ToState(),
            LeftIsActive: _activeSide == PanelSide.Left, _view, Window, Favorites.Items.ToList(), _showHidden,
            _language);
        try
        {
            _store.Save(state);
            LastSaveError = null;
        }
        catch (IOException e)
        {
            LastSaveError = e;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelAutosave();
        Left.Dispose();
        Right.Dispose();
    }

    private PanelTabsViewModel Side(PanelSide side) => side == PanelSide.Left ? Left : Right;

    private static PanelSide Other(PanelSide side) => side == PanelSide.Left ? PanelSide.Right : PanelSide.Left;

    private void RaisePanelsChanged()
    {
        if (!_loaded) return; // the panels exist only once both sides have been restored
        OnPropertyChanged(nameof(ActivePanel));
        OnPropertyChanged(nameof(PassivePanel));
    }

    private void OnSidePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PanelTabsViewModel.Active)) RaisePanelsChanged();
    }

    private void OnSideStateChanged(object? sender, EventArgs e) => ScheduleSave();

    private void ScheduleSave()
    {
        if (!_loaded || _restoring || _disposed || _autosaveCts is not null) return;
        var cts = new CancellationTokenSource();
        _autosaveCts = cts;
        _ = AutosaveAsync(cts);
    }

    private async Task AutosaveAsync(CancellationTokenSource cts)
    {
        var waited = false;
        var current = false;
        try
        {
            await _delay(AutosaveDelay, cts.Token);
            waited = true;
        }
        catch (Exception)
        {
            // Cancelled, or the delay itself failed: this save is dropped, the next change schedules another.
        }
        finally
        {
            // Release the pending slot whatever happened, so a failed delay never stops later autosaves.
            current = _autosaveCts == cts;
            if (current)
            {
                _autosaveCts = null;
                cts.Dispose();
            }
        }
        if (waited && current) Save();
    }

    private void CancelAutosave()
    {
        var cts = _autosaveCts;
        _autosaveCts = null;
        cts?.Cancel();
        cts?.Dispose();
    }
}
