using Avalonia.Headless.XUnit;
using Tandem.App.ViewModels;
using Tandem.Core.Listing;
using Tandem.Core.Settings;
using Tandem.Core.Vfs;
using Tandem.Core.Vfs.InMemory;

namespace Tandem.App.Tests.ViewModels;

public class WorkspaceTests : IDisposable
{
    private const string Home = "/home";

    private readonly InMemoryFileSystem _fs = new(VfsPath.LocalProviderId);
    private readonly ProviderRegistry _providers = new();
    private readonly string _stateDirectory =
        Path.Combine(Path.GetTempPath(), "tandem-workspace-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeDelay _delay = new();
    private readonly List<WorkspaceViewModel> _workspaces = [];

    public WorkspaceTests()
    {
        _providers.Register(_fs);
        _fs.AddDirectory(Home);
        _fs.AddDirectory("/a/b");
        _fs.AddDirectory("/x");
        _fs.AddFile("/x/.hidden");
        _fs.AddFile("/x/visible");
    }

    public void Dispose()
    {
        foreach (var workspace in _workspaces) workspace.Dispose();
        try
        {
            Directory.Delete(_stateDirectory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // nothing was saved
        }
        GC.SuppressFinalize(this);
    }

    private static VfsPath Local(string path) => VfsPath.Local(path);

    private JsonStateStore Store() => new(_stateDirectory);

    private WorkspaceViewModel Create(JsonStateStore? store = null)
    {
        var workspace = new WorkspaceViewModel(_providers, store ?? Store(), Home, _delay.Invoke);
        _workspaces.Add(workspace);
        return workspace;
    }

    private async Task<WorkspaceViewModel> LoadedAsync()
    {
        var workspace = Create();
        await workspace.LoadAsync();
        return workspace;
    }

    private AppState? SavedState() =>
        File.Exists(Path.Combine(_stateDirectory, "state.json")) ? Store().Load(Home) : null;

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("The condition was not met in time.");
            await Task.Delay(10);
        }
    }

    [AvaloniaFact]
    public async Task First_load_opens_home_on_both_sides()
    {
        var workspace = await LoadedAsync();

        Assert.Equal(PanelSide.Left, workspace.ActiveSide);
        Assert.Equal(Local(Home), workspace.Left.Active.Panel.CurrentPath);
        Assert.Equal(Local(Home), workspace.Right.Active.Panel.CurrentPath);
        Assert.Same(workspace.Left.Active.Panel, workspace.ActivePanel);
        Assert.Same(workspace.Right.Active.Panel, workspace.PassivePanel);
    }

    [AvaloniaFact]
    public async Task Switch_changes_active_side()
    {
        var workspace = await LoadedAsync();
        var changed = new List<string?>();
        workspace.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        workspace.SwitchPanel();

        Assert.Equal(PanelSide.Right, workspace.ActiveSide);
        Assert.Same(workspace.Right.Active.Panel, workspace.ActivePanel);
        Assert.Same(workspace.Left.Active.Panel, workspace.PassivePanel);
        Assert.Contains(nameof(WorkspaceViewModel.ActivePanel), changed);
        Assert.Contains(nameof(WorkspaceViewModel.PassivePanel), changed);

        workspace.SwitchPanel();
        Assert.Equal(PanelSide.Left, workspace.ActiveSide);
    }

    [AvaloniaFact]
    public async Task Active_panel_follows_the_active_tab()
    {
        var workspace = await LoadedAsync();
        var changed = new List<string?>();
        workspace.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        await workspace.Left.NewTabAsync();

        Assert.Same(workspace.Left.Active.Panel, workspace.ActivePanel);
        Assert.Contains(nameof(WorkspaceViewModel.ActivePanel), changed);
    }

    [AvaloniaFact]
    public async Task Mirror_opens_active_path_in_passive_panel()
    {
        var workspace = await LoadedAsync();
        await workspace.ActivePanel.NavigateToAsync(Local("/a/b"));

        await workspace.MirrorToOtherAsync();

        Assert.Equal(Local("/a/b"), workspace.PassivePanel.CurrentPath);
        Assert.Equal(PanelSide.Left, workspace.ActiveSide);
        Assert.True(workspace.Right.Active.History.CanGoBack);
    }

    [AvaloniaFact]
    public async Task Swap_exchanges_tab_sets()
    {
        var workspace = await LoadedAsync();
        await workspace.Left.Active.Panel.NavigateToAsync(Local("/a"));
        await workspace.Left.NewTabAsync();
        await workspace.Right.Active.Panel.NavigateToAsync(Local("/x"));
        var leftTabs = workspace.Left.Tabs.ToList();
        var leftActive = workspace.Left.Active;
        var rightTabs = workspace.Right.Tabs.ToList();

        await workspace.SwapPanelsAsync();

        Assert.Equal(rightTabs, workspace.Left.Tabs);
        Assert.Equal(leftTabs, workspace.Right.Tabs);
        Assert.Same(leftActive, workspace.Right.Active);
        Assert.Equal(PanelSide.Left, workspace.ActiveSide);
        Assert.Equal(Local("/x"), workspace.ActivePanel.CurrentPath);
        Assert.True(workspace.Right.Tabs[0].History.CanGoBack); // tabs keep their history

        // Each side still owns the tabs it now shows.
        Assert.True(workspace.Right.CloseActive());
        Assert.Single(workspace.Right.Tabs);
        Assert.Single(workspace.Left.Tabs);
    }

    [AvaloniaFact]
    public async Task ShowHidden_applies_to_every_panel_including_new_tabs()
    {
        var workspace = await LoadedAsync();
        await workspace.Right.NewTabAsync();

        workspace.ShowHidden = true;

        Assert.All(workspace.Left.Tabs.Concat(workspace.Right.Tabs), t => Assert.True(t.Panel.ShowHidden));
        await workspace.Left.NewTabAsync();
        Assert.True(workspace.Left.Active.Panel.ShowHidden);

        await workspace.SwapPanelsAsync();
        workspace.ShowHidden = false;
        Assert.All(workspace.Left.Tabs.Concat(workspace.Right.Tabs), t => Assert.False(t.Panel.ShowHidden));
    }

    [AvaloniaFact]
    public async Task Save_then_load_restores_tabs_favorites_and_view()
    {
        var workspace = await LoadedAsync();
        await workspace.Left.Active.Panel.NavigateToAsync(Local("/a/b"));
        workspace.Left.Active.Panel.Sort = new SortSpec(SortColumn.Size, Descending: true);
        await workspace.Left.NewTabAsync();
        await workspace.Left.Active.Panel.NavigateToAsync(Local("/x"));
        workspace.Left.Active.IsPinned = true;
        await workspace.Right.Active.Panel.NavigateToAsync(Local("/a"));
        workspace.ActiveSide = PanelSide.Right;
        workspace.Favorites.Add("/a/b");
        workspace.Favorites.Add("/x");
        workspace.View = new ViewState(Sidebar: true, VolumeBars: false, CommandLine: false, FKeyBar: true);
        workspace.ShowHidden = true;

        workspace.Save();
        Assert.Null(workspace.LastSaveError);
        var restored = await LoadedAsync();

        Assert.Equal([Local("/a/b"), Local("/x")], restored.Left.Tabs.Select(t => t.Panel.CurrentPath));
        Assert.Same(restored.Left.Tabs[1], restored.Left.Active);
        Assert.Equal(new SortSpec(SortColumn.Size, Descending: true), restored.Left.Tabs[0].Panel.Sort);
        Assert.True(restored.Left.Tabs[1].IsPinned);
        Assert.Equal(Local("/a"), restored.Right.Active.Panel.CurrentPath);
        Assert.Equal(PanelSide.Right, restored.ActiveSide);
        Assert.Equal(["/a/b", "/x"], restored.Favorites.Items);
        Assert.Equal(new ViewState(Sidebar: true, VolumeBars: false, CommandLine: false, FKeyBar: true), restored.View);
        Assert.True(restored.ShowHidden);
        Assert.All(restored.Right.Tabs.Concat(restored.Left.Tabs), t => Assert.True(t.Panel.ShowHidden));
    }

    [AvaloniaFact]
    public async Task Save_keeps_window_as_set_and_language_as_loaded()
    {
        Store().Save(AppState.Default(Home) with { Language = "ru" });
        var workspace = await LoadedAsync();
        var window = new WindowState(10, 20, 800, 600, Maximized: false, SplitRatio: 0.4);

        workspace.Window = window;
        workspace.Save();

        var saved = SavedState()!;
        Assert.Equal("ru", saved.Language);
        Assert.Equal(window, saved.Window);
        Assert.Equal(AppState.CurrentVersion, saved.Version);
    }

    [AvaloniaFact]
    public async Task Save_before_load_writes_nothing()
    {
        var workspace = Create();

        workspace.Save();

        Assert.Null(SavedState());
        await Task.CompletedTask;
    }

    [AvaloniaFact]
    public async Task Save_failure_is_exposed_not_thrown()
    {
        var blocker = Path.Combine(Path.GetTempPath(), "tandem-workspace-blocker-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(blocker, "");
        try
        {
            var workspace = Create(new JsonStateStore(Path.Combine(blocker, "sub")));
            await workspace.LoadAsync();

            workspace.Save();

            Assert.IsAssignableFrom<IOException>(workspace.LastSaveError);
        }
        finally
        {
            File.Delete(blocker);
        }
    }

    [AvaloniaFact]
    public async Task Changes_schedule_one_throttled_save()
    {
        var workspace = await LoadedAsync();
        Assert.Empty(_delay.Pending);

        workspace.Favorites.Add("/x");
        workspace.SwitchPanel();
        await workspace.Left.Active.Panel.NavigateToAsync(Local("/a"));

        var pending = Assert.Single(_delay.Pending);
        Assert.Equal(TimeSpan.FromSeconds(2), pending.Duration);
        Assert.Null(SavedState());

        pending.Complete();
        await WaitUntil(() => SavedState() is not null);

        var saved = SavedState()!;
        Assert.Equal(["/x"], saved.Favorites);
        Assert.False(saved.LeftIsActive);
        Assert.Equal("/a", saved.Left.Tabs[0].Path);
        Assert.Empty(_delay.Pending);

        workspace.View = workspace.View with { Sidebar = true };
        Assert.Single(_delay.Pending);
    }

    [AvaloniaFact]
    public async Task Explicit_save_cancels_the_pending_autosave()
    {
        var workspace = await LoadedAsync();
        workspace.ShowHidden = true;
        var pending = Assert.Single(_delay.Pending);

        workspace.Save();

        Assert.True(pending.Cancelled);
        Assert.True(SavedState()!.ShowHidden);
    }

    [AvaloniaFact]
    public async Task Dispose_cancels_pending_autosave_without_saving()
    {
        var workspace = await LoadedAsync();
        workspace.Favorites.Add("/x");
        var pending = Assert.Single(_delay.Pending);

        workspace.Dispose();

        Assert.True(pending.Cancelled);
        Assert.Null(SavedState());
    }

    [AvaloniaFact]
    public async Task A_failing_delay_does_not_disable_autosave()
    {
        var workspace = await LoadedAsync();
        workspace.Favorites.Add("/x");
        var broken = Assert.Single(_delay.Pending);

        broken.Fail(new InvalidOperationException("timer broke"));
        await WaitUntil(() => broken.Task.IsCompleted);
        await Task.Yield();

        workspace.Favorites.Add("/a");
        var next = Assert.Single(_delay.Pending);
        next.Complete();
        await WaitUntil(() => SavedState() is not null);
        Assert.Equal(["/x", "/a"], SavedState()!.Favorites);
    }

    [AvaloniaFact]
    public async Task Swap_never_shows_the_same_panel_on_both_sides()
    {
        var workspace = await LoadedAsync();
        await workspace.Left.Active.Panel.NavigateToAsync(Local("/a"));
        await workspace.Left.NewTabAsync();
        await workspace.Right.Active.Panel.NavigateToAsync(Local("/x"));
        var seen = new List<(PanelViewModel Active, PanelViewModel Passive)>();
        void Record() => seen.Add((workspace.ActivePanel, workspace.PassivePanel));
        workspace.PropertyChanged += (_, _) => Record();
        foreach (var side in new[] { workspace.Left, workspace.Right })
        {
            side.PropertyChanged += (_, _) => Record();
            side.StateChanged += (_, _) => Record();
            ((System.Collections.Specialized.INotifyCollectionChanged)side.Tabs).CollectionChanged += (_, _) => Record();
        }

        await workspace.SwapPanelsAsync();
        await workspace.SwapPanelsAsync();

        Assert.NotEmpty(seen);
        Assert.All(seen, pair => Assert.NotSame(pair.Active, pair.Passive));
    }

    [AvaloniaFact]
    public async Task Restore_does_not_schedule_a_save()
    {
        Store().Save(AppState.Default(Home) with { Favorites = ["/x"] });

        await LoadedAsync();

        Assert.Empty(_delay.Pending);
    }

    /// <summary>A delay the test completes by hand.</summary>
    private sealed class FakeDelay
    {
        private readonly List<Call> _calls = [];

        public IReadOnlyList<Call> Pending => _calls.Where(c => !c.Task.IsCompleted).ToList();

        public Task Invoke(TimeSpan duration, CancellationToken ct)
        {
            var call = new Call(duration);
            ct.Register(() => call.Source.TrySetCanceled(ct));
            _calls.Add(call);
            return call.Task;
        }

        public sealed class Call(TimeSpan duration)
        {
            public TaskCompletionSource Source { get; } = new();
            public TimeSpan Duration => duration;
            public Task Task => Source.Task;
            public bool Cancelled => Source.Task.IsCanceled;
            public void Complete() => Source.TrySetResult();
            public void Fail(Exception error) => Source.TrySetException(error);
        }
    }
}
