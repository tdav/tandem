using Avalonia.Headless.XUnit;
using Tandem.App.Commands;
using Tandem.App.Resources;
using Tandem.App.Tests.Integration;
using Tandem.App.Tests.Views;
using Tandem.App.ViewModels;
using Tandem.Core.Settings;
using Tandem.Core.Vfs;
using Tandem.Core.Vfs.InMemory;
using Tandem.Net.Tests.Remote;

namespace Tandem.App.Tests.ViewModels;

/// <summary>"Copy Path" (Cmd+Alt+C, the row context menu): the full paths of the marked items, else of the cursor
/// item (".." stands for the folder shown), one per line, on the clipboard, with a notice in the status line. Items
/// that are not on the local disk are written as the path line writes them. Both panels start at /home (.., docs,
/// src, README.md, notes.txt, report.pdf).</summary>
[Collection(LanguageCollection.Name)]
public class CopyPathTests : LanguageTestBase
{
    private const string Empty = "empty";

    private static void Cursor(PanelViewModel panel, string name)
    {
        var index = panel.Rows.ToList().FindIndex(r => r.Name == name);
        Assert.True(index >= 0, $"no row {name}");
        panel.CursorIndex = index;
    }

    private static Task<TestApp> StartAsync(InMemoryFileSystem? fs = null, AppState? state = null) =>
        TestApp.StartAsync(fs, state: state, show: false, extraProviders: [new InMemoryFileSystem(Empty)]);

    [AvaloniaFact]
    public async Task Copies_the_cursor_file_path_and_tells_so()
    {
        await using var app = await StartAsync();
        Cursor(app.Left, "notes.txt");

        Assert.True(await app.Main.Commands.ExecuteAsync(CommandIds.FileCopyPath));

        Assert.Equal(["/home/notes.txt"], app.Clipboard.Texts);
        Assert.Equal("Path copied", app.Left.Notice);
        Assert.Equal("Path copied", app.Main.Left.StatusLine);
        Assert.Empty(app.Failures);
    }

    [AvaloniaFact]
    public async Task Parent_link_copies_the_folder_shown()
    {
        await using var app = await StartAsync();
        Cursor(app.Left, "..");

        Assert.True(app.Main.Commands.CanExecute(CommandIds.FileCopyPath));
        Assert.True(await app.Main.Commands.ExecuteAsync(CommandIds.FileCopyPath));

        Assert.Equal(["/home"], app.Clipboard.Texts);
    }

    [AvaloniaFact]
    public async Task Marked_items_are_copied_one_per_line_instead_of_the_cursor()
    {
        await using var app = await StartAsync();
        app.Left.SelectWhere(e => e.Name is "docs" or "notes.txt" or "report.pdf");
        Cursor(app.Left, "README.md");

        Assert.True(await app.Main.Commands.ExecuteAsync(CommandIds.FileCopyPath));

        Assert.Equal(["/home/docs\n/home/notes.txt\n/home/report.pdf"], app.Clipboard.Texts);
        Assert.Equal("3 paths copied", app.Left.Notice);
    }

    [AvaloniaFact]
    public async Task Notice_is_in_Russian_with_plural_forms()
    {
        await using var app = await StartAsync(state: AppState.Default(TestApp.Home) with { Language = "ru" });
        Cursor(app.Left, "notes.txt");

        await app.Main.Commands.ExecuteAsync(CommandIds.FileCopyPath);
        Assert.Equal("Путь скопирован", app.Left.Notice);

        app.Left.SelectWhere(e => e.Name is "docs" or "notes.txt");
        await app.Main.Commands.ExecuteAsync(CommandIds.FileCopyPath);
        Assert.Equal("Скопировано 2 пути", app.Left.Notice);

        app.Left.SelectWhere(e => e.Name is "docs" or "src" or "README.md" or "notes.txt" or "report.pdf");
        await app.Main.Commands.ExecuteAsync(CommandIds.FileCopyPath);
        Assert.Equal("Скопировано 5 путей", app.Left.Notice);
    }

    [AvaloniaFact]
    public async Task Archive_entry_is_copied_as_the_path_line_writes_it()
    {
        await using var app = await ArchiveApp.StartAsync();
        await ArchiveApp.EnterAsync(app, "sample.zip");
        await app.Left.NavigateToAsync(app.Left.CurrentPath with { Path = "/docs" });
        Assert.Equal("/home/sample.zip/docs", app.Left.DisplayPath);
        var file = app.Left.Rows.First(r => !r.IsParentLink && !r.Entry!.IsDirectoryLike).Name;
        ArchiveApp.Cursor(app, app.Left, file);

        Assert.True(await app.Main.Commands.ExecuteAsync(CommandIds.FileCopyPath));
        ArchiveApp.Cursor(app, app.Left, "..");
        Assert.True(await app.Main.Commands.ExecuteAsync(CommandIds.FileCopyPath));

        Assert.Equal(["/home/sample.zip/docs/" + file, "/home/sample.zip/docs"], app.Clipboard.Texts);
    }

    [AvaloniaFact]
    public async Task Server_entry_is_copied_as_its_address_and_path_without_a_password()
    {
        var server = new FakeRemoteServer();
        server.AddFile("/www/index.html", "<p>");
        await using var remote = ServerScenarioTests.Provider(server);
        await using var app = await TestApp.StartAsync(show: false, extraProviders: [remote]);
        await app.Left.NavigateToAsync(remote.Root with { Path = "/www" });
        Cursor(app.Left, "index.html");

        Assert.True(await app.Main.Commands.ExecuteAsync(CommandIds.FileCopyPath));

        Assert.Equal(["sftp://test@127.0.0.1/www/index.html"], app.Clipboard.Texts);
        Assert.Equal(app.Left.DisplayPath + "/index.html", app.Clipboard.Texts[0]);
    }

    [AvaloniaFact]
    public async Task Search_results_copy_the_real_paths_of_the_files_found()
    {
        await using var app = await StartAsync();
        await app.Left.NavigateToAsync(app.Main.SearchResults.Add(VfsPath.Local("/"), "found",
            [VfsPath.Local("/other/inside.txt"), VfsPath.Local("/home/notes.txt")]));
        app.Left.SelectWhere(_ => true);

        Assert.True(await app.Main.Commands.ExecuteAsync(CommandIds.FileCopyPath));

        var copied = Assert.Single(app.Clipboard.Texts).Split('\n').Order(StringComparer.Ordinal);
        Assert.Equal(["/home/notes.txt", "/other/inside.txt"], copied);
    }

    [AvaloniaFact]
    public async Task Nothing_to_copy_in_an_empty_panel()
    {
        await using var app = await StartAsync();
        await app.Left.NavigateToAsync(new VfsPath(Empty, "/"));
        Assert.Empty(app.Left.Rows);

        Assert.False(app.Main.Commands.CanExecute(CommandIds.FileCopyPath));
        Assert.False(await app.Main.Commands.ExecuteAsync(CommandIds.FileCopyPath));

        Assert.Empty(app.Clipboard.Texts);
        Assert.Null(app.Left.Notice);
    }

    [AvaloniaFact]
    public async Task Cmd_Alt_C_copies_the_path()
    {
        await using var app = await StartAsync();
        Cursor(app.Left, "README.md");

        Assert.True(await app.Main.HandleKeyAsync(KeyChord.Parse("Cmd+Alt+C")));

        Assert.Equal(["/home/README.md"], app.Clipboard.Texts);
    }

    [AvaloniaFact]
    public async Task Row_menu_lists_copy_path_after_reveal_with_its_chord_and_runs_it()
    {
        await using var app = await StartAsync();
        Cursor(app.Left, "notes.txt");

        var menu = app.Main.Left.ContextMenuForRow(app.Left.CursorIndex);
        var keys = menu.Select(i => i.LabelKey).ToList();

        var item = menu[keys.IndexOf("Context_Reveal") + 1];
        Assert.Equal("Context_CopyPath", item.LabelKey);
        Assert.Equal(CommandIds.FileCopyPath, item.CommandId);
        Assert.Equal(Loc.Get("Context_CopyPath"), item.Label);
        Assert.Equal("Copy Path", item.Label);
        Assert.Equal(KeyChord.Parse("Cmd+Alt+C"), item.Chord);
        Assert.True(item.IsEnabled);

        await item.ExecuteAsync();

        Assert.Equal(["/home/notes.txt"], app.Clipboard.Texts);
        Assert.Equal("Path copied", app.Left.Notice);
    }

    [AvaloniaFact]
    public async Task File_menu_has_copy_path_after_open_with()
    {
        await using var app = await StartAsync();
        var file = Tandem.App.Views.MainMenu.Build(app.Main).Items.OfType<Avalonia.Controls.NativeMenuItem>()
            .Single(i => i.Header == "File");
        var ids = file.Menu!.Items.Select(i => (i as Avalonia.Controls.NativeMenuItem)?.CommandParameter as string)
            .ToList();

        Assert.Equal(CommandIds.FileCopyPath, ids[ids.IndexOf(CommandIds.ShellOpenWith) + 1]);
        var item = (Avalonia.Controls.NativeMenuItem)file.Menu.Items[ids.IndexOf(CommandIds.FileCopyPath)];
        Assert.Equal("Copy Path", item.Header);
    }
}
