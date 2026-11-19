using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tandem.App.Commands;
using Tandem.App.Services;
using Tandem.App.ViewModels;
using Tandem.App.Views;
using Tandem.App.Views.Dialogs;
using Tandem.Core.Platform;
using Tandem.Core.Vfs;
using Tandem.Core.Vfs.InMemory;

namespace Tandem.App.Tests.Views;

/// <summary>The shell integration with real key presses and mouse clicks on the headless main window, and the real
/// properties dialog. The shell, Quick Look and metadata are recording fakes.</summary>
[Collection(LanguageCollection.Name)]
public class ShellWindowTests : LanguageTestBase
{
    private static int IndexOf(PanelViewModel panel, string name) => panel.Rows.ToList().FindIndex(r => r.Name == name);

    private static FileListView ListView(TestApp app, PanelSide side) =>
        app.PanelView(side).GetVisualDescendants().OfType<FileListView>().Single();

    private static void RightClick(TestApp app, Point point)
    {
        app.Window.MouseDown(point, MouseButton.Right);
        app.Window.MouseUp(point, MouseButton.Right);
        app.Pump();
    }

    private static Point RowCenter(TestApp app, PanelSide side, string name)
    {
        var list = app.FileList(side);
        var row = list.ContainerFromIndex(IndexOf(side == PanelSide.Left ? app.Left : app.Right, name))
                  ?? throw new InvalidOperationException("The row is not realized.");
        return app.CenterOf(row);
    }

    private static string HeaderText(MenuItem item) => item.Header as string ?? (item.Header as TextBlock)?.Text ?? "";

    private static string[] Headers(ContextMenu menu) => menu.Items.OfType<MenuItem>().Select(HeaderText).ToArray();

    [AvaloniaFact]
    public async Task Enter_on_a_file_opens_it_with_its_default_app()
    {
        await using var app = await TestApp.StartAsync();
        app.Left.CursorIndex = IndexOf(app.Left, "notes.txt");

        app.Press(Key.Enter, physical: PhysicalKey.Enter);

        Assert.Equal([("open", "/home/notes.txt")], app.Shell.Calls);
        Assert.Empty(app.Failures);
    }

    [AvaloniaFact]
    public async Task Cmd_I_asks_for_the_properties_dialog_of_the_cursor_item()
    {
        await using var app = await TestApp.StartAsync();
        app.Left.CursorIndex = IndexOf(app.Left, "report.pdf");

        app.Press(Key.I, RawInputModifiers.Meta, PhysicalKey.I, "i");
        await TestApp.WaitUntil(() => app.Dialogs.PropertiesRequests.Count == 1);

        Assert.Equal("report.pdf", app.Dialogs.PropertiesRequests[0].Name);
        Assert.Equal("user:staff", app.Dialogs.PropertiesRequests[0].OwnerText);
    }

    [AvaloniaFact]
    public async Task F3_and_Cmd_Y_toggle_Quick_Look()
    {
        await using var app = await TestApp.StartAsync();
        app.Left.CursorIndex = IndexOf(app.Left, "notes.txt");

        app.Press(Key.F3, physical: PhysicalKey.F3);
        Assert.True(app.QuickLook.IsOpen);
        app.Press(Key.Y, RawInputModifiers.Meta, PhysicalKey.Y, "y");

        Assert.False(app.QuickLook.IsOpen);
        Assert.Equal(2, app.QuickLook.Toggles.Count);
    }

    [AvaloniaFact]
    public async Task Closing_the_window_closes_Quick_Look()
    {
        var app = await TestApp.StartAsync();
        try
        {
            app.Left.CursorIndex = IndexOf(app.Left, "notes.txt");
            app.Press(Key.F3, physical: PhysicalKey.F3);
            Assert.True(app.QuickLook.IsOpen);

            app.Window.Close();
            app.Pump();

            Assert.False(app.QuickLook.IsOpen);
            Assert.Equal(1, app.QuickLook.Closes);
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task Right_click_on_a_row_moves_the_cursor_and_opens_its_menu()
    {
        await using var app = await TestApp.StartAsync();
        app.Left.CursorIndex = IndexOf(app.Left, "docs");

        RightClick(app, RowCenter(app, PanelSide.Right, "README.md"));

        Assert.Equal(PanelSide.Right, app.Main.Workspace.ActiveSide);
        Assert.Equal("README.md", app.Right.CursorRow?.Name);
        var menu = ListView(app, PanelSide.Right).OpenMenu;
        Assert.NotNull(menu);
        Assert.Equal(
        [
            "Open", "Open With", "Show in Finder", "Quick Look", "Copy…", "Move…", "Rename", "Delete", "Properties",
        ], Headers(menu));
        var copy = menu.Items.OfType<MenuItem>().Single(i => i.Tag as string == CommandIds.FileCopy);
        Assert.Equal(new KeyGesture(Key.F5), copy.InputGesture);
        Assert.Empty(app.Shell.AppQueries); // the applications are asked for only when the submenu opens

        menu.Items.OfType<MenuItem>().Single(i => i.Tag as string == CommandIds.ShellReveal)
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        app.Pump();
        Assert.Equal([("reveal", "/home/README.md")], app.Shell.Calls);
        menu.Close();
    }

    [AvaloniaFact]
    public async Task Opening_the_open_with_submenu_lists_the_apps_once()
    {
        await using var app = await TestApp.StartAsync();
        app.Shell.Apps = [new AppInfo("TextEdit", "/System/Applications/TextEdit.app")];
        RightClick(app, RowCenter(app, PanelSide.Left, "notes.txt"));
        var menu = ListView(app, PanelSide.Left).OpenMenu!;
        var openWith = menu.Items.OfType<MenuItem>().Single(i => i.Tag as string == CommandIds.ShellOpenWith);

        openWith.IsSubMenuOpen = true;
        app.Pump();
        await TestApp.WaitUntil(() =>
        {
            app.Pump();
            return openWith.Items.OfType<MenuItem>().Any(i => HeaderText(i) == "TextEdit");
        });
        openWith.IsSubMenuOpen = false;
        openWith.IsSubMenuOpen = true;
        app.Pump();

        Assert.Equal(["/home/notes.txt"], app.Shell.AppQueries);
        Assert.Equal(["TextEdit"], openWith.Items.OfType<MenuItem>().Select(HeaderText));
        menu.Close();
    }

    [AvaloniaFact]
    public async Task The_open_with_submenu_shows_a_placeholder_until_the_apps_arrive_and_names_stay_literal()
    {
        await using var app = await TestApp.StartAsync();
        using var gate = new ManualResetEventSlim(false);
        app.Shell.AppsGate = gate;
        app.Shell.Apps = [new AppInfo("My_App", "/Applications/My_App.app")];
        RightClick(app, RowCenter(app, PanelSide.Left, "notes.txt"));
        var menu = ListView(app, PanelSide.Left).OpenMenu!;
        var openWith = menu.Items.OfType<MenuItem>().Single(i => i.Tag as string == CommandIds.ShellOpenWith);

        openWith.IsSubMenuOpen = true;
        app.Pump();
        var placeholder = Assert.Single(openWith.Items.OfType<MenuItem>());
        Assert.Equal(Tandem.App.Resources.Loc.Get("Context_Loading"), placeholder.Header);
        Assert.False(placeholder.IsEnabled);

        gate.Set();
        await TestApp.WaitUntil(() =>
        {
            app.Pump();
            return openWith.Items.OfType<MenuItem>().Any(i => i.IsEnabled);
        });
        var item = Assert.Single(openWith.Items.OfType<MenuItem>());
        // Shown as plain text: no AccessText, so no "_A" access key.
        Assert.Equal("My_App", Assert.IsType<TextBlock>(item.Header).Text);
        Assert.Equal([false], app.Shell.AppQueriesOnUiThread);
        menu.Close();
    }

    [AvaloniaFact]
    public async Task Right_click_below_the_rows_opens_the_folder_menu_and_keeps_the_cursor()
    {
        await using var app = await TestApp.StartAsync();
        app.Right.CursorIndex = IndexOf(app.Right, "notes.txt");
        var list = app.FileList(PanelSide.Right);
        var below = list.TranslatePoint(new Point(list.Bounds.Width / 2, list.Bounds.Height - 10), app.Window)!.Value;

        RightClick(app, below);

        Assert.Equal(PanelSide.Right, app.Main.Workspace.ActiveSide);
        Assert.Equal("notes.txt", app.Right.CursorRow?.Name);
        var menu = ListView(app, PanelSide.Right).OpenMenu;
        Assert.NotNull(menu);
        Assert.Equal(["New folder…", "Open Terminal here", "Refresh"], Headers(menu));
        menu.Close();
    }

    [AvaloniaFact]
    public async Task Open_with_from_the_main_menu_shows_the_apps_at_the_cursor()
    {
        await using var app = await TestApp.StartAsync();
        app.Shell.Apps = [new AppInfo("Preview", "/System/Applications/Preview.app")];
        app.Left.CursorIndex = IndexOf(app.Left, "report.pdf");

        await app.Main.ExecuteCommandAsync(CommandIds.ShellOpenWith);
        await TestApp.WaitUntil(() =>
        {
            app.Pump();
            return ListView(app, PanelSide.Left).OpenMenu is not null;
        });

        var menu = ListView(app, PanelSide.Left).OpenMenu;
        Assert.NotNull(menu);
        Assert.Equal(["Preview"], Headers(menu));
        menu.Close();
    }

    // ---- The real properties dialog ----------------------------------------------------------------------

    private static async Task<(Window Owner, DialogService Service, PropertiesViewModel Vm, InMemoryFileSystem Fs)>
        StartDialogAsync()
    {
        var fs = new InMemoryFileSystem(VfsPath.LocalProviderId);
        fs.AddFile("/home/notes.txt", "notes");
        var entry = (await fs.GetEntryAsync(VfsPath.Local("/home/notes.txt"), CancellationToken.None))!;
        var vm = new PropertiesViewModel(entry, fs, new FakeMetadata());
        await vm.LoadAsync();
        var owner = new Window { Width = 800, Height = 600 };
        owner.Show();
        return (owner, new DialogService { Owner = owner }, vm, fs);
    }

    private static PropertiesDialog DialogOf(Window owner)
    {
        Dispatcher.UIThread.RunJobs();
        return owner.OwnedWindows.OfType<PropertiesDialog>().Single();
    }

    private static TextBox Box(Window dialog, string name) =>
        dialog.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == name);

    [AvaloniaFact]
    public async Task Properties_dialog_applies_with_enter_and_keeps_a_bad_octal_open()
    {
        var (owner, service, vm, fs) = await StartDialogAsync();
        var showing = service.ShowPropertiesAsync(vm);
        var dialog = DialogOf(owner);
        Assert.Equal("644", Box(dialog, "Octal").Text);
        var groupRead = dialog.GetVisualDescendants().OfType<CheckBox>().ElementAt(3);

        Box(dialog, "Octal").Text = "9z";
        dialog.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await Task.Delay(20);
        Dispatcher.UIThread.RunJobs();
        Assert.True(dialog.IsVisible);
        Assert.Equal(true, groupRead.IsChecked);
        Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(),
            t => t.Name == "Error" && t.IsVisible && t.Text == vm.Error);

        Box(dialog, "Octal").Text = "600";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(false, groupRead.IsChecked); // the flags follow the octal field
        dialog.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await TestApp.WaitUntil(() => showing.IsCompleted);

        Assert.True(await showing);
        var entry = await fs.GetEntryAsync(VfsPath.Local("/home/notes.txt"), CancellationToken.None);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, entry!.Mode);
        owner.Close();
    }

    [AvaloniaFact]
    public async Task Esc_cancels_the_properties_dialog()
    {
        var (owner, service, vm, _) = await StartDialogAsync();
        var showing = service.ShowPropertiesAsync(vm);
        var dialog = DialogOf(owner);

        dialog.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        await TestApp.WaitUntil(() => showing.IsCompleted);

        Assert.False(await showing);
        owner.Close();
    }

    [AvaloniaFact]
    public async Task The_properties_dialog_cannot_be_closed_while_an_apply_runs()
    {
        using var gate = new ManualResetEventSlim(false);
        var fs = new InMemoryFileSystem(VfsPath.LocalProviderId);
        fs.AddFile("/home/notes.txt", "notes");
        fs.FailOn((op, _) =>
        {
            if (op == "SetMode") gate.Wait(TimeSpan.FromSeconds(10));
            return false;
        }, () => new IOException());
        var entry = (await fs.GetEntryAsync(VfsPath.Local("/home/notes.txt"), CancellationToken.None))!;
        var vm = new PropertiesViewModel(entry, fs, new FakeMetadata());
        await vm.LoadAsync();
        var owner = new Window { Width = 800, Height = 600 };
        owner.Show();
        var showing = new DialogService { Owner = owner }.ShowPropertiesAsync(vm);
        var dialog = DialogOf(owner);

        Box(dialog, "Octal").Text = "600";
        dialog.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.IsApplying);
        var cancel = dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "Cancel");
        Assert.False(cancel.IsEnabled);
        dialog.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        dialog.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.True(dialog.IsVisible);
        Assert.False(showing.IsCompleted);

        gate.Set();
        await TestApp.WaitUntil(() =>
        {
            Dispatcher.UIThread.RunJobs();
            return showing.IsCompleted;
        });
        Assert.True(await showing);
        Assert.True(vm.HasApplied);
        owner.Close();
    }
}
