using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tandem.App.Resources;
using Tandem.App.Services;
using Tandem.App.ViewModels.Net;
using Tandem.App.ViewModels.Viewing;
using Tandem.Core.Listing;
using Tandem.Core.Platform;
using Tandem.Core.Settings;
using Tandem.Core.Vfs;
using Tandem.Core.Vfs.Local;

namespace Tandem.App.ViewModels;

/// <summary>
/// What one side of the window shows around its active panel: the tabs, the volume bar, the path row (and its
/// editor), the column headers and the status row.
/// </summary>
/// <remarks>Created by <see cref="MainViewModel"/> before the workspace is loaded; <see cref="Panel"/> is null until
/// the side has its tabs. UI thread only.</remarks>
public sealed partial class SideViewModel : ObservableObject
{
    private const string Ascending = "▲";
    private const string Descending = "▼";

    private readonly MainViewModel _main;
    private PanelViewModel? _panel;
    private ListMode _listMode;

    internal SideViewModel(MainViewModel main, PanelSide side, PanelTabsViewModel tabs)
    {
        _main = main;
        Side = side;
        Tabs = tabs;
        CloseTabCommand = new AsyncRelayCommand<TabViewModel>(async tab =>
        {
            if (tab is null) return;
            Activate();
            try { await Tabs.CloseAsync(tab); }
            catch (Exception error) { _main.ReportFailure(error); }
        });
        ReconnectCommand = new AsyncRelayCommand(ReconnectAsync);
        tabs.PropertyChanged += OnTabsPropertyChanged;
    }

    public PanelSide Side { get; }

    public PanelTabsViewModel Tabs { get; }

    /// <summary>The panel of the active tab; null before the side has tabs.</summary>
    public PanelViewModel? Panel => _panel;

    /// <summary>The active tab, for the tab strip; setting it (a click on a tab) activates that tab and this side.</summary>
    public TabViewModel? SelectedTab
    {
        get => _panel is null ? null : Tabs.Active;
        set
        {
            // The strip pushes null while its items are replaced; only a real choice of another tab counts.
            if (value is null || _panel is null || !Tabs.Tabs.Contains(value) || value == Tabs.Active) return;
            Activate();
            Tabs.Activate(value);
        }
    }

    public TerminalViewModel? Terminal => SelectedTab?.Terminal;

    public bool ShowsTerminal => Terminal is not null;

    public bool ShowsVolumeBarForFiles => ShowVolumeBar && !ShowsTerminal;

    public bool IsActive => _main.Workspace.ActiveSide == Side;

    /// <summary>The window's quick-view panel.</summary>
    public QuickViewViewModel QuickView => _main.QuickView;

    /// <summary>Whether the quick-view panel shows in place of this side's file list.</summary>
    public bool ShowsQuickView => _main.QuickView.IsOpen && _main.QuickView.Side == Side;

    /// <summary>The quick-view panel's viewer while this side shows it; null otherwise, so only the side that shows
    /// the panel lays out its pages.</summary>
    public ViewerViewModel? QuickViewer => ShowsQuickView ? _main.QuickView.Viewer : null;

    public bool ShowVolumeBar => _main.Workspace.View.VolumeBars;

    /// <summary>How this side lists its folder (Ctrl+F1 brief, Ctrl+F2 full), kept in the workspace's view state.
    /// Every tab of the side shows the same way.</summary>
    public ListMode ListMode
    {
        get
        {
            var view = _main.Workspace.View;
            return Side == PanelSide.Left ? view.LeftList : view.RightList;
        }
        set
        {
            var view = _main.Workspace.View;
            _main.Workspace.View = Side == PanelSide.Left ? view with { LeftList = value } : view with { RightList = value };
        }
    }

    /// <summary>The brief view: names only, in columns filled top to bottom.</summary>
    public bool IsBrief => ListMode == ListMode.Brief;

    /// <summary>The rows of one column of the brief view, as the list last laid them out: Left and Right move the
    /// cursor by this many rows. 1 until the list has been shown.</summary>
    public int BriefRows { get; private set; } = 1;

    /// <summary>The rows of the brief view's columns shown at once, for PageUp and PageDown.</summary>
    public int BriefPageSize { get; private set; } = 1;

    /// <summary>The list tells how the brief view is laid out: the rows of a column and the columns shown whole.</summary>
    public void ReportBriefLayout(int rowsPerColumn, int visibleColumns)
    {
        BriefRows = Math.Max(1, rowsPerColumn);
        BriefPageSize = BriefRows * Math.Max(1, visibleColumns);
    }

    /// <summary>The file list's font, row height and columns: the settings' font and row height, and this side's own
    /// columns (<see cref="AppSettings.LeftColumns"/> or <c>RightColumns</c>).</summary>
    public ListLayoutViewModel Layout { get; } = new();

    /// <summary>The window's colour rules for file rows; PropertyChanged is raised when they are replaced.</summary>
    public FileColors FileColors => _main.FileColors;

    public IReadOnlyList<VolumeInfo> Volumes => _main.Volumes;

    /// <summary>Free and total space of the volume holding the folder shown; empty when it is not known.</summary>
    public string VolumeText
    {
        get
        {
            if (_panel is not { CurrentPath: var path } || path == default ||
                path.ProviderId != VfsPath.LocalProviderId) return "";
            var volume = _main.Volumes
                .Where(v => IsOnVolume(path.Path, VfsPath.Local(v.MountPoint).Path))
                .MaxBy(v => v.MountPoint.Length);
            return volume is null
                ? ""
                : Loc.Format("View_VolumeFree", SizeFormatter.Format(volume.FreeBytes),
                    SizeFormatter.Format(volume.TotalBytes));
        }
    }

    /// <summary>Whether the path row is a text field (<see cref="BeginPathEdit"/>).</summary>
    [ObservableProperty]
    public partial bool IsEditingPath { get; private set; }

    /// <summary>The text of the path editor.</summary>
    [ObservableProperty]
    public partial string PathText { get; set; } = "";

    /// <summary>A message of this side's own (a path that cannot be read); it shows instead of the status.</summary>
    [ObservableProperty]
    public partial string? Message { get; private set; }

    /// <summary>The status row: this side's message, else the panel's error, else its notice, else its selection
    /// status.</summary>
    public string StatusLine => Message ?? _panel?.Error ?? _panel?.Notice ?? _panel?.StatusText ?? "";

    public bool HasError => Message is not null || _panel?.Error is not null;

    /// <summary>The panel on the local disk cannot show a folder for lack of permission: the status row offers the
    /// system's access settings.</summary>
    public bool IsAccessDenied =>
        Message is null && _panel?.ErrorKind == PanelErrorKind.AccessDenied &&
        (_panel.CurrentPath == default || _panel.CurrentPath.ProviderId == VfsPath.LocalProviderId);

    /// <summary>The panel's source lost its connection: the status row offers <see cref="ReconnectCommand"/>.</summary>
    public bool IsConnectionLost => Message is null && _panel?.ErrorKind == PanelErrorKind.ConnectionLost;

    /// <summary>The reconnect button: reads the folder again, which makes the source connect again (as Cmd+R). When
    /// the connection was lost on the way to another folder, it goes on to that folder instead.</summary>
    public IRelayCommand ReconnectCommand { get; }

    private Task ReconnectAsync()
    {
        if (_panel is not { } panel) return Task.CompletedTask;
        return panel.ErrorKind == PanelErrorKind.ConnectionLost && panel.ErrorPath is { } target &&
               target != panel.CurrentPath
            ? panel.NavigateToAsync(target)
            : panel.RefreshAsync();
    }

    public string NameSortMark => SortMark(SortColumn.Name);
    public string ExtensionSortMark => SortMark(SortColumn.Extension);
    public string SizeSortMark => SortMark(SortColumn.Size);
    public string DateSortMark => SortMark(SortColumn.Modified);
    public string ModeSortMark => SortMark(SortColumn.Mode);

    /// <summary>The folders of the active tab's history, latest first, for the back/forward menu.</summary>
    public IReadOnlyList<VfsPath> HistoryEntries => _panel is null ? [] : Tabs.Active.History.Recent;

    public void Activate() => _main.Workspace.ActiveSide = Side;

    /// <summary>How <paramref name="path"/> reads to the user (a history entry): see
    /// <see cref="PanelViewModel.DisplayPath"/>.</summary>
    public string DisplayPathOf(VfsPath path) => _panel?.DisplayPathOf(path) ?? path.Path;

    /// <summary>Turns the path row into an editor holding the current path as the user reads it
    /// (<see cref="PanelViewModel.DisplayPath"/>).</summary>
    public void BeginPathEdit()
    {
        if (_panel is null) return;
        Activate();
        PathText = _panel.DisplayPath;
        Message = null;
        IsEditingPath = true;
    }

    /// <summary>Enter in the path editor: goes to the typed folder ("~" is the home folder) and closes the editor. The
    /// path is in the panel's provider, except inside a hosted one (an archive), where it is on the local disk.
    /// If the folder cannot be shown, the reason is in the status row and the editor stays open. True on success.
    /// An unexpected failure is reported, not thrown, and leaves the editor open.</summary>
    public async Task<bool> CommitPathEditAsync()
    {
        try
        {
            return await CommitPathAsync();
        }
        catch (Exception e)
        {
            _main.ReportFailure(e);
            return false;
        }
    }

    private async Task<bool> CommitPathAsync()
    {
        if (_panel is not { } panel) return false;
        var text = PathText.Trim();
        var current = panel.CurrentPath;
        var providerId = current == default || IsNested(current) ? VfsPath.LocalProviderId : current.ProviderId;
        if (ParsePath(text, providerId, _main.Workspace.HomeDirectory) is not { } target)
        {
            Message = Loc.Format("Path_Invalid", text);
            return false;
        }
        Message = null;
        await panel.NavigateToAsync(target);
        if (panel != _panel || panel.CurrentPath != target || panel.Error is not null) return false;
        IsEditingPath = false;
        return true;
    }

    /// <summary>Whether <paramref name="path"/> is inside a provider that lives in another (it has a host location),
    /// or in one no longer registered.</summary>
    private bool IsNested(VfsPath path)
    {
        try
        {
            return _main.Services.Providers.HostLocationOf(path) is not null;
        }
        catch (KeyNotFoundException)
        {
            return true;
        }
    }

    /// <summary>Esc in the path editor: closes it without moving.</summary>
    public void CancelPathEdit()
    {
        Message = null;
        IsEditingPath = false;
    }

    /// <summary>A click on a row: activates this side and puts the cursor there; with <paramref name="toggleMark"/>
    /// (Cmd held) also toggles the row's mark.</summary>
    public void ClickRow(int index, bool toggleMark)
    {
        Activate();
        if (_panel is not { } panel || index < 0 || index >= panel.Rows.Count) return;
        if (toggleMark) panel.Rows[index].IsSelected = !panel.Rows[index].IsSelected;
        panel.CursorIndex = index;
    }

    /// <summary>A right click on a row: puts the cursor there and activates this side (as a click does), so the menu
    /// acts on the row under the pointer. Empty when there is no such row.</summary>
    public IReadOnlyList<ContextMenuItem> ContextMenuForRow(int index)
    {
        ClickRow(index, toggleMark: false);
        return _panel is { } panel && index >= 0 && index < panel.Rows.Count ? ContextMenuModel.ForRow(_main) : [];
    }

    /// <summary>A right click below the rows: activates this side and leaves the cursor where it is.</summary>
    public IReadOnlyList<ContextMenuItem> ContextMenuForEmptySpace()
    {
        Activate();
        return _panel is null ? [] : ContextMenuModel.ForEmptySpace(_main);
    }

    /// <summary>A right click on a tab: activates this side and that tab (as a click does), so the menu acts on the
    /// tab under the pointer. Empty when the tab is not one of this side's.</summary>
    public IReadOnlyList<ContextMenuItem> ContextMenuForTab(TabViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (_panel is null || !Tabs.Tabs.Contains(tab)) return [];
        Activate();
        Tabs.Activate(tab);
        return ContextMenuModel.ForTab(_main);
    }

    /// <summary>The tabs' close buttons: <see cref="CloseTab"/> with the button's tab.</summary>
    public IAsyncRelayCommand<TabViewModel> CloseTabCommand { get; }

    /// <summary>A tab's close button: activates this side and closes that tab, unless
    /// <see cref="PanelTabsViewModel.CanClose"/> refuses (a pinned tab, the only one). True when it was closed.</summary>
    public bool CloseTab(TabViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        Activate();
        return Tabs.Close(tab);
    }

    /// <summary>What a drop at row <paramref name="rowIndex"/> (or below the rows, -1) would go into, when the
    /// dragged <paramref name="localPaths"/> may be dropped there; null otherwise.</summary>
    public DropTarget? DropTargetFor(IReadOnlyList<string> localPaths, int rowIndex) =>
        _panel is { } panel && DropHandler.TargetAt(panel, rowIndex) is { } target &&
        _main.Drops.CanDrop(localPaths, Side, target.Directory)
            ? target
            : null;

    /// <summary>Files dropped on this side into <paramref name="target"/>: the dialog offering copy and move (Move
    /// pre-selected with <paramref name="preferMove"/>), then the operation chosen; this side becomes active once the
    /// user accepts. An unexpected failure is reported, not thrown.</summary>
    public async Task DropAsync(IReadOnlyList<string> localPaths, DropTarget target, bool preferMove)
    {
        try
        {
            await _main.Drops.DropAsync(localPaths, Side, target.Directory, preferMove);
        }
        catch (Exception e)
        {
            _main.ReportFailure(e);
        }
    }

    /// <summary>The items a drag starting on row <paramref name="rowIndex"/> carries (see
    /// <see cref="DropHandler.DragItems"/>).</summary>
    public IReadOnlyList<FileEntry> DragItems(int rowIndex) =>
        _panel is { } panel ? DropHandler.DragItems(panel, rowIndex) : [];

    /// <summary>An unexpected failure in this side's view (e.g. starting a drag): logged and told, not thrown.</summary>
    internal void ReportFailure(Exception error) => _main.ReportFailure(error);

    /// <summary>The access-denied button: opens the system's Full Disk Access settings.</summary>
    [RelayCommand]
    private void OpenAccessSettings() => _main.Shell.OpenAccessSettings();

    /// <summary>A double click on a row: as Enter on it. An unexpected failure is reported, not thrown.</summary>
    public async Task OpenRowAsync(int index)
    {
        ClickRow(index, toggleMark: false);
        if (_panel is not { } panel) return;
        try
        {
            await panel.ActivateAsync();
        }
        catch (Exception e)
        {
            _main.ReportFailure(e);
        }
    }

    /// <summary>A typed path, or null when it is not one: absolute, or starting with "~" for the home folder. A
    /// local path may be typed in the running system's own form (on Windows <c>C:\x</c>, <c>~\x</c>).</summary>
    internal static VfsPath? ParsePath(string text, string providerId, string homeDirectory)
    {
        if (providerId == VfsPath.LocalProviderId)
        {
            if (LocalPath.ExpandTyped(text, homeDirectory, LocalPath.Current) is not { } absolute) return null;
            text = absolute;
        }
        else if (!text.StartsWith('/')) return null;
        try
        {
            return new VfsPath(providerId, text);
        }
        catch (ArgumentException)
        {
            return null; // e.g. ".." above the root
        }
    }

    /// <summary>A click on a column header, or its sort command: sorts this side's panel by the column, ascending;
    /// by the column it is already sorted by, the other way round. Activates this side.</summary>
    [RelayCommand]
    public void SortBy(SortColumn column)
    {
        Activate();
        if (_panel is not { } panel) return;
        panel.Sort = panel.Sort.Column == column
            ? panel.Sort with { Descending = !panel.Sort.Descending }
            : new SortSpec(column);
    }

    /// <summary>Reverses the direction of this side's sort, keeping its column. Activates this side.</summary>
    public void ReverseSort()
    {
        Activate();
        if (_panel is { } panel) panel.Sort = panel.Sort with { Descending = !panel.Sort.Descending };
    }

    /// <summary>Opens the volume's root in this side's panel.</summary>
    [RelayCommand]
    public Task OpenVolumeAsync(VolumeInfo volume) => OpenFolderAsync(VfsPath.Local(volume.MountPoint));

    internal Task OpenFolderAsync(VfsPath path)
    {
        Activate();
        if (ShowsTerminal) Tabs.ActivateLastFileTab();
        return _panel?.NavigateToAsync(path) ?? Task.CompletedTask;
    }

    [RelayCommand]
    private Task GoBackAsync()
    {
        Activate();
        return _panel is null ? Task.CompletedTask : Tabs.GoBackAsync();
    }

    [RelayCommand]
    private Task GoForwardAsync()
    {
        Activate();
        return _panel is null ? Task.CompletedTask : Tabs.GoForwardAsync();
    }

    [RelayCommand]
    private Task OpenHistoryEntryAsync(VfsPath path)
    {
        Activate();
        return _panel?.NavigateToAsync(path) ?? Task.CompletedTask;
    }

    internal void OnActiveSideChanged() => OnPropertyChanged(nameof(IsActive));

    internal void OnQuickViewChanged()
    {
        OnPropertyChanged(nameof(ShowsQuickView));
        OnPropertyChanged(nameof(QuickViewer));
    }

    internal void OnViewChanged()
    {
        OnPropertyChanged(nameof(ShowVolumeBar));
        OnPropertyChanged(nameof(ShowsVolumeBarForFiles));
        if (_listMode == ListMode) return;
        _listMode = ListMode;
        OnPropertyChanged(nameof(ListMode));
        OnPropertyChanged(nameof(IsBrief));
    }

    /// <summary>Takes this side's part of the settings: the layout of its list and the colour rules.</summary>
    internal void OnSettingsChanged(AppSettings settings, bool fileColorsChanged)
    {
        Layout.Apply(Side == PanelSide.Left ? settings.LeftColumns : settings.RightColumns, settings.FontSize,
            settings.RowHeight);
        if (fileColorsChanged) OnPropertyChanged(nameof(FileColors));
    }

    internal void OnVolumesChanged()
    {
        OnPropertyChanged(nameof(Volumes));
        OnPropertyChanged(nameof(VolumeText));
    }

    private void OnTabsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PanelTabsViewModel.Active)) AttachPanel(Tabs.Active.Panel);
    }

    private void AttachPanel(PanelViewModel panel)
    {
        if (ReferenceEquals(panel, _panel)) return;
        if (_panel is not null) _panel.PropertyChanged -= OnPanelPropertyChanged;
        _panel = panel;
        panel.PropertyChanged += OnPanelPropertyChanged;
        if (IsEditingPath) CancelPathEdit();
        OnPropertyChanged(nameof(Panel));
        OnPropertyChanged(nameof(SelectedTab));
        OnPropertyChanged(nameof(Terminal));
        OnPropertyChanged(nameof(ShowsTerminal));
        OnPropertyChanged(nameof(ShowsVolumeBarForFiles));
        RaiseStatusChanged();
        RaiseSortChanged();
        OnPropertyChanged(nameof(VolumeText));
        OnPropertyChanged(nameof(HistoryEntries));
    }

    private void OnPanelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PanelViewModel.StatusText):
            case nameof(PanelViewModel.Notice):
            case nameof(PanelViewModel.Error):
            case nameof(PanelViewModel.ErrorKind):
                RaiseStatusChanged();
                break;
            case nameof(PanelViewModel.Sort):
                RaiseSortChanged();
                break;
            case nameof(PanelViewModel.CurrentPath):
                OnPropertyChanged(nameof(VolumeText));
                OnPropertyChanged(nameof(HistoryEntries));
                break;
        }
    }

    partial void OnMessageChanged(string? value) => RaiseStatusChanged();

    private void RaiseStatusChanged()
    {
        OnPropertyChanged(nameof(StatusLine));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(IsAccessDenied));
        OnPropertyChanged(nameof(IsConnectionLost));
    }

    private void RaiseSortChanged()
    {
        OnPropertyChanged(nameof(NameSortMark));
        OnPropertyChanged(nameof(ExtensionSortMark));
        OnPropertyChanged(nameof(SizeSortMark));
        OnPropertyChanged(nameof(DateSortMark));
        OnPropertyChanged(nameof(ModeSortMark));
    }

    private string SortMark(SortColumn column) =>
        _panel?.Sort is { } sort && sort.Column == column ? sort.Descending ? Descending : Ascending : "";

    private static bool IsOnVolume(string path, string mountPoint) =>
        mountPoint == "/" || path == mountPoint || path.StartsWith(mountPoint + "/", StringComparison.Ordinal);
}
