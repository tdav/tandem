using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Tandem.App.Commands;
using Tandem.App.Tests.Views;
using Tandem.App.ViewModels;
using Tandem.App.Views;
using Tandem.Core.Listing;
using Tandem.Core.Settings;
using Tandem.Core.Vfs.InMemory;

namespace Tandem.App.Tests.ViewModels;

// Start-up applies the saved language process-wide, so these tests run in the language collection.
[Collection(LanguageCollection.Name)]
public class SortCommandsTests : LanguageTestBase
{
    /// <summary>Home holds a folder and three files whose sizes are in another order than their names.</summary>
    private static InMemoryFileSystem SizedFileSystem()
    {
        var fs = TestApp.DefaultFileSystem();
        fs.AddFile(TestApp.Home + "/a.txt", new string('a', 300));
        fs.AddFile(TestApp.Home + "/b.txt", new string('b', 100));
        fs.AddFile(TestApp.Home + "/c.txt", new string('c', 200));
        return fs;
    }

    private static string[] Names(PanelViewModel panel, params string[] among) =>
        panel.Rows.Select(r => r.Name).Where(among.Contains).ToArray();

    private static Task Run(TestApp app, string id) => app.Main.Commands.ExecuteAsync(id);

    [AvaloniaFact]
    public async Task Sort_command_sorts_active_panel_by_that_column()
    {
        await using var app = await TestApp.StartAsync(SizedFileSystem(), show: false);

        Assert.True(await app.Main.Commands.ExecuteAsync(CommandIds.SortBySize));

        Assert.Equal(new SortSpec(SortColumn.Size), app.Left.Sort);
        Assert.Equal("▲", app.Main.Left.SizeSortMark);
        Assert.Equal("", app.Main.Left.NameSortMark);
        await TestApp.WaitUntil(() => Names(app.Left, "a.txt", "b.txt", "c.txt") is ["b.txt", "c.txt", "a.txt"]);

        await Run(app, CommandIds.SortByExtension);
        Assert.Equal(new SortSpec(SortColumn.Extension), app.Left.Sort);
        await Run(app, CommandIds.SortByDate);
        Assert.Equal(new SortSpec(SortColumn.Modified), app.Left.Sort);
        Assert.Equal("▲", app.Main.Left.DateSortMark);
        await Run(app, CommandIds.SortByMode);
        Assert.Equal(new SortSpec(SortColumn.Mode), app.Left.Sort);
        Assert.Equal("▲", app.Main.Left.ModeSortMark);
        Assert.Equal("", app.Main.Left.DateSortMark);
        await Run(app, CommandIds.SortByName);
        Assert.Equal(new SortSpec(SortColumn.Name), app.Left.Sort);
    }

    [AvaloniaFact]
    public async Task Sort_by_permissions_orders_rows_by_mode()
    {
        var fs = SizedFileSystem();
        await fs.SetModeAsync(Tandem.Core.Vfs.VfsPath.Local(TestApp.Home + "/a.txt"),
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, CancellationToken.None);
        await fs.SetModeAsync(Tandem.Core.Vfs.VfsPath.Local(TestApp.Home + "/b.txt"),
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead, CancellationToken.None);
        await fs.SetModeAsync(Tandem.Core.Vfs.VfsPath.Local(TestApp.Home + "/c.txt"), UnixFileMode.UserRead,
            CancellationToken.None);
        await using var app = await TestApp.StartAsync(fs, show: false);

        await app.Main.HandleKeyAsync(KeyChord.Parse("Ctrl+Cmd+5"));

        Assert.Equal(new SortSpec(SortColumn.Mode), app.Left.Sort);
        await TestApp.WaitUntil(() => Names(app.Left, "a.txt", "b.txt", "c.txt") is ["c.txt", "b.txt", "a.txt"]);
    }

    [AvaloniaFact]
    public async Task Sort_command_on_current_column_flips_direction()
    {
        await using var app = await TestApp.StartAsync(show: false);
        Assert.Equal(new SortSpec(SortColumn.Name), app.Left.Sort);

        await Run(app, CommandIds.SortByName);
        Assert.Equal(new SortSpec(SortColumn.Name, Descending: true), app.Left.Sort);
        Assert.Equal("▼", app.Main.Left.NameSortMark);

        await Run(app, CommandIds.SortByName);
        Assert.Equal(new SortSpec(SortColumn.Name), app.Left.Sort);

        // Another column starts ascending, whatever the direction was.
        await Run(app, CommandIds.SortByName);
        await Run(app, CommandIds.SortBySize);
        Assert.Equal(new SortSpec(SortColumn.Size), app.Left.Sort);
    }

    [AvaloniaFact]
    public async Task Sort_reverse_flips_direction_only()
    {
        await using var app = await TestApp.StartAsync(show: false);
        await Run(app, CommandIds.SortBySize);

        await Run(app, CommandIds.SortReverse);
        Assert.Equal(new SortSpec(SortColumn.Size, Descending: true), app.Left.Sort);
        Assert.Equal("▼", app.Main.Left.SizeSortMark);

        await Run(app, CommandIds.SortReverse);
        Assert.Equal(new SortSpec(SortColumn.Size), app.Left.Sort);
    }

    [AvaloniaFact]
    public async Task Sort_commands_do_not_touch_passive_panel()
    {
        await using var app = await TestApp.StartAsync(show: false);

        await Run(app, CommandIds.SortBySize);
        await Run(app, CommandIds.SortReverse);
        Assert.Equal(new SortSpec(SortColumn.Name), app.Right.Sort);

        app.Main.Workspace.SwitchPanel();
        await Run(app, CommandIds.SortByDate);
        Assert.Equal(new SortSpec(SortColumn.Modified), app.Right.Sort);
        Assert.Equal(new SortSpec(SortColumn.Size, Descending: true), app.Left.Sort);
    }

    [AvaloniaFact]
    public async Task Sort_keeps_cursor_on_same_item()
    {
        await using var app = await TestApp.StartAsync(SizedFileSystem(), show: false);
        app.Left.CursorIndex = app.Left.Rows.ToList().FindIndex(r => r.Name == "a.txt");
        var before = app.Left.CursorIndex;

        await Run(app, CommandIds.SortBySize);
        await TestApp.WaitUntil(() => Names(app.Left, "a.txt", "b.txt", "c.txt") is ["b.txt", "c.txt", "a.txt"]);

        Assert.Equal("a.txt", app.Left.CursorRow?.Name);
        Assert.NotEqual(before, app.Left.CursorIndex); // the row moved, and the cursor with it
    }

    [AvaloniaFact]
    public async Task Keyboard_sort_survives_save_and_load()
    {
        await using var app = await TestApp.StartAsync(show: false);
        await app.Main.HandleKeyAsync(KeyChord.Parse("Ctrl+Cmd+3"));
        await app.Main.HandleKeyAsync(KeyChord.Parse("Ctrl+Cmd+R"));
        app.Main.Workspace.Save();

        var saved = new JsonStateStore(app.StateDirectory).Load(TestApp.Home);
        await using var restarted = await TestApp.StartAsync(state: saved, show: false);

        Assert.Equal(new SortSpec(SortColumn.Size, Descending: true), restarted.Left.Sort);
        Assert.Equal(new SortSpec(SortColumn.Name), restarted.Right.Sort);
    }

    [AvaloniaFact]
    public async Task Sort_menu_checks_current_column_and_direction()
    {
        await using var app = await TestApp.StartAsync(show: false);
        var menu = MainMenu.Build(app.Main);
        var items = AllItems(menu).ToList();
        NativeMenuItem Item(string id) => items.Single(i => i.CommandParameter as string == id);
        string[] columns =
        [
            CommandIds.SortByName, CommandIds.SortByExtension, CommandIds.SortBySize, CommandIds.SortByDate,
            CommandIds.SortByMode,
        ];
        string[] Checked() => columns.Where(id => Item(id).IsChecked).ToArray();

        // The submenu sits in View, with the columns and "Reversed", each showing its chord.
        var sort = items.Single(i => i.Header == "Sort");
        var view = menu.Items.OfType<NativeMenuItem>().Single(i => i.Header == "View");
        Assert.Contains(view.Menu!.Items, i => ReferenceEquals(i, sort));
        var sortItems = sort.Menu!.Items.OfType<NativeMenuItem>().Where(i => i is not NativeMenuItemSeparator).ToList();
        Assert.Equal([..columns, CommandIds.SortReverse], sortItems.Select(i => i.CommandParameter as string));
        Assert.Equal(["By Name", "By Extension", "By Size", "By Date", "By Permissions", "Reversed"],
            sortItems.Select(i => i.Header));
        Assert.Equal(new KeyGesture(Key.D5, KeyModifiers.Control | KeyModifiers.Meta), Item(CommandIds.SortByMode).Gesture);
        Assert.Equal(new KeyGesture(Key.D3, KeyModifiers.Control | KeyModifiers.Meta), Item(CommandIds.SortBySize).Gesture);
        Assert.Equal(new KeyGesture(Key.R, KeyModifiers.Control | KeyModifiers.Meta), Item(CommandIds.SortReverse).Gesture);
        Assert.All(sortItems, i => Assert.NotEqual(MenuItemToggleType.None, i.ToggleType));

        Assert.Equal([CommandIds.SortByName], Checked());
        Assert.False(Item(CommandIds.SortReverse).IsChecked);

        await Run(app, CommandIds.SortBySize);
        Assert.Equal([CommandIds.SortBySize], Checked());
        Assert.False(Item(CommandIds.SortReverse).IsChecked);

        Item(CommandIds.SortReverse).Command!.Execute(CommandIds.SortReverse); // chosen from the menu
        await TestApp.WaitUntil(() => app.Left.Sort.Descending);
        Assert.True(Item(CommandIds.SortReverse).IsChecked);
        Assert.Equal([CommandIds.SortBySize], Checked());

        // The menu shows the active panel's sort.
        app.Main.Workspace.SwitchPanel();
        Assert.Equal([CommandIds.SortByName], Checked());
        Assert.False(Item(CommandIds.SortReverse).IsChecked);

        // A tab of its own sort becoming active is shown too.
        app.Main.Workspace.SwitchPanel();
        await app.Main.Commands.ExecuteAsync(CommandIds.TabNew);
        await Run(app, CommandIds.SortByDate);
        Assert.Equal([CommandIds.SortByDate], Checked());
        app.Main.Workspace.Left.Previous();
        Assert.Equal([CommandIds.SortBySize], Checked());
        Assert.True(Item(CommandIds.SortReverse).IsChecked);

        await Run(app, CommandIds.SortByMode);
        Assert.Equal([CommandIds.SortByMode], Checked());
    }

    [AvaloniaFact]
    public async Task Russian_sort_menu_names_the_permissions_item()
    {
        await using var app = await TestApp.StartAsync(state: AppState.Default(TestApp.Home) with { Language = "ru" },
            show: false);

        var item = AllItems(MainMenu.Build(app.Main)).Single(i => i.CommandParameter as string == CommandIds.SortByMode);

        Assert.Equal("По правам", item.Header);
    }

    private static IEnumerable<NativeMenuItem> AllItems(NativeMenu menu)
    {
        foreach (var item in menu.Items.OfType<NativeMenuItem>())
        {
            yield return item;
            if (item.Menu is { } sub)
                foreach (var child in AllItems(sub))
                    yield return child;
        }
    }
}
