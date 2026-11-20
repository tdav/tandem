using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Tandem.App.Commands;
using Tandem.App.Resources;
using Tandem.App.Services;
using Tandem.App.Tests.Views;
using Tandem.App.ViewModels;
using Tandem.Core.Vfs;
using Tandem.Core.Vfs.InMemory;

namespace Tandem.App.Tests.ViewModels;

/// <summary>Panels over files that open as folders (<see cref="FakeHosted"/>, mounted from /home/data.pack), and
/// application bundles, on the whole app over the in-memory "local disk". Both panels start at /home.</summary>
[Collection(LanguageCollection.Name)]
public class HostedPanelTests : LanguageTestBase
{
    private static int IndexOf(PanelViewModel panel, string name) => panel.Rows.ToList().FindIndex(r => r.Name == name);

    private static void Cursor(PanelViewModel panel, string name)
    {
        var index = IndexOf(panel, name);
        Assert.True(index >= 0, $"no row {name}");
        panel.CursorIndex = index;
    }

    private static InMemoryFileSystem FileSystem()
    {
        var fs = TestApp.DefaultFileSystem();
        fs.AddFile(TestApp.Home + "/data.pack", "packed");
        fs.AddFile(TestApp.Home + "/report.docx", "PK\u0003\u0004rest");
        fs.AddFile(TestApp.Home + "/Apps/Calc.app/Contents/Info.plist", "plist");
        return fs;
    }

    private static async Task<(TestApp App, FakeHosted Hosted)> StartAsync(bool show = false)
    {
        var fs = FileSystem();
        var hosted = new FakeHosted(fs);
        hosted.Files.AddFile("/docs/inner.txt", "inner");
        hosted.Files.AddFile("/top.txt", "top");
        var app = await TestApp.StartAsync(fs, show: show, extraProviders: [hosted], mounters: [hosted]);
        return (app, hosted);
    }

    /// <summary>Enter on /home/data.pack in the left panel.</summary>
    private static async Task EnterPackAsync(TestApp app)
    {
        Cursor(app.Left, "data.pack");
        await app.Left.ActivateAsync();
        Assert.Equal(FakeHosted.Root, app.Left.CurrentPath);
    }

    [AvaloniaFact]
    public async Task Enter_on_mountable_file_navigates_into_it()
    {
        var (app, hosted) = await StartAsync();
        await using var _ = app;

        await EnterPackAsync(app);

        Assert.Equal([("data.pack", false)], hosted.MountRequests);
        Assert.Equal([false], hosted.MountsOnUiThread);
        Assert.Contains(app.Left.Rows, r => r.Name == "top.txt");
        Assert.Empty(app.Shell.Calls);
        Assert.Empty(app.Failures);
    }

    [AvaloniaFact]
    public async Task Up_from_hosted_root_returns_to_host_folder_with_cursor_on_the_file()
    {
        var (app, _) = await StartAsync();
        await using var __ = app;
        await EnterPackAsync(app);

        await app.Left.GoUpAsync();

        Assert.Equal(VfsPath.Local(TestApp.Home), app.Left.CurrentPath);
        Assert.Equal("data.pack", app.Left.CursorRow?.Name);

        // The ".." row and Backspace go the same way; inside, up goes to the parent first.
        await EnterPackAsync(app);
        await app.Left.NavigateToAsync(new VfsPath(FakeHosted.ProviderId, "/docs"));
        Assert.True(await app.Main.HandleKeyAsync(KeyChord.Parse("Back")));
        Assert.Equal(FakeHosted.Root, app.Left.CurrentPath);
        Assert.Equal("docs", app.Left.CursorRow?.Name);
        app.Left.CursorIndex = 0;
        await app.Left.ActivateAsync();
        Assert.Equal(VfsPath.Local(TestApp.Home), app.Left.CurrentPath);
        Assert.Equal("data.pack", app.Left.CursorRow?.Name);
    }

    [AvaloniaFact]
    public async Task Hosted_root_shows_parent_link()
    {
        var (app, _) = await StartAsync();
        await using var __ = app;

        await EnterPackAsync(app);

        Assert.True(app.Left.Rows[0].IsParentLink);
        await app.Left.NavigateToAsync(VfsPath.Local("/"));
        Assert.DoesNotContain(app.Left.Rows, r => r.IsParentLink); // a root with no host has none
    }

    [AvaloniaFact]
    public async Task Path_bar_and_tab_title_use_display_path()
    {
        var (app, _) = await StartAsync(show: true);
        await using var __ = app;
        await EnterPackAsync(app);
        var label = app.PanelView(PanelSide.Left).GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.Name == "PathLabel");

        Assert.Equal("/home/data.pack", app.Left.DisplayPath);
        Assert.Equal("data.pack", app.Main.Workspace.Left.Active.Title);
        app.Pump();
        Assert.Equal("/home/data.pack", label.Text);

        await app.Left.NavigateToAsync(new VfsPath(FakeHosted.ProviderId, "/docs"));
        app.Pump();

        Assert.Equal("/home/data.pack/docs", app.Left.DisplayPath);
        Assert.Equal("/home/data.pack/docs", label.Text);
        Assert.Equal("docs", app.Main.Workspace.Left.Active.Title);
        Assert.Equal("/home/data.pack/docs",
            app.Main.Left.DisplayPathOf(app.Main.Left.HistoryEntries[0])); // the back/forward menu
    }

    [AvaloniaFact]
    public async Task Tab_inside_hosted_provider_is_saved_as_host_folder()
    {
        var (app, _) = await StartAsync();
        await using var __ = app;
        await EnterPackAsync(app);
        await app.Left.NavigateToAsync(new VfsPath(FakeHosted.ProviderId, "/docs"));

        var tab = Assert.Single(app.Main.Workspace.Left.ToState().Tabs);

        Assert.Equal(VfsPath.LocalProviderId, tab.ProviderId);
        Assert.Equal(TestApp.Home, tab.Path);
    }

    [AvaloniaFact]
    public async Task Refresh_after_host_vanished_climbs_out_to_host_folder()
    {
        var (app, _) = await StartAsync();
        await using var __ = app;
        await EnterPackAsync(app);
        await app.Left.NavigateToAsync(new VfsPath(FakeHosted.ProviderId, "/docs"));

        await app.Fs.DeleteAsync(VfsPath.Local(TestApp.Home + "/data.pack"), recursive: false, CancellationToken.None);
        await app.Left.RefreshAsync();

        Assert.Equal(VfsPath.Local(TestApp.Home), app.Left.CurrentPath);
        Assert.Null(app.Left.Error);
    }

    [AvaloniaFact]
    public async Task Mount_failure_shows_message_and_panel_stays()
    {
        var (app, hosted) = await StartAsync();
        await using var __ = app;
        var failure = new IOException("damaged");
        hosted.MountFailure = failure;
        var failed = new List<Exception>();
        app.Left.OpenFailed += (_, e) => failed.Add(e);
        Cursor(app.Left, "data.pack");

        await app.Left.ActivateAsync();
        await TestApp.WaitUntil(() => app.Dialogs.Messages.Count > 0);

        Assert.Equal([failure], failed);
        Assert.Equal(VfsPath.Local(TestApp.Home), app.Left.CurrentPath);
        Assert.Equal("data.pack", app.Left.CursorRow?.Name);
        Assert.Null(app.Left.Error);
        Assert.Equal([(OperationTexts.ForFailure(failure), "damaged")], app.Dialogs.MessagesWithDetail);
        Assert.Empty(app.Shell.Calls);
        Assert.Empty(app.Failures);
    }

    [AvaloniaFact]
    public async Task Cancelled_mount_does_nothing()
    {
        var (app, hosted) = await StartAsync();
        await using var __ = app;
        hosted.MountFailure = new OperationCanceledException();
        Cursor(app.Left, "data.pack");

        await app.Left.ActivateAsync();
        app.Pump();

        Assert.Equal(VfsPath.Local(TestApp.Home), app.Left.CurrentPath);
        Assert.Null(app.Left.Error);
        Assert.Empty(app.Dialogs.Messages);
        Assert.Empty(app.Shell.Calls);
        Assert.Empty(app.Failures);
    }

    [AvaloniaFact]
    public async Task Enter_on_app_bundle_opens_it_instead_of_navigating()
    {
        var (app, hosted) = await StartAsync();
        await using var __ = app;
        await app.Left.NavigateToAsync(VfsPath.Local(TestApp.Home + "/Apps"));
        Cursor(app.Left, "Calc.app");

        await app.Left.ActivateAsync();

        Assert.Equal([("open", "/home/Apps/Calc.app")], app.Shell.Calls);
        Assert.Equal(VfsPath.Local(TestApp.Home + "/Apps"), app.Left.CurrentPath);
        Assert.Empty(hosted.MountRequests);
    }

    [AvaloniaFact]
    public async Task Nav_enter_goes_inside_app_bundle()
    {
        var (app, _) = await StartAsync();
        await using var __ = app;
        await app.Left.NavigateToAsync(VfsPath.Local(TestApp.Home + "/Apps"));
        Cursor(app.Left, "Calc.app");

        Assert.True(await app.Main.HandleKeyAsync(KeyChord.Parse("Cmd+Down")));

        Assert.Equal(VfsPath.Local(TestApp.Home + "/Apps/Calc.app"), app.Left.CurrentPath);
        Assert.Empty(app.Shell.Calls);

        // The bundle's context menu offers the same.
        await app.Left.GoUpAsync();
        Assert.DoesNotContain(app.Main.Left.ContextMenuForRow(IndexOf(app.Left, "..")),
            i => i.LabelKey == "Context_ShowPackageContents");
        var menu = app.Main.Left.ContextMenuForRow(IndexOf(app.Left, "Calc.app"));
        var show = menu.Single(i => i.LabelKey == "Context_ShowPackageContents");
        Assert.Equal(CommandIds.NavEnter, show.CommandId);
        Assert.Equal(KeyChord.Parse("Cmd+Down"), show.Chord);
        Assert.Equal(1, menu.ToList().IndexOf(show)); // right after Open
        await show.ExecuteAsync();
        Assert.Equal(VfsPath.Local(TestApp.Home + "/Apps/Calc.app"), app.Left.CurrentPath);
    }

    [AvaloniaFact]
    public async Task Nav_enter_mounts_by_content_and_reports_plain_file()
    {
        var (app, hosted) = await StartAsync();
        await using var __ = app;
        var plain = new List<FileEntry>();
        app.Left.NotAFolder += (_, e) => plain.Add(e);

        Cursor(app.Left, "report.docx");
        await app.Left.EnterAsync();
        Assert.Equal(FakeHosted.Root, app.Left.CurrentPath);
        Assert.Equal(VfsPath.Local(TestApp.Home + "/report.docx"), hosted.Host);
        Assert.Equal([("report.docx", true)], hosted.MountRequests);

        await app.Left.GoUpAsync();
        Cursor(app.Left, "notes.txt");
        await app.Left.EnterAsync();
        await TestApp.WaitUntil(() => app.Dialogs.Messages.Count > 0);

        Assert.Equal(VfsPath.Local(TestApp.Home), app.Left.CurrentPath);
        Assert.Equal(["notes.txt"], plain.Select(e => e.Name));
        Assert.Equal([Loc.Format("Panel_NotAFolder", "notes.txt")], app.Dialogs.Messages);
        Assert.Empty(app.Shell.Calls); // not opened either
        Assert.Empty(app.Failures);
    }

    [AvaloniaFact]
    public async Task Enter_on_file_of_non_local_provider_opens_temp_copy()
    {
        var (app, _) = await StartAsync();
        await using var __ = app;
        await EnterPackAsync(app);
        Cursor(app.Left, "top.txt");

        await app.Left.ActivateAsync();
        await TestApp.WaitUntil(() => app.Shell.Calls.Count > 0);

        var (call, path) = Assert.Single(app.Shell.Calls);
        Assert.Equal("open", call);
        Assert.Equal("top.txt", Path.GetFileName(path));
        Assert.StartsWith(app.Temp.Directory + Path.DirectorySeparatorChar, path);
        Assert.Equal("top", await File.ReadAllTextAsync(path));
        Assert.Empty(app.Dialogs.Messages);
        Assert.Empty(app.Failures);
    }

    [AvaloniaFact]
    public async Task Typed_path_in_hosted_panel_addresses_local_disk()
    {
        var (app, _) = await StartAsync();
        await using var __ = app;
        await EnterPackAsync(app);
        await app.Left.NavigateToAsync(new VfsPath(FakeHosted.ProviderId, "/docs"));
        var side = app.Main.Left;

        side.BeginPathEdit();
        Assert.Equal("/home/data.pack/docs", side.PathText);
        side.PathText = "/other";

        Assert.True(await side.CommitPathEditAsync());
        Assert.Equal(VfsPath.Local("/other"), app.Left.CurrentPath);
    }
}
