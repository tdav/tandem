using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Tandem.App.Commands;
using Tandem.App.Tests.Views;
using Tandem.App.ViewModels;
using Tandem.Core.Search;
using Tandem.Core.Vfs;
using Tandem.Core.Vfs.InMemory;

namespace Tandem.App.Tests.ViewModels;

/// <summary>Panels whose folder lists entries of another provider: a search result set made with
/// <see cref="MainViewModel.SearchResults"/>, on the whole app over the in-memory "local disk". The set searched /proj
/// and found /proj/a/a.txt, /proj/b/a.txt, /proj/b/c.txt and the folder /proj/b/sub; the left panel shows it, the right
/// one /dst.</summary>
[Collection(LanguageCollection.Name)]
public class ForeignEntriesTests : LanguageTestBase
{
    private static readonly VfsPath[] Found =
    [
        VfsPath.Local("/proj/a/a.txt"), VfsPath.Local("/proj/b/a.txt"), VfsPath.Local("/proj/b/c.txt"),
        VfsPath.Local("/proj/b/sub"),
    ];

    private static InMemoryFileSystem FileSystem()
    {
        var fs = TestApp.DefaultFileSystem();
        fs.AddFile("/proj/a/a.txt", "first");
        fs.AddFile("/proj/b/a.txt", "second");
        fs.AddFile("/proj/b/c.txt", "third");
        fs.AddDirectory("/proj/b/sub");
        fs.AddDirectory("/dst");
        return fs;
    }

    private static async Task<TestApp> StartAsync(bool trashCapable = false, bool show = false,
        ProviderCapabilities removed = ProviderCapabilities.None,
        IReadOnlyList<IFileSystemProvider>? extraProviders = null, IReadOnlyList<VfsPath>? found = null)
    {
        var app = await TestApp.StartAsync(FileSystem(), show: show, trashCapable: trashCapable,
            removedCapabilities: removed, extraProviders: extraProviders);
        var set = app.Main.SearchResults.Add(VfsPath.Local("/proj"), "Search in /proj: *", found ?? Found);
        await app.Left.NavigateToAsync(set);
        await app.Right.NavigateToAsync(VfsPath.Local("/dst"));
        return app;
    }

    private static int IndexOf(PanelViewModel panel, VfsPath path) =>
        panel.Rows.ToList().FindIndex(r => r.Entry?.Path == path);

    private static void Cursor(PanelViewModel panel, string path)
    {
        var index = IndexOf(panel, VfsPath.Local(path));
        Assert.True(index >= 0, $"no row {path}");
        panel.CursorIndex = index;
    }

    private static void Mark(PanelViewModel panel, params string[] paths)
    {
        foreach (var path in paths) panel.Rows[IndexOf(panel, VfsPath.Local(path))].IsSelected = true;
    }

    private static VfsPath[] Listed(PanelViewModel panel) =>
        panel.Rows.Where(r => !r.IsParentLink).Select(r => r.Entry!.Path).ToArray();

    private static async Task<bool> Run(TestApp app, string id)
    {
        var ran = await app.Main.Commands.ExecuteAsync(id);
        await app.Main.Files.WhenIdleAsync();
        return ran;
    }

    [AvaloniaFact]
    public async Task Results_panel_shows_each_entrys_folder_next_to_its_name()
    {
        await using var app = await StartAsync(show: true);

        Assert.Equal("Search in /proj: *", app.Left.DisplayPath);
        Assert.True(app.Left.Rows[0].IsParentLink);
        Assert.Equal("", app.Left.Rows[0].LocationText);
        Assert.Equal(["/proj/a", "/proj/b", "/proj/b", "/proj/b"],
            Found.Select(p => app.Left.Rows[IndexOf(app.Left, p)].LocationText));
        Assert.Equal(2, app.Left.Rows.Count(r => r.Name == "a.txt"));

        // An ordinary folder's rows show no folder.
        await app.Right.NavigateToAsync(VfsPath.Local("/proj/b"));
        Assert.All(app.Right.Rows, r => Assert.Equal("", r.LocationText));

        app.Pump();
        var texts = app.FileList(PanelSide.Left).GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.Name == "LocationText" && t.IsEffectivelyVisible).Select(t => t.Text).ToList();
        Assert.Contains("/proj/a", texts);
        Assert.Contains("/proj/b", texts);
        Assert.DoesNotContain(app.FileList(PanelSide.Right).GetVisualDescendants().OfType<TextBlock>(),
            t => t.Name == "LocationText" && t.IsEffectivelyVisible);
        Assert.Empty(app.Failures);
    }

    [AvaloniaFact]
    public async Task F5_from_results_copies_real_files_to_the_other_panel()
    {
        await using var app = await StartAsync();
        Mark(app.Left, "/proj/a/a.txt", "/proj/b/c.txt");
        app.Dialogs.AnswerCopyMove();

        Assert.True(app.Main.Commands.CanExecute(CommandIds.FileCopy));
        Assert.True(await Run(app, CommandIds.FileCopy));

        Assert.Equal("/dst", Assert.Single(app.Dialogs.CopyMoveRequests).Destination);
        Assert.Equal("first", app.Fs.ReadAllText("/dst/a.txt"));
        Assert.Equal("third", app.Fs.ReadAllText("/dst/c.txt"));
        Assert.True(app.Fs.Exists("/proj/a/a.txt"));
        Assert.Contains(app.Right.Rows, r => r.Name == "c.txt");
        Assert.Equal(Found.Length, Listed(app.Left).Length);

        // A relative destination starts at the folder of the (first) item, not at the result set.
        Cursor(app.Left, "/proj/b/c.txt");
        app.Left.ClearSelection();
        app.Dialogs.AnswerCopyMove("sub");
        await Run(app, CommandIds.FileCopy);
        Assert.Equal("third", app.Fs.ReadAllText("/proj/b/sub/c.txt"));
        Assert.Empty(app.Dialogs.Messages);
        Assert.Empty(app.Failures);
    }

    [AvaloniaFact]
    public async Task F8_from_results_moves_real_files_to_trash_and_the_list_shrinks()
    {
        await using var app = await StartAsync(trashCapable: true);
        Mark(app.Left, "/proj/a/a.txt", "/proj/b/a.txt");
        app.Dialogs.DeleteAnswers.Enqueue(true);

        Assert.True(app.Main.Commands.CanExecute(CommandIds.FileDelete));
        Assert.True(await Run(app, CommandIds.FileDelete));

        Assert.Equal((2, (string?)null, false), Assert.Single(app.Dialogs.DeleteRequests));
        Assert.Equal(["/proj/a/a.txt", "/proj/b/a.txt"], app.Trash.Items);
        Assert.False(app.Fs.Exists("/proj/b/a.txt"));
        Assert.Equal(["/proj/b/c.txt", "/proj/b/sub"], Listed(app.Left).Select(p => p.Path).Order());
        Assert.Equal(0, app.Left.SelectedCount);
        Assert.Empty(app.Failures);
    }

    [AvaloniaFact]
    public async Task F6_and_rename_are_available_by_the_entries_provider()
    {
        var readOnly = new InMemoryFileSystem("ro");
        readOnly.AddFile("/r/locked.txt", "locked");
        var roProvider = new CapabilityProvider(readOnly, ProviderCapabilities.None, ProviderCapabilities.Write);
        await using var app = await StartAsync(extraProviders: [roProvider],
            found: [.. Found, new VfsPath("ro", "/r/locked.txt")]);
        var locked = IndexOf(app.Left, new VfsPath("ro", "/r/locked.txt"));
        Assert.Equal("/r", app.Left.Rows[locked].LocationText);

        Cursor(app.Left, "/proj/b/c.txt");
        Assert.True(app.Main.Commands.CanExecute(CommandIds.FileMove));
        Assert.True(app.Main.Commands.CanExecute(CommandIds.FileRename));
        Assert.True(app.Main.Commands.CanExecute(CommandIds.FileDelete));
        Assert.True(app.Main.Commands.CanExecute(CommandIds.FileDeletePermanent));

        // Every target must be writable; copying needs only a writable destination.
        Mark(app.Left, "/proj/b/c.txt");
        app.Left.Rows[locked].IsSelected = true;
        Assert.False(app.Main.Commands.CanExecute(CommandIds.FileMove));
        Assert.False(app.Main.Commands.CanExecute(CommandIds.FileDelete));
        Assert.False(app.Main.Commands.CanExecute(CommandIds.FileDeletePermanent));
        Assert.True(app.Main.Commands.CanExecute(CommandIds.FileCopy));
        app.Left.ClearSelection();
        app.Left.CursorIndex = locked;
        Assert.False(app.Main.Commands.CanExecute(CommandIds.FileRename));

        // F6 moves the real file; the result set no longer finds it.
        Cursor(app.Left, "/proj/b/c.txt");
        app.Dialogs.AnswerCopyMove();
        Assert.True(await Run(app, CommandIds.FileMove));
        Assert.Equal("third", app.Fs.ReadAllText("/dst/c.txt"));
        Assert.False(app.Fs.Exists("/proj/b/c.txt"));
        Assert.Equal(-1, IndexOf(app.Left, VfsPath.Local("/proj/b/c.txt")));

        // Rename works in the item's own folder.
        Cursor(app.Left, "/proj/a/a.txt");
        await Run(app, CommandIds.FileRename);
        var editor = app.Main.Files.Rename!;
        editor.Text = "renamed.txt";
        Assert.True(await editor.CommitAsync());
        await app.Main.Files.WhenIdleAsync();
        Assert.Equal("first", app.Fs.ReadAllText("/proj/a/renamed.txt"));
        Assert.False(app.Fs.Exists("/proj/a/a.txt"));
        Assert.Empty(app.Dialogs.Messages);
        Assert.Empty(app.Failures);
    }

    [AvaloniaFact]
    public async Task F7_and_F5_into_results_are_unavailable()
    {
        await using var app = await StartAsync();
        Cursor(app.Left, "/proj/b/c.txt");

        Assert.False(app.Main.Commands.CanExecute(CommandIds.FileMkdir));
        Assert.False(await Run(app, CommandIds.FileMkdir));

        // The results are the passive panel now: nothing can be copied or moved into them.
        await app.Right.NavigateToAsync(VfsPath.Local("/home"));
        app.Main.Workspace.ActiveSide = PanelSide.Right;
        app.Right.CursorIndex = app.Right.Rows.ToList().FindIndex(r => r.Name == "notes.txt");
        Assert.False(app.Main.Commands.CanExecute(CommandIds.FileCopy));
        Assert.False(app.Main.Commands.CanExecute(CommandIds.FileMove));
        Assert.False(await Run(app, CommandIds.FileCopy));
        Assert.Empty(app.Dialogs.Log);

        // Nor dropped onto the set itself; a folder found in it takes drops.
        Assert.False(app.Main.Drops.CanDrop(["/home/notes.txt"], PanelSide.Left, app.Left.CurrentPath));
        Assert.True(app.Main.Drops.CanDrop(["/home/notes.txt"], PanelSide.Left, VfsPath.Local("/proj/b/sub")));
    }

    [AvaloniaFact]
    public async Task Enter_on_a_folder_result_goes_to_the_real_folder()
    {
        await using var app = await StartAsync();
        Cursor(app.Left, "/proj/b/sub");

        await app.Left.ActivateAsync();

        Assert.Equal(VfsPath.Local("/proj/b/sub"), app.Left.CurrentPath);
        Assert.Equal("/proj/b/sub", app.Left.DisplayPath);
        Assert.Empty(app.Shell.Calls);
    }

    [AvaloniaFact]
    public async Task Up_from_results_returns_to_the_searched_folder()
    {
        await using var app = await StartAsync();

        await app.Left.GoUpAsync();
        Assert.Equal(VfsPath.Local("/proj"), app.Left.CurrentPath);

        // The ".." row goes the same way.
        await app.Left.NavigateToAsync(new VfsPath(SearchResultsProvider.ProviderId, "/1"));
        app.Left.CursorIndex = 0;
        await app.Left.ActivateAsync();
        Assert.Equal(VfsPath.Local("/proj"), app.Left.CurrentPath);
    }

    [AvaloniaFact]
    public async Task Cursor_and_marks_survive_refresh_when_names_repeat()
    {
        await using var app = await StartAsync();
        var second = app.Left.Rows.Select((r, i) => (r, i)).Where(x => x.r.Name == "a.txt").Skip(1).First();
        var path = second.r.Entry!.Path;
        app.Left.CursorIndex = second.i;
        second.r.IsSelected = true;

        await app.Left.RefreshAsync();

        Assert.Equal(path, app.Left.CursorRow?.Entry?.Path);
        Assert.Equal([path], app.Left.GetOperationTargets().Select(e => e.Path));
        Assert.Equal(1, app.Left.SelectedCount);
    }

    [AvaloniaFact]
    public async Task Reveal_and_open_work_on_a_result()
    {
        await using var app = await StartAsync();
        Cursor(app.Left, "/proj/a/a.txt");

        Assert.True(await app.Main.Commands.ExecuteAsync(CommandIds.ShellReveal));
        await app.Left.ActivateAsync();
        Assert.True(app.Main.Commands.CanExecute(CommandIds.ShellOpenWith));
        Assert.True(await app.Main.Commands.ExecuteAsync(CommandIds.FileQuickLook));
        app.Pump();

        Assert.Equal([("reveal", "/proj/a/a.txt"), ("open", "/proj/a/a.txt")], app.Shell.Calls);
        Assert.Equal([["/proj/a/a.txt"]], app.QuickLook.Toggles);
        // A terminal opens in the panel's folder, which is not on the disk.
        Assert.False(app.Main.Commands.CanExecute(CommandIds.ShellOpenTerminal));
        Assert.True(app.Main.Commands.CanExecute(CommandIds.FileProperties));

        // A drag out of the results carries the real files.
        Mark(app.Left, "/proj/a/a.txt", "/proj/b/c.txt");
        Assert.Equal(["/proj/a/a.txt", "/proj/b/c.txt"],
            DropHandler.DragItems(app.Left, app.Left.CursorIndex).Select(e => e.Path.Path).Order());
        Assert.Empty(app.Dialogs.Messages);
        Assert.Empty(app.Failures);
    }

    [AvaloniaFact]
    public async Task Results_tab_is_saved_as_the_searched_folder()
    {
        await using var app = await StartAsync();

        var tab = app.Main.Workspace.Left.ToState().Tabs.Single();

        Assert.Equal((VfsPath.LocalProviderId, "/proj"), (tab.ProviderId, tab.Path));
    }

    [AvaloniaFact]
    public async Task Panel_on_a_dropped_result_set_ends_at_the_searched_folder()
    {
        await using var app = await StartAsync();
        for (var i = 0; i < SearchResultsProvider.MaxResultSets; i++)
            app.Main.SearchResults.Add(VfsPath.Local("/"), $"set {i}", []);

        await app.Left.RefreshAsync();

        Assert.Null(app.Left.Error);
        Assert.Equal(VfsPath.Local("/proj"), app.Left.CurrentPath);
        Assert.Empty(app.Failures);
    }

    [AvaloniaFact]
    public async Task Drop_on_the_parent_row_of_results_targets_the_searched_folder()
    {
        await using var app = await StartAsync();

        Assert.True(app.Left.Rows[0].IsParentLink);
        Assert.Equal(new DropTarget(VfsPath.Local("/proj"), 0), DropHandler.TargetAt(app.Left, 0));
    }

    [AvaloniaFact]
    public async Task F8_on_targets_where_one_provider_has_no_trash_asks_for_permanent_deletion()
    {
        var other = new InMemoryFileSystem("mem2");
        other.AddFile("/m/other.txt", "other");
        await using var app = await StartAsync(trashCapable: true, extraProviders: [other],
            found: [.. Found, new VfsPath("mem2", "/m/other.txt")]);
        Mark(app.Left, "/proj/a/a.txt");
        app.Left.Rows[IndexOf(app.Left, new VfsPath("mem2", "/m/other.txt"))].IsSelected = true;
        app.Dialogs.DeleteAnswers.Enqueue(true);

        Assert.True(await Run(app, CommandIds.FileDelete));

        Assert.Equal((2, (string?)null, true), Assert.Single(app.Dialogs.DeleteRequests));
        Assert.Empty(app.Trash.Items);
        Assert.False(app.Fs.Exists("/proj/a/a.txt"));
        Assert.False(other.Exists("/m/other.txt"));
        Assert.Empty(app.Failures);
    }

    [AvaloniaFact]
    public async Task Quick_look_is_unavailable_on_a_result_that_is_not_local()
    {
        var other = new InMemoryFileSystem("mem2");
        other.AddFile("/m/other.txt", "other");
        await using var app = await StartAsync(extraProviders: [other],
            found: [.. Found, new VfsPath("mem2", "/m/other.txt")]);

        app.Left.CursorIndex = IndexOf(app.Left, new VfsPath("mem2", "/m/other.txt"));
        Assert.False(app.Main.Commands.CanExecute(CommandIds.FileQuickLook));
        Cursor(app.Left, "/proj/a/a.txt");
        Assert.True(app.Main.Commands.CanExecute(CommandIds.FileQuickLook));
    }
}
