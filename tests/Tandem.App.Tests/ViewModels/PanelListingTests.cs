using System.Collections.Specialized;
using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Tandem.App.ViewModels;
using Tandem.Core.Listing;
using Tandem.Core.Vfs;
using Tandem.Core.Vfs.InMemory;

namespace Tandem.App.Tests.ViewModels;

// In the language collection: row texts are compared with English resources, and the 100k timing test should
// not share the machine with other tests.
[Collection(LanguageCollection.Name)]
public class PanelListingTests : IDisposable
{
    private readonly InMemoryFileSystem _fs = new();
    private readonly ObservedProvider _observed;
    private readonly ProviderRegistry _providers = new();
    private PanelViewModel? _panel;

    public PanelListingTests()
    {
        _observed = new ObservedProvider(_fs);
        _providers.Register(_observed);
    }

    public void Dispose()
    {
        _panel?.Dispose();
        GC.SuppressFinalize(this);
    }

    private PanelViewModel CreatePanel() => _panel = new PanelViewModel(_providers);

    private static VfsPath Mem(string path) => new("mem", path);

    private static string[] Names(PanelViewModel panel) => panel.Rows.Select(r => r.Name).ToArray();

    private static void PutCursorOn(PanelViewModel panel, string name) =>
        panel.CursorIndex = panel.Rows.ToList().FindIndex(r => r.Name == name);

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
    public async Task Lists_sorted_with_parent_link_first()
    {
        _fs.AddFile("/d/b.txt", "bb");
        _fs.AddFile("/d/File10");
        _fs.AddFile("/d/file2");
        _fs.AddFile("/d/a.txt");
        _fs.AddDirectory("/d/sub");
        var panel = CreatePanel();
        var navigated = new List<VfsPath>();
        panel.Navigated += (_, path) => navigated.Add(path);

        await panel.NavigateToAsync(Mem("/d"));

        Assert.Equal(["..", "sub", "a.txt", "b.txt", "file2", "File10"], Names(panel));
        Assert.True(panel.Rows[0].IsParentLink);
        Assert.Null(panel.Rows[0].Entry);
        Assert.All(panel.Rows.Skip(1), r => Assert.False(r.IsParentLink));
        Assert.Equal(Mem("/d"), panel.CurrentPath);
        Assert.Equal(0, panel.CursorIndex);
        Assert.Same(panel.Rows[0], panel.CursorRow);
        Assert.False(panel.IsLoading);
        Assert.Null(panel.Error);
        Assert.Equal([Mem("/d")], navigated);
    }

    [AvaloniaFact]
    public async Task Row_shows_size_date_and_mode_text()
    {
        var modified = new DateTimeOffset(2026, 3, 4, 15, 6, 0, TimeSpan.Zero);
        _fs.AddFile("/d/report.pdf", new byte[1536], modified);
        _fs.AddDirectory("/d/sub");
        await _fs.SetModeAsync(Mem("/d/report.pdf"),
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead, CancellationToken.None);
        var panel = CreatePanel();

        await panel.NavigateToAsync(Mem("/d"));

        var file = panel.Rows.Single(r => r.Name == "report.pdf");
        Assert.Equal("pdf", file.Extension);
        Assert.Equal("1.5 KB", file.SizeText);
        Assert.Equal(modified.ToLocalTime().ToString("g", System.Globalization.CultureInfo.GetCultureInfo("en")),
            file.DateText);
        Assert.Equal("rwxr-xr--", file.ModeText);
        var folder = panel.Rows.Single(r => r.Name == "sub");
        Assert.Equal("Folder", folder.SizeText);
        Assert.Equal("", folder.Extension);
        Assert.Equal("", panel.Rows[0].ModeText);
        Assert.Equal("", panel.Rows[0].DateText);
    }

    [AvaloniaFact]
    public async Task Root_has_no_parent_link()
    {
        _fs.AddFile("/a.txt");
        var panel = CreatePanel();

        await panel.NavigateToAsync(Mem("/"));

        Assert.Equal(["a.txt"], Names(panel));
    }

    [AvaloniaFact]
    public async Task Hidden_files_filtered_until_ShowHidden()
    {
        _fs.AddFile("/d/.secret");
        _fs.AddFile("/d/visible");
        var panel = CreatePanel();

        await panel.NavigateToAsync(Mem("/d"));
        Assert.Equal(["..", "visible"], Names(panel));

        panel.ShowHidden = true;
        await WaitUntil(() => panel.Rows.Count == 3);
        Assert.Equal(["..", ".secret", "visible"], Names(panel));

        panel.ShowHidden = false;
        await WaitUntil(() => panel.Rows.Count == 2);
        Assert.Equal(["..", "visible"], Names(panel));
    }

    [AvaloniaFact]
    public async Task Enter_on_directory_navigates()
    {
        _fs.AddFile("/d/sub/inner.txt");
        _fs.AddFile("/d/z.txt");
        await _fs.CreateSymlinkAsync(Mem("/d/link"), "sub", CancellationToken.None);
        var panel = CreatePanel();
        await panel.NavigateToAsync(Mem("/d"));

        PutCursorOn(panel, "sub");
        await panel.ActivateAsync();

        Assert.Equal(Mem("/d/sub"), panel.CurrentPath);
        Assert.Equal(["..", "inner.txt"], Names(panel));

        // a link to a directory shows its own name and opens like a directory
        await panel.NavigateToAsync(Mem("/d"));
        Assert.Equal(["..", "link", "sub", "z.txt"], Names(panel));
        Assert.Equal("Folder", panel.Rows[1].SizeText);
        PutCursorOn(panel, "link");
        await panel.ActivateAsync();
        Assert.Equal(Mem("/d/link"), panel.CurrentPath);
        Assert.Equal(["..", "inner.txt"], Names(panel));
    }

    [AvaloniaFact]
    public async Task GoUp_places_cursor_on_previous_folder()
    {
        _fs.AddDirectory("/d/a");
        _fs.AddDirectory("/d/m/inner");
        _fs.AddDirectory("/d/z");
        var panel = CreatePanel();
        await panel.NavigateToAsync(Mem("/d/m/inner"));

        await panel.GoUpAsync();
        Assert.Equal(Mem("/d/m"), panel.CurrentPath);
        Assert.Equal("inner", panel.CursorRow?.Name);

        // Enter on ".." goes up the same way
        panel.CursorIndex = 0;
        Assert.True(panel.CursorRow?.IsParentLink);
        await panel.ActivateAsync();
        Assert.Equal(Mem("/d"), panel.CurrentPath);
        Assert.Equal("m", panel.CursorRow?.Name);
    }

    [AvaloniaFact]
    public async Task Access_denied_keeps_previous_listing_and_sets_error()
    {
        _fs.AddFile("/d/a.txt");
        _fs.AddDirectory("/d/sub");
        _fs.AddDirectory("/locked");
        _fs.FailOn((op, p) => op == "Enumerate" && p.Path == "/locked",
            () => new VfsAccessDeniedException("Permission denied"));
        var panel = CreatePanel();
        await panel.NavigateToAsync(Mem("/d"));
        PutCursorOn(panel, "a.txt");
        var navigated = 0;
        panel.Navigated += (_, _) => navigated++;

        await panel.NavigateToAsync(Mem("/locked"));

        Assert.Equal(Mem("/d"), panel.CurrentPath);
        Assert.Equal(["..", "sub", "a.txt"], Names(panel));
        Assert.Equal("a.txt", panel.CursorRow?.Name);
        Assert.Equal(PanelErrorKind.AccessDenied, panel.ErrorKind);
        Assert.Equal("Access to the folder /locked is denied", panel.Error);
        Assert.Equal(0, navigated);
        Assert.False(panel.IsLoading);

        await panel.NavigateToAsync(Mem("/missing"));
        Assert.Equal(PanelErrorKind.NotFound, panel.ErrorKind);
        Assert.Equal("The folder /missing does not exist", panel.Error);

        await panel.NavigateToAsync(Mem("/d/sub"));
        Assert.Null(panel.Error);
        Assert.Null(panel.ErrorKind);
        Assert.Equal(Mem("/d/sub"), panel.CurrentPath);
    }

    [AvaloniaFact]
    public async Task Sort_change_keeps_cursor_on_same_name()
    {
        _fs.AddFile("/d/a.txt", new string('x', 30));
        _fs.AddFile("/d/b.txt", new string('x', 10));
        _fs.AddFile("/d/c.txt", new string('x', 20));
        var panel = CreatePanel();
        await panel.NavigateToAsync(Mem("/d"));
        PutCursorOn(panel, "b.txt");
        Assert.Equal(2, panel.CursorIndex);

        panel.Sort = new SortSpec(SortColumn.Size, Descending: true);

        await WaitUntil(() => panel.Rows[1].Name == "a.txt" && !panel.IsLoading);
        Assert.Equal(["..", "a.txt", "c.txt", "b.txt"], Names(panel));
        Assert.Equal("b.txt", panel.CursorRow?.Name);
        Assert.Equal(3, panel.CursorIndex);
    }

    [AvaloniaFact]
    public async Task Refresh_keeps_cursor_and_selection_by_name()
    {
        _fs.AddFile("/d/b.txt");
        _fs.AddFile("/d/c.txt");
        _fs.AddFile("/d/d.txt");
        var panel = CreatePanel();
        await panel.NavigateToAsync(Mem("/d"));
        PutCursorOn(panel, "c.txt");
        panel.Rows.Single(r => r.Name == "d.txt").IsSelected = true;

        _fs.AddFile("/d/a.txt");
        await _fs.DeleteAsync(Mem("/d/b.txt"), recursive: false, CancellationToken.None);
        await panel.RefreshAsync();

        Assert.Equal(["..", "a.txt", "c.txt", "d.txt"], Names(panel));
        Assert.Equal("c.txt", panel.CursorRow?.Name);
        Assert.Equal(["d.txt"], panel.Rows.Where(r => r.IsSelected).Select(r => r.Name));
    }

    [AvaloniaFact]
    public async Task Watch_change_triggers_refresh()
    {
        _fs.AddFile("/d/a.txt");
        _fs.AddDirectory("/other");
        var panel = CreatePanel();
        await panel.NavigateToAsync(Mem("/d"));

        _fs.AddFile("/d/new.txt");
        await Task.Run(() => _fs.RaiseChanged("/d")); // providers call back on a background thread

        await WaitUntil(() => panel.Rows.Any(r => r.Name == "new.txt"));
        Assert.Equal(Mem("/d"), panel.CurrentPath);

        // after leaving the folder, a late callback of its (already disposed) subscription is ignored:
        // it must not refresh the folder shown now
        await panel.NavigateToAsync(Mem("/other"));
        var oldCallback = _observed.WatchCallbacks.First(w => w.Directory == Mem("/d")).Changed;
        var readsOfOther = _observed.ReadsOf("/other");
        var readsOfD = _observed.ReadsOf("/d");
        await Task.Run(oldCallback);
        await Task.Delay(300);
        Assert.Equal(Mem("/other"), panel.CurrentPath);
        Assert.Equal(readsOfOther, _observed.ReadsOf("/other"));
        Assert.Equal(readsOfD, _observed.ReadsOf("/d"));
    }

    [AvaloniaFact]
    public async Task Change_during_read_is_not_lost()
    {
        _fs.AddFile("/slow/s.txt");
        var readDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _observed.AfterRead = dir =>
        {
            if (dir.Path != "/slow" || readDone.Task.IsCompleted) return Task.CompletedTask;
            readDone.SetResult();
            return release.Task;
        };
        var panel = CreatePanel();

        var navigation = panel.NavigateToAsync(Mem("/slow"));
        await readDone.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // the entries are already read; the folder changes before the navigation has finished
        _fs.AddFile("/slow/new.txt");
        await Task.Run(() => _fs.RaiseChanged("/slow"));
        await Task.Delay(50); // the posted callback reaches the panel while the read is still held
        release.SetResult();
        await navigation;

        await WaitUntil(() => panel.Rows.Any(r => r.Name == "new.txt"));
        Assert.Equal(Mem("/slow"), panel.CurrentPath);
    }

    [AvaloniaFact]
    public async Task Dispose_during_load_publishes_nothing_and_releases_watch()
    {
        _fs.AddFile("/d/a.txt");
        _fs.AddFile("/slow/s.txt");
        var readDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _observed.AfterRead = dir =>
        {
            if (dir.Path != "/slow") return Task.CompletedTask;
            readDone.TrySetResult();
            return release.Task;
        };
        var panel = CreatePanel();
        await panel.NavigateToAsync(Mem("/d"));
        var navigated = 0;
        panel.Navigated += (_, _) => navigated++;
        var resets = 0;
        ((INotifyCollectionChanged)panel.Rows).CollectionChanged += (_, _) => resets++;

        var navigation = panel.NavigateToAsync(Mem("/slow"));
        await readDone.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, _observed.ActiveWatches); // /d and the in-flight /slow
        panel.Dispose();
        release.SetResult();
        await navigation;

        Assert.Equal(0, resets);
        Assert.Equal(0, navigated);
        Assert.Equal(Mem("/d"), panel.CurrentPath);
        Assert.Equal(["..", "a.txt"], Names(panel));
        await WaitUntil(() => _observed.ActiveWatches == 0);
    }

    [AvaloniaFact]
    public async Task Vanished_directory_falls_back_to_nearest_ancestor()
    {
        _fs.AddDirectory("/a/b/c");
        _fs.AddFile("/a/keep.txt");
        var panel = CreatePanel();
        await panel.NavigateToAsync(Mem("/a/b/c"));

        await _fs.DeleteAsync(Mem("/a/b"), recursive: true, CancellationToken.None);
        await Task.Run(() => _fs.RaiseChanged("/a/b/c"));

        await WaitUntil(() => panel.CurrentPath == Mem("/a") && !panel.IsLoading);
        Assert.Equal(["..", "keep.txt"], Names(panel));
        Assert.Null(panel.Error);
    }

    [AvaloniaFact]
    public async Task Newer_navigation_cancels_older()
    {
        _fs.AddFile("/slow/s.txt");
        _fs.AddFile("/fast/f.txt");
        using var gate = new ManualResetEventSlim();
        _fs.FailOn((op, p) =>
        {
            if (op == "Enumerate" && p.Path == "/slow") gate.Wait(TimeSpan.FromSeconds(10));
            return false;
        }, () => new IOException("unused"));
        var panel = CreatePanel();
        var navigated = new List<VfsPath>();
        panel.Navigated += (_, path) => navigated.Add(path);

        var slow = panel.NavigateToAsync(Mem("/slow"));
        await Task.Delay(50);
        Assert.True(panel.IsLoading);
        await panel.NavigateToAsync(Mem("/fast"));

        Assert.Equal(Mem("/fast"), panel.CurrentPath);
        Assert.Equal(["..", "f.txt"], Names(panel));

        gate.Set();
        await slow;
        Assert.Equal(Mem("/fast"), panel.CurrentPath);
        Assert.Equal(["..", "f.txt"], Names(panel));
        Assert.Equal([Mem("/fast")], navigated);
        Assert.False(panel.IsLoading);
        Assert.Null(panel.Error);
    }

    [AvaloniaFact]
    public async Task Loads_100k_entries_fast()
    {
        const int count = 100_000;
        for (var i = 0; i < count; i++) _fs.AddFile($"/big/file{count - i}.txt");
        for (var i = 0; i < 2000; i++) _fs.AddFile($"/warm/w{i}.txt");
        var panel = CreatePanel();
        await panel.NavigateToAsync(Mem("/warm")); // warm-up: JIT and first-time costs stay out of the timing

        var resets = 0;
        var otherChanges = 0;
        long firstRowsAt = -1;
        var clock = new Stopwatch();
        ((INotifyCollectionChanged)panel.Rows).CollectionChanged += (_, e) =>
        {
            if (e.Action != NotifyCollectionChangedAction.Reset) otherChanges++;
            else resets++;
            if (firstRowsAt < 0 && panel.Rows.Count > 0 && panel.Rows.Any(r => r.Name.StartsWith("file")))
                firstRowsAt = clock.ElapsedMilliseconds;
        };

        clock.Start();
        await panel.NavigateToAsync(Mem("/big"));
        var fullAt = clock.ElapsedMilliseconds;

        Assert.Equal(count + 1, panel.Rows.Count);
        Assert.Equal("file1.txt", panel.Rows[1].Name);
        Assert.Equal($"file{count}.txt", panel.Rows[^1].Name);
        Assert.InRange(firstRowsAt, 0, 999);
        Assert.InRange(fullAt, 0, 2999);
        Assert.InRange(resets, 1, 3);
        Assert.Equal(0, otherChanges);
    }

    [AvaloniaFact]
    public async Task Enter_on_file_raises_FileActivated()
    {
        _fs.AddFile("/d/a.txt");
        var panel = CreatePanel();
        await panel.NavigateToAsync(Mem("/d"));
        var activated = new List<FileEntry>();
        panel.FileActivated += (_, entry) => activated.Add(entry);

        PutCursorOn(panel, "a.txt");
        await panel.ActivateAsync();

        Assert.Equal(Mem("/d/a.txt"), Assert.Single(activated).Path);
        Assert.Equal(Mem("/d"), panel.CurrentPath);
    }
}
