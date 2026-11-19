using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tandem.App.Resources;
using Tandem.App.Services;
using Tandem.Core.Listing;
using Tandem.Core.Platform;
using Tandem.Core.Vfs;

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

    internal SideViewModel(MainViewModel main, PanelSide side, PanelTabsViewModel tabs)
    {
        _main = main;
        Side = side;
        Tabs = tabs;
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

    public bool IsActive => _main.Workspace.ActiveSide == Side;

    public bool ShowVolumeBar => _main.Workspace.View.VolumeBars;

    public IReadOnlyList<VolumeInfo> Volumes => _main.Volumes;

    /// <summary>Free and total space of the volume holding the folder shown; empty when it is not known.</summary>
    public string VolumeText
    {
        get
        {
            if (_panel is not { CurrentPath: var path } || path == default ||
                path.ProviderId != VfsPath.LocalProviderId) return "";
            var volume = _main.Volumes
                .Where(v => IsOnVolume(path.Path, v.MountPoint))
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

    /// <summary>The status row: this side's message, else the panel's error, else its selection status.</summary>
    public string StatusLine => Message ?? _panel?.Error ?? _panel?.StatusText ?? "";

    public bool HasError => Message is not null || _panel?.Error is not null;

    /// <summary>The panel on the local disk cannot show a folder for lack of permission: the status row offers the
    /// system's access settings.</summary>
    public bool IsAccessDenied =>
        Message is null && _panel?.ErrorKind == PanelErrorKind.AccessDenied &&
        (_panel.CurrentPath == default || _panel.CurrentPath.ProviderId == VfsPath.LocalProviderId);

    public string NameSortMark => SortMark(SortColumn.Name);
    public string ExtensionSortMark => SortMark(SortColumn.Extension);
    public string SizeSortMark => SortMark(SortColumn.Size);
    public string DateSortMark => SortMark(SortColumn.Modified);

    /// <summary>The folders of the active tab's history, latest first, for the back/forward menu.</summary>
    public IReadOnlyList<VfsPath> HistoryEntries => _panel is null ? [] : Tabs.Active.History.Recent;

    public void Activate() => _main.Workspace.ActiveSide = Side;

    /// <summary>Turns the path row into an editor holding the current path.</summary>
    public void BeginPathEdit()
    {
        if (_panel is null) return;
        Activate();
        PathText = _panel.CurrentPath == default ? "" : _panel.CurrentPath.Path;
        Message = null;
        IsEditingPath = true;
    }

    /// <summary>Enter in the path editor: goes to the typed folder ("~" is the home folder) and closes the editor.
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
        var providerId = panel.CurrentPath == default ? VfsPath.LocalProviderId : panel.CurrentPath.ProviderId;
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

    /// <summary>A typed path, or null when it is not one: absolute, or starting with "~" for the home folder.</summary>
    internal static VfsPath? ParsePath(string text, string providerId, string homeDirectory)
    {
        if (text == "~" || text.StartsWith("~/", StringComparison.Ordinal))
            text = homeDirectory + text[1..];
        if (!text.StartsWith('/')) return null;
        try
        {
            return new VfsPath(providerId, text);
        }
        catch (ArgumentException)
        {
            return null; // e.g. ".." above the root
        }
    }

    [RelayCommand]
    private void SortBy(SortColumn column)
    {
        Activate();
        if (_panel is not { } panel) return;
        panel.Sort = panel.Sort.Column == column
            ? panel.Sort with { Descending = !panel.Sort.Descending }
            : new SortSpec(column);
    }

    /// <summary>Opens the volume's root in this side's panel.</summary>
    [RelayCommand]
    public Task OpenVolumeAsync(VolumeInfo volume)
    {
        Activate();
        return _panel?.NavigateToAsync(VfsPath.Local(volume.MountPoint)) ?? Task.CompletedTask;
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

    internal void OnViewChanged() => OnPropertyChanged(nameof(ShowVolumeBar));

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
    }

    private void RaiseSortChanged()
    {
        OnPropertyChanged(nameof(NameSortMark));
        OnPropertyChanged(nameof(ExtensionSortMark));
        OnPropertyChanged(nameof(SizeSortMark));
        OnPropertyChanged(nameof(DateSortMark));
    }

    private string SortMark(SortColumn column) =>
        _panel?.Sort is { } sort && sort.Column == column ? sort.Descending ? Descending : Ascending : "";

    private static bool IsOnVolume(string path, string mountPoint) =>
        mountPoint == "/" || path == mountPoint || path.StartsWith(mountPoint + "/", StringComparison.Ordinal);
}
