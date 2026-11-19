using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Tandem.Core.Listing;
using Tandem.Core.Settings;
using Tandem.Core.Vfs;

namespace Tandem.App.ViewModels;

/// <summary>
/// The tabs of one side of the window, one of them active. There is at least one tab once
/// <see cref="RestoreAsync"/> has run; before that there are none and <see cref="Active"/> must not be read.
/// </summary>
/// <remarks>Created and called on the UI thread. Owns its tabs: closing a tab, restoring and
/// <see cref="Dispose"/> dispose the tabs (and so the panels) that go away.</remarks>
public sealed partial class PanelTabsViewModel : ObservableObject, IDisposable
{
    private readonly ProviderRegistry _providers;
    private readonly TabCollection _tabs = [];
    private TabViewModel? _active;
    private VfsPath _fallback;
    private bool _showHidden;
    private bool _disposed;

    public PanelTabsViewModel(ProviderRegistry providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _providers = providers;
        Tabs = new ReadOnlyObservableCollection<TabViewModel>(_tabs);
    }

    /// <summary>The tabs in their order on screen.</summary>
    public ReadOnlyObservableCollection<TabViewModel> Tabs { get; }

    public TabViewModel Active =>
        _active ?? throw new InvalidOperationException("The side has no tabs until RestoreAsync has run.");

    /// <summary>Raised when something that is saved changes: the tabs, their order, the active tab, or a tab's
    /// folder, sort or pin.</summary>
    public event EventHandler? StateChanged;

    /// <summary>Whether the panels list hidden entries; set by the workspace for both sides, applied to new tabs.</summary>
    internal bool ShowHidden
    {
        get => _showHidden;
        set
        {
            _showHidden = value;
            foreach (var tab in _tabs) tab.Panel.ShowHidden = value;
        }
    }

    /// <summary>Opens a tab next to the active one on the same folder with the same sort, and activates it. Does
    /// nothing if that folder can no longer be shown.</summary>
    public async Task NewTabAsync()
    {
        var source = Active;
        if (source.Panel.CurrentPath == default) return;
        var tab = CreateTab(source.Panel.Sort, pinned: false);
        await tab.Panel.NavigateToAsync(source.Panel.CurrentPath);
        if (_disposed || tab.Panel.CurrentPath == default)
        {
            tab.Dispose();
            return;
        }
        var index = _tabs.IndexOf(source); // the source may have been closed meanwhile
        Attach(tab, index >= 0 ? index + 1 : _tabs.Count);
        SetActive(tab);
    }

    /// <summary>Whether <paramref name="tab"/> may be closed: it is one of this side's tabs, not the only one, and not
    /// pinned. The one rule every way of closing a tab follows.</summary>
    public bool CanClose(TabViewModel tab) => _tabs.Count > 1 && !tab.IsPinned && _tabs.Contains(tab);

    /// <summary>Closes the active tab and activates its right neighbour (or the left one if it was the last).
    /// False, and nothing closed, when <see cref="CanClose"/> refuses (the only tab, a pinned one).</summary>
    public bool CloseActive() => _active is { } active && Close(active);

    /// <summary>Closes <paramref name="tab"/> (its close button). The active tab stays active unless it is the one
    /// closed; then its right neighbour (or the left one) becomes active. False, and nothing closed, when
    /// <see cref="CanClose"/> refuses.</summary>
    public bool Close(TabViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (!CanClose(tab)) return false;
        var index = _tabs.IndexOf(tab);
        Detach(tab);
        _tabs.RemoveAt(index);
        if (ReferenceEquals(tab, _active)) SetActive(_tabs[Math.Min(index, _tabs.Count - 1)]);
        else RaiseStateChanged();
        tab.Dispose();
        return true;
    }

    /// <summary>Whether <see cref="CloseOthers"/> would close anything.</summary>
    public bool CanCloseOthers(TabViewModel keep) => _tabs.Any(tab => !ReferenceEquals(tab, keep) && CanClose(tab));

    /// <summary>Activates <paramref name="keep"/> and closes every other tab that <see cref="CanClose"/> allows, so
    /// pinned tabs stay. Returns how many were closed.</summary>
    public int CloseOthers(TabViewModel keep)
    {
        Activate(keep);
        var closed = 0;
        foreach (var tab in _tabs.Where(tab => !ReferenceEquals(tab, keep)).ToList())
            if (Close(tab)) closed++;
        return closed;
    }

    /// <summary>Activates the tab to the right, the first after the last.</summary>
    public void Next() => Step(+1);

    /// <summary>Activates the tab to the left, the last before the first.</summary>
    public void Previous() => Step(-1);

    public void Activate(TabViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (!_tabs.Contains(tab)) throw new ArgumentException("The tab does not belong to this side.", nameof(tab));
        SetActive(tab);
    }

    public Task GoBackAsync() => Active.GoBackAsync();

    public Task GoForwardAsync() => Active.GoForwardAsync();

    /// <summary>The tabs as saved; a tab that shows no folder is saved on the fallback folder of the last restore.</summary>
    public PanelState ToState()
    {
        var active = Active;
        var tabs = _tabs.Select(tab =>
        {
            var path = tab.Panel.CurrentPath == default ? _fallback : tab.Panel.CurrentPath;
            var sort = tab.Panel.Sort;
            return new TabState(path.ProviderId, path.Path, sort.Column, sort.Descending, tab.IsPinned);
        }).ToList();
        return new PanelState(tabs, _tabs.IndexOf(active));
    }

    /// <summary>
    /// Replaces the tabs with the saved ones. A tab whose folder is gone opens its nearest existing ancestor; a tab
    /// of an unknown provider, an invalid path, or with no reachable ancestor opens <paramref name="fallback"/>. An
    /// empty list gives one tab on <paramref name="fallback"/>. All tabs have been opened when the task completes.
    /// </summary>
    public async Task RestoreAsync(PanelState state, VfsPath fallback)
    {
        ArgumentNullException.ThrowIfNull(state);
        _fallback = fallback;
        var saved = state.Tabs.Count > 0
            ? state.Tabs
            : [new TabState(fallback.ProviderId, fallback.Path, SortColumn.Name, SortDescending: false, Pinned: false)];

        var tabs = saved.Select(t => CreateTab(new SortSpec(t.SortColumn, t.SortDescending), t.Pinned)).ToList();
        await Task.WhenAll(tabs.Select((tab, i) => OpenAsync(tab.Panel, saved[i], fallback)));
        if (_disposed)
        {
            foreach (var tab in tabs) tab.Dispose();
            return;
        }

        var old = _tabs.ToList();
        foreach (var tab in old) Detach(tab);
        _tabs.Clear();
        foreach (var tab in tabs) Attach(tab, _tabs.Count);
        SetActive(_tabs[Math.Clamp(state.ActiveTab, 0, _tabs.Count - 1)]);
        foreach (var tab in old) tab.Dispose();
    }

    /// <summary>Exchanges all tabs, with their panels and history, and the active tab between two sides. Both sides
    /// change silently first; only then are the notifications raised, so no listener sees a half-done swap.</summary>
    internal static void SwapContents(PanelTabsViewModel a, PanelTabsViewModel b)
    {
        var aTabs = a.TakeAll();
        var bTabs = b.TakeAll();
        a.PutAllSilently(bTabs.Tabs, bTabs.Active);
        b.PutAllSilently(aTabs.Tabs, aTabs.Active);
        a.RaiseContentsReplaced();
        b.RaiseContentsReplaced();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var tab in _tabs)
        {
            Detach(tab);
            tab.Dispose();
        }
    }

    private TabViewModel CreateTab(SortSpec sort, bool pinned)
    {
        var panel = new PanelViewModel(_providers) { Sort = sort, ShowHidden = _showHidden };
        return new TabViewModel(panel) { IsPinned = pinned };
    }

    private async Task OpenAsync(PanelViewModel panel, TabState saved, VfsPath fallback)
    {
        if (TryResolve(saved) is { } path)
        {
            for (VfsPath? candidate = path; candidate is { } folder; candidate = folder.Parent)
            {
                await panel.NavigateToAsync(folder);
                if (panel.CurrentPath != default) return;
            }
        }
        await panel.NavigateToAsync(fallback);
    }

    /// <summary>The saved folder, or null when its provider is not registered or its path is not valid.</summary>
    private VfsPath? TryResolve(TabState saved)
    {
        try
        {
            var path = new VfsPath(saved.ProviderId, saved.Path);
            _ = _providers.For(path);
            return path;
        }
        catch (Exception e) when (e is KeyNotFoundException or ArgumentException)
        {
            return null;
        }
    }

    private void Step(int delta)
    {
        if (_tabs.Count == 0) return;
        var index = _tabs.IndexOf(Active);
        SetActive(_tabs[(index + delta + _tabs.Count) % _tabs.Count]);
    }

    private void SetActive(TabViewModel? tab)
    {
        if (ReferenceEquals(_active, tab)) return;
        _active = tab;
        OnPropertyChanged(nameof(Active));
        RaiseStateChanged();
    }

    private void Attach(TabViewModel tab, int index)
    {
        tab.StateChanged += OnTabStateChanged;
        _tabs.Insert(index, tab);
        RaiseStateChanged();
    }

    private void Detach(TabViewModel tab) => tab.StateChanged -= OnTabStateChanged;

    private (List<TabViewModel> Tabs, TabViewModel? Active) TakeAll()
    {
        var tabs = _tabs.ToList();
        foreach (var tab in tabs) Detach(tab);
        return (tabs, _active);
    }

    private void PutAllSilently(List<TabViewModel> tabs, TabViewModel? active)
    {
        _tabs.ReplaceSilently(tabs);
        foreach (var tab in tabs) tab.StateChanged += OnTabStateChanged;
        _active = active;
    }

    private void RaiseContentsReplaced()
    {
        _tabs.RaiseReset();
        OnPropertyChanged(nameof(Active));
        RaiseStateChanged();
    }

    /// <summary>The tabs, with a way to replace them all without notifying, for a swap that must look atomic.</summary>
    private sealed class TabCollection : ObservableCollection<TabViewModel>
    {
        public void ReplaceSilently(IEnumerable<TabViewModel> tabs)
        {
            CheckReentrancy();
            Items.Clear();
            foreach (var tab in tabs) Items.Add(tab);
        }

        public void RaiseReset()
        {
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }

    private void OnTabStateChanged(object? sender, EventArgs e) => RaiseStateChanged();

    private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);
}
