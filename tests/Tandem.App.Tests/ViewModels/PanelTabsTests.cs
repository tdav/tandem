using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Tandem.App.ViewModels;
using Tandem.Core.Listing;
using Tandem.Core.Settings;
using Tandem.Core.Vfs;
using Tandem.Core.Vfs.InMemory;

namespace Tandem.App.Tests.ViewModels;

public class PanelTabsTests : IDisposable
{
    private readonly InMemoryFileSystem _fs = new();
    private readonly ObservedProvider _observed;
    private readonly ProviderRegistry _providers = new();
    private readonly PanelTabsViewModel _tabs;

    public PanelTabsTests()
    {
        _observed = new ObservedProvider(_fs);
        _providers.Register(_observed);
        _fs.AddDirectory("/home");
        _fs.AddDirectory("/a/b/c");
        _fs.AddDirectory("/x");
        _tabs = new PanelTabsViewModel(_providers);
    }

    public void Dispose()
    {
        _tabs.Dispose();
        GC.SuppressFinalize(this);
    }

    private static VfsPath Mem(string path) => new("mem", path);

    private static TabState Tab(string path, string provider = "mem", SortColumn column = SortColumn.Name,
        bool descending = false, bool pinned = false) => new(provider, path, column, descending, pinned);

    private Task RestoreAsync(params TabState[] tabs) => _tabs.RestoreAsync(new PanelState(tabs, 0), Mem("/home"));

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("The condition was not met in time.");
            await Task.Delay(10);
        }
    }

    [AvaloniaFact]
    public async Task New_tab_copies_current_path_and_activates()
    {
        await RestoreAsync(Tab("/a", column: SortColumn.Size, descending: true));
        var first = _tabs.Active;

        await _tabs.NewTabAsync();

        Assert.Equal(2, _tabs.Tabs.Count);
        Assert.NotSame(first, _tabs.Active);
        Assert.Same(_tabs.Tabs[1], _tabs.Active);
        Assert.NotSame(first.Panel, _tabs.Active.Panel);
        Assert.Equal(Mem("/a"), _tabs.Active.Panel.CurrentPath);
        Assert.Equal(new SortSpec(SortColumn.Size, Descending: true), _tabs.Active.Panel.Sort);
        Assert.False(_tabs.Active.History.CanGoBack);
    }

    [AvaloniaFact]
    public async Task New_tab_is_inserted_after_the_active_one()
    {
        await RestoreAsync(Tab("/a"), Tab("/x"));
        _tabs.Activate(_tabs.Tabs[0]);

        await _tabs.NewTabAsync();

        Assert.Equal([Mem("/a"), Mem("/a"), Mem("/x")], _tabs.Tabs.Select(t => t.Panel.CurrentPath));
        Assert.Same(_tabs.Tabs[1], _tabs.Active);
    }

    [AvaloniaFact]
    public async Task Cannot_close_last_or_pinned_tab()
    {
        await RestoreAsync(Tab("/a"));

        Assert.False(_tabs.CloseActive());
        Assert.Single(_tabs.Tabs);

        await _tabs.NewTabAsync();
        _tabs.Active.IsPinned = true;

        Assert.False(_tabs.CloseActive());
        Assert.Equal(2, _tabs.Tabs.Count);

        _tabs.Active.IsPinned = false;
        Assert.True(_tabs.CloseActive());
        Assert.Single(_tabs.Tabs);
    }

    [AvaloniaFact]
    public async Task Close_others_keeps_the_given_and_pinned_tabs()
    {
        await RestoreAsync(Tab("/a", pinned: true), Tab("/x"), Tab("/home"), Tab("/a/b"));
        var (pinned, keep) = (_tabs.Tabs[0], _tabs.Tabs[2]);
        var changes = 0;
        _tabs.StateChanged += (_, _) => changes++;

        Assert.True(_tabs.CanCloseOthers(keep));
        Assert.Equal(2, _tabs.CloseOthers(keep));

        Assert.Equal([pinned, keep], _tabs.Tabs);
        Assert.Same(keep, _tabs.Active);
        Assert.True(changes > 0);
        Assert.False(_tabs.CanCloseOthers(keep)); // only the pinned one is left besides it
        Assert.Equal(0, _tabs.CloseOthers(keep));
    }

    [AvaloniaFact]
    public async Task Close_others_disposes_closed_panels()
    {
        await RestoreAsync(Tab("/a"), Tab("/x"), Tab("/home"));
        await WaitUntil(() => _observed.ActiveWatches == 3);

        _tabs.CloseOthers(_tabs.Tabs[1]);

        Assert.Equal(Mem("/x"), Assert.Single(_tabs.Tabs).Panel.CurrentPath);
        await WaitUntil(() => _observed.ActiveWatches == 1); // only the kept tab still watches its folder
    }

    [AvaloniaFact]
    public async Task Closing_another_tab_keeps_the_active_one_and_refuses_a_pinned_one()
    {
        await RestoreAsync(Tab("/a"), Tab("/x", pinned: true), Tab("/home"));
        var active = _tabs.Tabs[2];
        _tabs.Activate(active);

        Assert.False(_tabs.CanClose(_tabs.Tabs[1]));
        Assert.False(_tabs.Close(_tabs.Tabs[1]));
        Assert.True(_tabs.CanClose(_tabs.Tabs[0]));
        Assert.True(_tabs.Close(_tabs.Tabs[0]));

        Assert.Equal([Mem("/x"), Mem("/home")], _tabs.Tabs.Select(t => t.Panel.CurrentPath));
        Assert.Same(active, _tabs.Active);
    }

    [AvaloniaFact]
    public async Task Close_activates_neighbour()
    {
        await RestoreAsync(Tab("/a"), Tab("/x"), Tab("/home"));
        await WaitUntil(() => _observed.ActiveWatches == 3);

        _tabs.Activate(_tabs.Tabs[1]);
        Assert.True(_tabs.CloseActive());
        Assert.Equal(Mem("/home"), _tabs.Active.Panel.CurrentPath); // the one to the right

        Assert.True(_tabs.CloseActive());
        Assert.Equal(Mem("/a"), _tabs.Active.Panel.CurrentPath); // no tab to the right: the one to the left
        Assert.Single(_tabs.Tabs);

        await WaitUntil(() => _observed.ActiveWatches == 1); // the closed tabs' panels are disposed
    }

    [AvaloniaFact]
    public async Task Next_and_previous_wrap_around()
    {
        await RestoreAsync(Tab("/a"), Tab("/x"));
        _tabs.Activate(_tabs.Tabs[1]);

        _tabs.Next();
        Assert.Same(_tabs.Tabs[0], _tabs.Active);
        _tabs.Previous();
        Assert.Same(_tabs.Tabs[1], _tabs.Active);
        _tabs.Next();
        _tabs.Next();
        Assert.Same(_tabs.Tabs[1], _tabs.Active);
    }

    [AvaloniaFact]
    public async Task Activate_rejects_a_foreign_tab()
    {
        await RestoreAsync(Tab("/a"));
        using var other = new PanelTabsViewModel(_providers);
        await other.RestoreAsync(new PanelState([Tab("/x")], 0), Mem("/home"));

        Assert.Throws<ArgumentException>(() => _tabs.Activate(other.Active));
    }

    [AvaloniaFact]
    public async Task Back_does_not_add_history_entry()
    {
        await RestoreAsync(Tab("/a"));
        var panel = _tabs.Active.Panel;
        await panel.NavigateToAsync(Mem("/a/b"));
        await panel.NavigateToAsync(Mem("/a/b/c"));

        await _tabs.GoBackAsync();

        Assert.Equal(Mem("/a/b"), panel.CurrentPath);
        Assert.True(_tabs.Active.History.CanGoForward);

        await _tabs.GoBackAsync();
        Assert.Equal(Mem("/a"), panel.CurrentPath);
        Assert.False(_tabs.Active.History.CanGoBack);

        await _tabs.GoForwardAsync();
        await _tabs.GoForwardAsync();
        Assert.Equal(Mem("/a/b/c"), panel.CurrentPath);
        Assert.False(_tabs.Active.History.CanGoForward);
        Assert.Equal([Mem("/a/b/c"), Mem("/a/b"), Mem("/a")], _tabs.Active.History.Recent);
    }

    [AvaloniaFact]
    public async Task Back_to_a_vanished_folder_stays_and_skips_it_next_time()
    {
        await RestoreAsync(Tab("/a"));
        var panel = _tabs.Active.Panel;
        await panel.NavigateToAsync(Mem("/x"));
        await panel.NavigateToAsync(Mem("/home"));
        await _fs.DeleteAsync(Mem("/x"), recursive: true, CancellationToken.None);

        await _tabs.GoBackAsync();

        Assert.Equal(Mem("/home"), panel.CurrentPath);
        Assert.Equal(PanelErrorKind.NotFound, panel.ErrorKind);
        Assert.False(_tabs.Active.History.CanGoForward); // the position still matches the folder shown

        await _tabs.GoBackAsync();
        Assert.Equal(Mem("/a"), panel.CurrentPath);
    }

    [AvaloniaFact]
    public async Task Title_is_folder_name_or_slash_for_root()
    {
        await RestoreAsync(Tab("/a/b"));
        Assert.Equal("b", _tabs.Active.Title);

        await _tabs.Active.Panel.NavigateToAsync(Mem("/"));
        Assert.Equal("/", _tabs.Active.Title);
    }

    [AvaloniaFact]
    public async Task Restore_missing_path_uses_nearest_ancestor()
    {
        await RestoreAsync(Tab("/a/b/gone/deeper"));

        Assert.Equal(Mem("/a/b"), _tabs.Active.Panel.CurrentPath);
    }

    [AvaloniaFact]
    public async Task Restore_unknown_provider_uses_fallback()
    {
        await _tabs.RestoreAsync(new PanelState([Tab("/a", provider: "sftp"), Tab("/x")], 0), Mem("/home"));

        Assert.Equal(2, _tabs.Tabs.Count);
        Assert.Equal(Mem("/home"), _tabs.Tabs[0].Panel.CurrentPath);
        Assert.Equal(Mem("/x"), _tabs.Tabs[1].Panel.CurrentPath);
    }

    [AvaloniaFact]
    public async Task Restore_activates_the_saved_tab()
    {
        await _tabs.RestoreAsync(new PanelState([Tab("/a"), Tab("/x")], 1), Mem("/home"));

        Assert.Same(_tabs.Tabs[1], _tabs.Active);
        Assert.Equal(Mem("/x"), _tabs.Active.Panel.CurrentPath);
    }

    [AvaloniaFact]
    public async Task State_roundtrip_preserves_sort_and_pin()
    {
        var state = new PanelState(
            [Tab("/a", column: SortColumn.Modified, descending: true, pinned: true), Tab("/x", column: SortColumn.Size)],
            1);
        await _tabs.RestoreAsync(state, Mem("/home"));

        var saved = _tabs.ToState();

        Assert.Equal(1, saved.ActiveTab);
        Assert.Equal(state.Tabs, saved.Tabs);
        Assert.True(_tabs.Tabs[0].IsPinned);
        Assert.Equal(new SortSpec(SortColumn.Modified, Descending: true), _tabs.Tabs[0].Panel.Sort);
    }

    [AvaloniaFact]
    public async Task State_changed_is_raised_for_path_sort_pin_and_tabs()
    {
        await RestoreAsync(Tab("/a"));
        var changes = 0;
        _tabs.StateChanged += (_, _) => changes++;

        await _tabs.Active.Panel.NavigateToAsync(Mem("/x"));
        Assert.True(changes > 0);

        changes = 0;
        _tabs.Active.Panel.Sort = new SortSpec(SortColumn.Size);
        Assert.True(changes > 0);

        changes = 0;
        _tabs.Active.IsPinned = true;
        Assert.True(changes > 0);

        changes = 0;
        await _tabs.NewTabAsync();
        Assert.True(changes > 0);
    }
}
