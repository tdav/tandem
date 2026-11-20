using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tandem.App.Commands;
using Tandem.App.Tests.ViewModels;
using Tandem.App.ViewModels;
using Tandem.App.ViewModels.Search;
using Tandem.App.Views.Search;
using Tandem.Core.Search;
using Tandem.Core.Vfs;
using Tandem.Core.Vfs.InMemory;

namespace Tandem.App.Tests.Views;

/// <summary>The file search window (Alt+F7, Cmd+F) through the real windows: opened for the active folder, one window
/// brought forward again, its keys, its result list, and closed with the app.</summary>
[Collection(LanguageCollection.Name)]
public class SearchWindowTests : LanguageTestBase
{
    /// <summary>The default file system plus /home/docs/deep.txt.</summary>
    private static InMemoryFileSystem FileSystem()
    {
        var fs = TestApp.DefaultFileSystem();
        fs.AddFile(TestApp.Home + "/docs/deep.txt", "deep");
        return fs;
    }

    /// <summary>Holds the walk when it starts reading <paramref name="folder"/> until the gate is set (only the walk,
    /// which runs off the UI thread, waits).</summary>
    private static Gate HoldAt(InMemoryFileSystem fs, string folder)
    {
        var gate = new Gate();
        fs.FailOn((op, path) =>
        {
            if (op == "Enumerate" && path.Path == folder && !Dispatcher.UIThread.CheckAccess()) gate.Wait();
            return false;
        }, () => new InvalidOperationException());
        return gate;
    }

    private static SearchWindow? SearchWindowOf(TestApp app) => app.Windows.Open.OfType<SearchWindow>().SingleOrDefault();

    private static void Press(Window window, Key key, PhysicalKey physical,
        RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPress(key, modifiers, physical, null);
        if (window.IsVisible)
        {
            // The key may close the window, also later: from a job that the release itself runs first.
            try { window.KeyRelease(key, modifiers, physical, null); }
            catch (ObjectDisposedException) when (!window.IsVisible) { }
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static T Named<T>(Window window, string name) where T : Control =>
        window.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    [AvaloniaFact]
    public async Task Cmd_F_opens_search_for_the_active_folder()
    {
        await using var app = await TestApp.StartAsync(FileSystem());

        app.Press(Key.F, RawInputModifiers.Meta, PhysicalKey.F);

        var window = SearchWindowOf(app);
        Assert.NotNull(window);
        Assert.Equal("/home", window.Search.RootText);
        Assert.Equal(VfsPath.Local("/home"), window.Search.Root);
        Assert.Equal("/home", Named<TextBlock>(window, "RootText").Text);
        window.Search.NameMasks = "*.txt";

        // Again from another folder: the same window, the new folder, the same criteria. (The headless platform has
        // one keyboard focus for all windows, which the search window holds now, so the command runs directly.)
        await app.Left.NavigateToAsync(VfsPath.Local("/home/docs"));
        Assert.True(await app.Main.Commands.ExecuteAsync(CommandIds.SearchFiles));
        app.Pump();

        Assert.Same(window, SearchWindowOf(app));
        Assert.True(window.IsVisible);
        Assert.Equal("/home/docs", window.Search.RootText);
        Assert.Equal("*.txt", window.Search.NameMasks);
        Assert.Equal("/home/docs", Named<TextBlock>(window, "RootText").Text);

        // The same folder again: the same model, results kept.
        await window.Search.StartAsync();
        var search = window.Search;
        Assert.True(await app.Main.Commands.ExecuteAsync(CommandIds.SearchFiles));
        Assert.Same(search, window.Search);
        Assert.Single(window.Search.Results);
        Assert.Empty(app.Failures);
    }

    [AvaloniaFact]
    public async Task Search_is_unavailable_on_a_non_local_panel()
    {
        var other = new InMemoryFileSystem("mem2");
        other.AddDirectory("/m");
        await using var app = await TestApp.StartAsync(FileSystem(), show: false, extraProviders: [other]);
        Assert.True(app.Main.Commands.CanExecute(CommandIds.SearchFiles));
        var seen = new List<bool>(); // the availability each change announcement finds
        app.Main.Commands.CanExecuteChanged +=
            (_, _) => seen.Add(app.Main.Commands.CanExecute(CommandIds.SearchFiles));

        await app.Left.NavigateToAsync(new VfsPath("mem2", "/m"));
        Assert.False(app.Main.Commands.CanExecute(CommandIds.SearchFiles));
        Assert.Contains(false, seen);
        Assert.True(app.Main.RunCommand.CanExecute(CommandIds.SearchFiles) is false);
        seen.Clear();
        await app.Left.NavigateToAsync(VfsPath.Local("/home/docs"));
        Assert.Contains(true, seen);
        await app.Left.NavigateToAsync(new VfsPath("mem2", "/m"));
        Assert.False(await app.Main.Commands.ExecuteAsync(CommandIds.SearchFiles));

        // A result set is not a local folder either.
        var set = app.Main.SearchResults.Add(VfsPath.Local("/home"), "Search: * in /home", []);
        await app.Left.NavigateToAsync(set);
        Assert.False(app.Main.Commands.CanExecute(CommandIds.SearchFiles));

        await app.Left.NavigateToAsync(VfsPath.Local("/home"));
        Assert.True(app.Main.Commands.CanExecute(CommandIds.SearchFiles));
        Assert.Empty(app.Windows.Open);
    }

    [AvaloniaFact]
    public async Task Esc_stops_running_search_then_closes()
    {
        var fs = FileSystem();
        using var gate = HoldAt(fs, "/home/src");
        await using var app = await TestApp.StartAsync(fs);
        try
        {
            app.Press(Key.F7, RawInputModifiers.Alt, PhysicalKey.F7);
            var window = SearchWindowOf(app)!;
            var search = window.Search;
            var run = search.StartAsync();
            Assert.True(search.IsRunning);

            Press(window, Key.Escape, PhysicalKey.Escape);

            Assert.False(search.IsRunning);
            Assert.StartsWith("Stopped", search.StatusText);
            Assert.True(window.IsVisible);

            Press(window, Key.Escape, PhysicalKey.Escape);

            Assert.False(window.IsVisible);
            Assert.Null(SearchWindowOf(app));
            Assert.True(search.IsDisposed);
            gate.Set();
            await run;
        }
        finally
        {
            gate.Set();
        }
    }

    [AvaloniaFact]
    public async Task Enter_in_a_field_starts_and_Enter_on_a_result_goes_to_it()
    {
        await using var app = await TestApp.StartAsync(FileSystem());
        app.Press(Key.F, RawInputModifiers.Meta, PhysicalKey.F);
        var window = SearchWindowOf(app)!;
        var search = window.Search;
        var names = Named<TextBox>(window, "NameMasksBox");
        Assert.True(names.IsFocused);
        names.Text = "deep.txt";

        Press(window, Key.Enter, PhysicalKey.Enter);
        await TestApp.WaitUntil(() =>
        {
            Dispatcher.UIThread.RunJobs();
            return !search.IsRunning && search.Results.Count == 1;
        });
        window.UpdateLayout();

        var list = Named<ListBox>(window, "ResultsList");
        var item = list.GetVisualDescendants().OfType<ListBoxItem>().Single();
        Assert.Contains(item.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "deep.txt");
        Assert.Contains(item.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "/home/docs");
        list.SelectedIndex = 0;
        window.Activate();
        Assert.True(item.Focus());
        Assert.False(names.IsFocused);
        Press(window, Key.Enter, PhysicalKey.Enter);
        await TestApp.WaitUntil(() =>
        {
            Dispatcher.UIThread.RunJobs();
            return !window.IsVisible;
        });

        Assert.Equal(VfsPath.Local("/home/docs"), app.Left.CurrentPath);
        Assert.Equal("deep.txt", app.Left.CursorRow?.Name);
        Assert.Empty(app.Failures);
    }

    [AvaloniaFact]
    public async Task Many_results_are_added_in_batches_and_the_list_shows_them()
    {
        var fs = FileSystem();
        for (var i = 0; i < 2000; i++) fs.AddFile($"/home/many/f{i:D4}.log");
        await using var app = await TestApp.StartAsync(fs);
        app.Press(Key.F, RawInputModifiers.Meta, PhysicalKey.F);
        var window = SearchWindowOf(app)!;
        var search = window.Search;
        search.NameMasks = "*.log";
        var notifications = 0;
        search.Results.CollectionChanged += (_, _) => notifications++;

        await search.StartAsync();
        window.UpdateLayout();

        Assert.Equal(2000, search.Results.Count);
        Assert.True(notifications < 2000, $"{notifications} notifications");
        var list = Named<ListBox>(window, "ResultsList");
        var shown = list.GetVisualDescendants().OfType<ListBoxItem>().Count();
        Assert.InRange(shown, 1, 200); // virtualized
    }

    [AvaloniaFact]
    public async Task Quitting_stops_a_running_search_and_closes_its_window_without_asking()
    {
        var fs = FileSystem();
        using var gate = HoldAt(fs, "/home/src");
        await using var app = await TestApp.StartAsync(fs);
        try
        {
            app.Press(Key.F, RawInputModifiers.Meta, PhysicalKey.F);
            var window = SearchWindowOf(app)!;
            var search = window.Search;
            var run = search.StartAsync();
            Assert.True(search.IsRunning);
            Assert.True(app.Main.HasOpenWindows);

            app.Window.Close();
            await TestApp.WaitUntil(() =>
            {
                app.Pump();
                return !app.Window.IsVisible;
            });

            Assert.False(window.IsVisible);
            Assert.False(search.IsRunning);
            Assert.True(search.IsDisposed);
            Assert.Empty(app.Windows.Open);
            Assert.Empty(app.Dialogs.SaveChangesRequests);
            Assert.Empty(app.Dialogs.QuitRequests);
            gate.Set();
            await run;
        }
        finally
        {
            gate.Set();
        }
    }

    [AvaloniaFact]
    public async Task Close_all_closes_the_search_window_and_stops_its_search()
    {
        var fs = FileSystem();
        using var gate = HoldAt(fs, "/home/src");
        await using var app = await TestApp.StartAsync(fs);
        try
        {
            app.Press(Key.F, RawInputModifiers.Meta, PhysicalKey.F);
            var search = SearchWindowOf(app)!.Search;
            _ = search.StartAsync();

            Assert.True(await app.Windows.CloseAllAsync());

            Assert.False(search.IsRunning);
            Assert.Empty(app.Windows.Open);
            Assert.False(app.Main.HasOpenWindows);
        }
        finally
        {
            gate.Set();
        }
    }

    [AvaloniaFact]
    public async Task Search_is_in_the_File_menu_with_Alt_F7()
    {
        await using var app = await TestApp.StartAsync(show: false);

        var menu = global::Tandem.App.Views.MainMenu.Build(app.Main);
        var file = menu.Items.OfType<NativeMenuItem>().First().Menu!;
        var item = file.Items.OfType<NativeMenuItem>().Single(i => i.CommandParameter as string == CommandIds.SearchFiles);

        Assert.Equal("Find Files…", item.Header);
        Assert.Equal(new KeyGesture(Key.F7, KeyModifiers.Alt), item.Gesture);
        Assert.Equal([KeyChord.Parse("Alt+F7"), KeyChord.Parse("Cmd+F")], KeyMap.Default.ChordsFor(CommandIds.SearchFiles));
    }
}
