using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Tandem.App.Commands;
using Tandem.App.ViewModels;
using Tandem.App.Views;
using Tandem.Core.Listing;
using Tandem.Core.Platform;
using Tandem.Core.Settings;
using WindowState = Tandem.Core.Settings.WindowState;
using Tandem.Core.Vfs;

namespace Tandem.App.Tests.Views;

// Start-up applies the saved language process-wide, so these tests run in the language collection.
[Collection(LanguageCollection.Name)]
public class MainWindowTests : LanguageTestBase
{
    private static int IndexOf(PanelViewModel panel, string name) => panel.Rows.ToList().FindIndex(r => r.Name == name);

    [AvaloniaFact]
    public async Task Shows_two_panels_with_rows()
    {
        await using var app = await TestApp.StartAsync();

        foreach (var side in new[] { PanelSide.Left, PanelSide.Right })
        {
            var list = app.FileList(side);
            Assert.True(list.ItemCount > 0);
            Assert.NotEmpty(list.GetRealizedContainers());
        }
        Assert.Equal("Tandem", app.Window.Title);
    }

    [AvaloniaFact]
    public async Task Tab_key_switches_active_panel()
    {
        await using var app = await TestApp.StartAsync();
        Assert.Equal(PanelSide.Left, app.Main.Workspace.ActiveSide);

        app.Press(Key.Tab, physical: PhysicalKey.Tab);

        Assert.Equal(PanelSide.Right, app.Main.Workspace.ActiveSide);
        Assert.True(app.Main.Right.IsActive);
        Assert.False(app.Main.Left.IsActive);
        Assert.True(app.FileList(PanelSide.Right).IsKeyboardFocusWithin);
    }

    [AvaloniaFact]
    public async Task Typing_letters_runs_quick_search()
    {
        await using var app = await TestApp.StartAsync();

        app.Type("r");
        app.Type("e");

        Assert.Equal("README.md", app.Left.CursorRow?.Name);
        Assert.Equal("re", app.Left.QuickSearchText);
        Assert.Equal(app.Left.CursorIndex, app.FileList(PanelSide.Left).SelectedIndex);
    }

    [AvaloniaFact]
    public async Task Quick_search_finds_cyrillic_names_typed_on_a_russian_layout()
    {
        var fs = TestApp.DefaultFileSystem();
        fs.AddFile(TestApp.Home + "/Отчёт.txt");
        await using var app = await TestApp.StartAsync(fs);

        app.Type("о");
        app.Type("т");

        Assert.Equal("Отчёт.txt", app.Left.CursorRow?.Name);
    }

    [AvaloniaFact]
    public async Task Text_typed_with_cmd_held_is_not_quick_search()
    {
        await using var app = await TestApp.StartAsync();
        var before = app.Left.CursorIndex;

        app.Window.KeyPress(Key.N, RawInputModifiers.Meta, PhysicalKey.N, "n");
        app.Type("n");
        app.Window.KeyRelease(Key.N, RawInputModifiers.Meta, PhysicalKey.N, "n");
        app.Pump();

        Assert.Equal(before, app.Left.CursorIndex);
        Assert.Equal("", app.Left.QuickSearchText);
    }

    [AvaloniaFact]
    public async Task Cmd_chords_use_the_physical_key_on_a_russian_layout()
    {
        await using var app = await TestApp.StartAsync();
        var refreshes = 0;
        app.Main.Commands.Register(CommandIds.PanelRefresh, () =>
        {
            refreshes++;
            return Task.CompletedTask;
        });

        // Cmd+R with the Russian layout active: the key produces "к"; only the physical key says R.
        app.Press(Key.None, RawInputModifiers.Meta, PhysicalKey.R, "к");

        Assert.Equal(1, refreshes);
    }

    [AvaloniaFact]
    public async Task Space_selects_and_moves_down()
    {
        await using var app = await TestApp.StartAsync();
        app.Left.CursorIndex = IndexOf(app.Left, "docs");
        app.Pump();

        app.Press(Key.Space, physical: PhysicalKey.Space, symbol: " ");
        app.Type(" ");

        Assert.True(app.Left.Rows[IndexOf(app.Left, "docs")].IsSelected);
        Assert.Equal(IndexOf(app.Left, "docs") + 1, app.Left.CursorIndex);
        Assert.Equal(1, app.Left.SelectedCount);
    }

    [AvaloniaFact]
    public async Task Arrows_move_the_cursor_and_shift_marks()
    {
        await using var app = await TestApp.StartAsync();
        app.Left.CursorIndex = 1;

        app.Press(Key.Down, physical: PhysicalKey.ArrowDown);
        Assert.Equal(2, app.Left.CursorIndex);

        app.Press(Key.Down, RawInputModifiers.Shift, PhysicalKey.ArrowDown);
        Assert.Equal(3, app.Left.CursorIndex);
        Assert.True(app.Left.Rows[2].IsSelected);

        app.Press(Key.End, physical: PhysicalKey.End);
        Assert.Equal(app.Left.Rows.Count - 1, app.Left.CursorIndex);
        app.Press(Key.Home, physical: PhysicalKey.Home);
        Assert.Equal(0, app.Left.CursorIndex);
    }

    [AvaloniaFact]
    public async Task The_list_never_moves_its_highlight_on_its_own()
    {
        await using var app = await TestApp.StartAsync();
        var list = app.FileList(PanelSide.Left);
        app.Left.CursorIndex = 3;
        app.Pump();
        Assert.Equal(3, list.SelectedIndex);
        Assert.True(list.IsKeyboardFocusWithin);

        var moves = new (Key Key, PhysicalKey Physical)[]
        {
            (Key.Down, PhysicalKey.ArrowDown), (Key.Up, PhysicalKey.ArrowUp), (Key.Home, PhysicalKey.Home),
            (Key.End, PhysicalKey.End), (Key.PageUp, PhysicalKey.PageUp), (Key.PageDown, PhysicalKey.PageDown),
        };
        foreach (var modifiers in new[] { RawInputModifiers.Alt, RawInputModifiers.Meta, RawInputModifiers.Control })
        foreach (var (key, physical) in moves)
        {
            app.Press(key, modifiers, physical);
            await TestApp.WaitUntil(() => !app.Left.IsLoading); // Cmd+Up is nav.up: let the folder load
            app.Pump();
            AssertListFollowsCursor(app, list, $"{modifiers}+{key}");
        }

        app.Type("z"); // no name starts with z
        AssertListFollowsCursor(app, list, "typed z");
        app.Press(Key.A, RawInputModifiers.Meta, PhysicalKey.A, "a"); // select.all marks rows; the highlight stays one
        AssertListFollowsCursor(app, list, "Cmd+A");
        Assert.Equal(app.Left.TotalCount, app.Left.SelectedCount);
        app.Press(Key.Down, physical: PhysicalKey.ArrowDown);
        AssertListFollowsCursor(app, list, "Down");
    }

    private static void AssertListFollowsCursor(TestApp app, ListBox list, string after)
    {
        Assert.True(list.SelectedIndex == app.Left.CursorIndex,
            $"After {after}: list highlight {list.SelectedIndex}, cursor {app.Left.CursorIndex}.");
        Assert.True(list.Selection.Count <= 1, $"After {after}: {list.Selection.Count} rows highlighted.");
        Assert.True(list.IsKeyboardFocusWithin, $"After {after}: the focus left the list ({app.Focused?.GetType().Name}).");
        var focusedRow = (app.Focused as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true);
        if (focusedRow is not null)
            Assert.True(list.IndexFromContainer(focusedRow) == app.Left.CursorIndex,
                $"After {after}: focus on row {list.IndexFromContainer(focusedRow)}, cursor {app.Left.CursorIndex}.");
    }

    [AvaloniaFact]
    public async Task Enter_opens_a_folder_and_reports_a_file()
    {
        await using var app = await TestApp.StartAsync();
        FileEntry? activated = null;
        app.Left.FileActivated += (_, e) => activated = e;

        app.Left.CursorIndex = IndexOf(app.Left, "notes.txt");
        app.Press(Key.Enter, physical: PhysicalKey.Enter);
        Assert.Equal("notes.txt", activated?.Name);

        app.Left.CursorIndex = IndexOf(app.Left, "docs");
        app.Press(Key.Enter, physical: PhysicalKey.Enter);
        await TestApp.WaitUntil(() => app.Left.CurrentPath.Path == "/home/docs");

        app.Press(Key.Back, physical: PhysicalKey.Backspace);
        await TestApp.WaitUntil(() => app.Left.CurrentPath.Path == "/home");
        Assert.Equal("docs", app.Left.CursorRow?.Name);
    }

    [AvaloniaFact]
    public async Task Header_click_changes_sort_and_toggles_direction()
    {
        await using var app = await TestApp.StartAsync();
        var header = app.PanelView(PanelSide.Left).GetVisualDescendants().OfType<Button>()
            .Single(b => b.Name == "SortBySize");

        app.Click(header);
        Assert.Equal(new SortSpec(SortColumn.Size), app.Left.Sort);
        Assert.Equal("▲", app.Main.Left.SizeSortMark);

        app.Click(header);
        Assert.Equal(new SortSpec(SortColumn.Size, Descending: true), app.Left.Sort);
        Assert.Equal("▼", app.Main.Left.SizeSortMark);
        Assert.Equal("", app.Main.Left.NameSortMark);

        app.Click(app.PanelView(PanelSide.Left).GetVisualDescendants().OfType<Button>().Single(b => b.Name == "SortByName"));
        Assert.Equal(new SortSpec(SortColumn.Name), app.Left.Sort);
    }

    [AvaloniaFact]
    public async Task View_toggles_hide_and_show_parts()
    {
        await using var app = await TestApp.StartAsync();
        var fkeys = app.Find<FKeyBar>("FKeyBar");
        var commandLine = app.Find<Control>("CommandLine");
        var sidebar = app.Find<SidebarView>("Sidebar");
        var volumeBars = app.Window.GetVisualDescendants().OfType<Control>().Where(c => c.Name == "VolumeBar").ToList();
        Assert.Equal(2, volumeBars.Count);

        Assert.True(fkeys.IsVisible);
        Assert.True(commandLine.IsVisible);
        Assert.All(volumeBars, v => Assert.True(v.IsVisible));

        app.Main.ToggleFKeyBar();
        app.Main.ToggleCommandLine();
        app.Main.ToggleVolumeBars();
        app.Main.ToggleSidebar();
        app.Pump();

        Assert.False(fkeys.IsVisible);
        Assert.False(commandLine.IsVisible);
        Assert.All(volumeBars, v => Assert.False(v.IsVisible));
        Assert.True(sidebar.IsVisible);
        Assert.Equal(new ViewState(Sidebar: true, VolumeBars: false, CommandLine: false, FKeyBar: false),
            app.Main.Workspace.View);

        app.Main.ToggleFKeyBar();
        app.Pump();
        Assert.True(fkeys.IsVisible);
    }

    [AvaloniaFact]
    public async Task Sidebar_hidden_by_default()
    {
        await using var app = await TestApp.StartAsync();

        Assert.False(app.Find<SidebarView>("Sidebar").IsVisible);
    }

    [AvaloniaFact]
    public async Task Keys_ignored_while_path_editor_focused()
    {
        await using var app = await TestApp.StartAsync();
        app.Left.CursorIndex = IndexOf(app.Left, "docs");

        app.Press(Key.G, RawInputModifiers.Meta | RawInputModifiers.Shift, PhysicalKey.G, "G");
        var editor = app.Find<TextBox>("PathEditor");
        Assert.True(app.Main.Left.IsEditingPath);
        Assert.Same(editor, app.Focused);

        app.Press(Key.Space, physical: PhysicalKey.Space, symbol: " ");
        app.Press(Key.Back, physical: PhysicalKey.Backspace);
        app.Press(Key.Down, physical: PhysicalKey.ArrowDown);
        app.Type("re");
        Assert.Equal(0, app.Left.SelectedCount);
        Assert.Equal("/home", app.Left.CurrentPath.Path);
        Assert.Equal("docs", app.Left.CursorRow?.Name);
        Assert.Equal("", app.Left.QuickSearchText);

        // Tab is the editor's too: it moves the focus on, it does not switch panels.
        app.Press(Key.Tab, physical: PhysicalKey.Tab);
        Assert.Equal(PanelSide.Left, app.Main.Workspace.ActiveSide);
    }

    [AvaloniaFact]
    public async Task Cmd_chords_still_work_in_an_editor_except_editing_ones()
    {
        await using var app = await TestApp.StartAsync();
        var refreshes = 0;
        app.Main.Commands.Register(CommandIds.PanelRefresh, () =>
        {
            refreshes++;
            return Task.CompletedTask;
        });
        app.Main.Left.BeginPathEdit();
        app.Pump();
        Assert.IsType<TextBox>(app.Focused);

        app.Press(Key.R, RawInputModifiers.Meta, PhysicalKey.R, "r");
        app.Press(Key.A, RawInputModifiers.Meta, PhysicalKey.A, "a");

        Assert.Equal(1, refreshes);
        Assert.Equal(0, app.Left.SelectedCount); // Cmd+A selected the editor's text, not the files
    }

    [AvaloniaFact]
    public async Task Path_edit_navigates_on_enter()
    {
        await using var app = await TestApp.StartAsync();
        app.Main.Left.BeginPathEdit();
        app.Pump();
        var editor = app.Find<TextBox>("PathEditor");
        Assert.Equal("/home", editor.Text);

        editor.Text = "/nowhere";
        app.Press(Key.Enter, physical: PhysicalKey.Enter);
        await TestApp.WaitUntil(() => !app.Left.IsLoading && app.Main.Left.HasError);
        Assert.True(app.Main.Left.IsEditingPath);
        Assert.True(editor.IsVisible);
        Assert.Contains("/nowhere", app.Main.Left.StatusLine);
        Assert.Equal("/home", app.Left.CurrentPath.Path);

        editor.Text = "/other";
        app.Press(Key.Enter, physical: PhysicalKey.Enter);
        await TestApp.WaitUntil(() => app.Left.CurrentPath.Path == "/other");
        app.Pump();
        Assert.False(app.Main.Left.IsEditingPath);
        Assert.False(app.Main.Left.HasError);
        Assert.True(app.FileList(PanelSide.Left).IsKeyboardFocusWithin);
    }

    [AvaloniaFact]
    public async Task Path_edit_escape_cancels()
    {
        await using var app = await TestApp.StartAsync();
        app.Main.Left.BeginPathEdit();
        app.Pump();
        app.Find<TextBox>("PathEditor").Text = "/other";

        app.Press(Key.Escape, physical: PhysicalKey.Escape);

        Assert.False(app.Main.Left.IsEditingPath);
        Assert.Equal("/home", app.Left.CurrentPath.Path);
        Assert.True(app.FileList(PanelSide.Left).IsKeyboardFocusWithin);
    }

    [AvaloniaFact]
    public async Task Volume_list_updates_on_change_event()
    {
        await using var app = await TestApp.StartAsync();
        await TestApp.WaitUntil(() => app.Main.Volumes.Count == 1);
        var bar = app.PanelView(PanelSide.Left).GetVisualDescendants().OfType<ItemsControl>()
            .Single(c => c.Name == "VolumeButtons");
        app.Pump();
        Assert.Equal(1, bar.ItemCount);

        await app.Volumes.ChangeAsync(
        [
            new VolumeInfo("Macintosh HD", "/", 100, 50, false),
            new VolumeInfo("USB", "/Volumes/USB", 10, 5, true),
        ]);
        await TestApp.WaitUntil(() => app.Main.Volumes.Count == 2);
        app.Pump();

        Assert.Equal(2, bar.ItemCount);
        Assert.Contains("USB", app.Main.Volumes.Select(v => v.Name));
    }

    [AvaloniaFact]
    public async Task FKey_bar_click_executes_command()
    {
        await using var app = await TestApp.StartAsync();
        var f5 = app.Find<FKeyBar>("FKeyBar").GetVisualDescendants().OfType<Button>()
            .Single(b => b.Tag as string == CommandIds.FileCopy);
        Assert.False(f5.IsEffectivelyEnabled); // the cursor is on "..": nothing to copy
        app.Click(f5); // clicking a disabled command does nothing and throws nothing
        Assert.Empty(app.Dialogs.Log);

        var copies = 0;
        app.Main.Commands.Register(CommandIds.FileCopy, () =>
        {
            copies++;
            return Task.CompletedTask;
        });
        app.Main.Commands.RaiseCanExecuteChanged();
        app.Pump();
        Assert.True(f5.IsEffectivelyEnabled);

        app.Click(f5);

        Assert.Equal(1, copies);
    }

    [AvaloniaFact]
    public async Task FKey_bar_lists_F3_to_F8()
    {
        await using var app = await TestApp.StartAsync();

        var buttons = app.Find<FKeyBar>("FKeyBar").GetVisualDescendants().OfType<Button>().ToList();

        Assert.Equal(
            [CommandIds.FileView, CommandIds.FileEdit, CommandIds.FileCopy, CommandIds.FileMove, CommandIds.FileMkdir,
                CommandIds.FileDelete],
            buttons.Select(b => b.Tag as string));
        Assert.Equal(["F3", "F4", "F5", "F6", "F7", "F8"], app.Main.FKeys.Select(k => k.Key));
        Assert.Equal("Copy", app.Main.FKeys[2].Label);
    }

    [AvaloniaFact]
    public async Task Menu_lists_later_commands_disabled_and_shows_shortcuts()
    {
        await using var app = await TestApp.StartAsync();
        var menu = NativeMenu.GetMenu(app.Window);
        Assert.NotNull(menu);
        var items = AllItems(menu).ToList();

        var copy = items.Single(i => i.CommandParameter as string == CommandIds.FileCopy);
        Assert.False(copy.Command!.CanExecute(copy.CommandParameter));
        copy.Command.Execute(copy.CommandParameter); // does nothing, throws nothing
        Assert.Equal(new KeyGesture(Key.F5), copy.Gesture);

        // Copy is off with the cursor on ".."; commands of later tasks are off.
        foreach (var id in new[] { CommandIds.FileView, CommandIds.FileQuickLook, CommandIds.ShellReveal })
            Assert.False(items.Single(i => i.CommandParameter as string == id).Command!.CanExecute(id));

        var refresh = items.Single(i => i.CommandParameter as string == CommandIds.PanelRefresh);
        Assert.True(refresh.Command!.CanExecute(refresh.CommandParameter));
        Assert.Equal(new KeyGesture(Key.R, KeyModifiers.Meta), refresh.Gesture);

        // Every command has a menu item.
        Assert.All(CommandIds.All, id => Assert.Contains(items, i => i.CommandParameter as string == id));
    }

    [AvaloniaFact]
    public async Task Menu_view_items_follow_the_view_state()
    {
        await using var app = await TestApp.StartAsync();
        var items = AllItems(NativeMenu.GetMenu(app.Window)!).ToList();
        var sidebar = items.Single(i => i.Header == "Sidebar");
        Assert.False(sidebar.IsChecked);

        sidebar.Command!.Execute(sidebar.CommandParameter);

        Assert.True(app.Main.Workspace.View.Sidebar);
        Assert.True(sidebar.IsChecked);
    }

    [AvaloniaFact]
    public async Task Clicks_move_the_cursor_mark_and_open()
    {
        await using var app = await TestApp.StartAsync();
        app.Main.Workspace.SwitchPanel();
        app.Pump();
        var list = app.FileList(PanelSide.Left);
        var docs = IndexOf(app.Left, "docs");
        var notes = IndexOf(app.Left, "notes.txt");

        app.Click((Control)list.ContainerFromIndex(notes)!);
        Assert.Equal(PanelSide.Left, app.Main.Workspace.ActiveSide);
        Assert.Equal(notes, app.Left.CursorIndex);
        Assert.Equal(0, app.Left.SelectedCount);

        app.Click((Control)list.ContainerFromIndex(notes)!, RawInputModifiers.Meta);
        Assert.True(app.Left.Rows[notes].IsSelected);
        app.Click((Control)list.ContainerFromIndex(notes)!, RawInputModifiers.Meta);
        Assert.False(app.Left.Rows[notes].IsSelected);

        var row = (Control)list.ContainerFromIndex(docs)!;
        var point = app.CenterOf(row);
        app.Window.MouseDown(point, MouseButton.Left);
        app.Window.MouseUp(point, MouseButton.Left);
        app.Window.MouseDown(point, MouseButton.Left);
        app.Window.MouseUp(point, MouseButton.Left);
        await TestApp.WaitUntil(() => app.Left.CurrentPath.Path == "/home/docs");
    }

    [AvaloniaFact]
    public async Task Parent_row_has_an_empty_size_cell()
    {
        await using var app = await TestApp.StartAsync();

        Assert.True(app.Left.Rows[0].IsParentLink);
        Assert.Equal("", app.Left.Rows[0].SizeText);
        Assert.Equal("Folder", app.Left.Rows[IndexOf(app.Left, "docs")].SizeText);
    }

    [AvaloniaFact]
    public async Task Access_denied_shows_the_error_and_the_access_placeholder()
    {
        var fs = TestApp.DefaultFileSystem();
        fs.AddDirectory("/locked");
        fs.FailOn((op, path) => op == "Enumerate" && path.Path == "/locked",
            () => new VfsAccessDeniedException("/locked"));
        await using var app = await TestApp.StartAsync(fs);
        var placeholder = app.Find<Button>("GrantAccess");
        Assert.False(placeholder.IsVisible);

        await app.Left.NavigateToAsync(VfsPath.Local("/locked"));
        app.Pump();

        Assert.Equal(PanelErrorKind.AccessDenied, app.Left.ErrorKind);
        Assert.Equal(app.Left.Error, app.Main.Left.StatusLine);
        Assert.True(placeholder.IsVisible);
    }

    [AvaloniaFact]
    public async Task Favorites_command_opens_the_favorites_menu()
    {
        await using var app = await TestApp.StartAsync();
        var requested = 0;
        app.Main.FavoritesMenuRequested += (_, _) => requested++;

        app.Press(Key.D, RawInputModifiers.Meta, PhysicalKey.D, "d");

        Assert.Equal(1, requested);
    }

    [AvaloniaFact]
    public async Task Command_line_focus_command_focuses_the_command_line()
    {
        await using var app = await TestApp.StartAsync();

        app.Press(Key.L, RawInputModifiers.Meta, PhysicalKey.L, "l");

        Assert.Same(app.Find<TextBox>("CommandLineBox"), app.Focused);
        app.Press(Key.Escape, physical: PhysicalKey.Escape);
        Assert.True(app.FileList(PanelSide.Left).IsKeyboardFocusWithin);
    }

    [AvaloniaFact]
    public async Task Window_placement_is_restored_and_saved_on_close()
    {
        var state = AppState.Default(TestApp.Home) with
        {
            Language = "en",
            Window = new WindowState(40, 50, 900, 600, Maximized: false, SplitRatio: 0.3),
        };
        await using var app = await TestApp.StartAsync(state: state, show: false);
        var screens = new[] { new PixelRect(0, 0, 1920, 1080) };
        var window = new MainWindow(app.Main, screens);
        window.Show();
        app.Pump();

        Assert.Equal(new PixelPoint(40, 50), window.Position);
        Assert.Equal(900, window.Width);
        Assert.Equal(600, window.Height);
        Assert.Equal(0.3, window.SplitRatio, 2);

        window.Close();
        var saved = new JsonStateStore(app.StateDirectory).Load(TestApp.Home);
        Assert.NotNull(saved.Window);
        Assert.Equal(900, saved.Window.Width);
        Assert.Equal(0.3, saved.Window.SplitRatio, 2);
    }

    [AvaloniaFact]
    public async Task Off_screen_placement_is_centred_with_the_default_size()
    {
        var state = AppState.Default(TestApp.Home) with
        {
            Language = "en",
            Window = new WindowState(5000, 5000, 900, 600, Maximized: false, SplitRatio: 0.3),
        };
        await using var app = await TestApp.StartAsync(state: state, show: false);
        var window = new MainWindow(app.Main, [new PixelRect(0, 0, 1920, 1080)]);
        window.Show();
        app.Pump();

        Assert.Equal(1200, window.Width);
        Assert.Equal(750, window.Height);
        Assert.Equal(WindowStartupLocation.CenterScreen, window.WindowStartupLocation);
        window.Close();
    }

    [AvaloniaFact]
    public async Task File_list_realizes_only_visible_rows_of_a_huge_folder()
    {
        const int count = 100_000;
        var fs = TestApp.DefaultFileSystem();
        for (var i = 0; i < count; i++) fs.AddFile($"/big/file{i:D6}.txt");
        await using var app = await TestApp.StartAsync(fs);

        await app.Left.NavigateToAsync(VfsPath.Local("/big"));
        app.Pump();
        var list = app.FileList(PanelSide.Left);
        Assert.Equal(count + 1, list.ItemCount);
        var realized = list.GetRealizedContainers().Count();
        Assert.InRange(realized, 1, 200);

        app.Left.CursorIndex = count; // the last row
        app.Pump();

        Assert.Equal(count, list.SelectedIndex);
        var last = Assert.IsAssignableFrom<Control>(list.ContainerFromIndex(count));
        var top = last.TranslatePoint(new Point(0, 0), list)!.Value.Y;
        Assert.InRange(top, 0, list.Bounds.Height - last.Bounds.Height);
        Assert.InRange(list.GetRealizedContainers().Count(), 1, 200);
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
