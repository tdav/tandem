using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Tandem.App.Commands;
using Tandem.App.Resources;
using Tandem.App.Tests.Views;
using Tandem.App.ViewModels.Tools;
using Tandem.App.Views.Dialogs;
using Tandem.App.Views.Tools;
using Tandem.Core.Vfs;
using Tandem.Net;
using Tandem.Net.Remote;
using Tandem.Net.Tests.Remote;

namespace Tandem.App.Tests.Integration;

[Collection(LanguageCollection.Name)]
public sealed class ServerCompareSyncTests : LanguageTestBase
{
    private static readonly DateTimeOffset FileTime = new(2025, 1, 2, 3, 4, 0, TimeSpan.Zero);

    [AvaloniaFact]
    public async Task Synchronize_a_disk_folder_with_a_server_folder_both_ways()
    {
        var server = new FakeRemoteServer();
        server.AddFile("/share/on-server.txt", "server", FileTime);
        var fs = TestApp.DefaultFileSystem();
        fs.AddFile("/local/on-disk.txt", "disk", FileTime);
        await using var provider = Remote(server);
        await using var app = await TestApp.StartAsync(fs, extraProviders: [provider]);
        await app.Left.NavigateToAsync(VfsPath.Local("/local"));
        await app.Right.NavigateToAsync(provider.Root.Combine("share"));
        Assert.True(await app.Main.Commands.ExecuteAsync(CommandIds.ToolsSyncDirs));
        var sync = Assert.IsType<SyncViewModel>(Assert.IsType<SyncWindow>(Assert.Single(app.ToolWindows.Open)).DataContext);

        sync.ByContent = true;
        await sync.CompareAsync();
        Assert.Equal(1, sync.Totals.ToLeft);
        Assert.Equal(1, sync.Totals.ToRight);
        var pending = sync.SynchronizeAsync();
        ClickConfirmation(app, "Confirm");
        await pending;

        Assert.Equal("server", fs.ReadAllText("/local/on-server.txt"));
        Assert.Equal("disk", server.ReadAllText("/share/on-disk.txt"));
        Assert.Empty(sync.Rows);
        sync.ShowEqual = true;
        Assert.Equal(2, sync.Rows.Count);
        Assert.Empty(app.Failures);
    }

    [AvaloniaFact]
    public async Task Synchronize_confirmation_distinguishes_the_trash_from_permanent_server_deletion()
    {
        var server = new FakeRemoteServer();
        server.AddFile("/share/server-only.txt", "server", FileTime);
        var fs = TestApp.DefaultFileSystem();
        fs.AddFile("/local/disk-only.txt", "disk", FileTime);
        await using var provider = Remote(server);
        await using var app = await TestApp.StartAsync(fs, trashCapable: true, extraProviders: [provider]);
        await app.Left.NavigateToAsync(VfsPath.Local("/local"));
        await app.Right.NavigateToAsync(provider.Root.Combine("share"));
        Assert.True(await app.Main.Commands.ExecuteAsync(CommandIds.ToolsSyncDirs));
        var sync = Assert.IsType<SyncViewModel>(Assert.IsType<SyncWindow>(Assert.Single(app.ToolWindows.Open)).DataContext);
        await sync.CompareAsync();
        Assert.Single(sync.Rows, row => row.Item.RelativePath == "disk-only.txt").Action =
            Tandem.Core.Tools.Sync.SyncAction.DeleteLeft;
        Assert.Single(sync.Rows, row => row.Item.RelativePath == "server-only.txt").Action =
            Tandem.Core.Tools.Sync.SyncAction.DeleteRight;

        var pending = sync.SynchronizeAsync();
        var confirm = Assert.Single(app.ToolWindows.Open.OfType<ConfirmDialog>());
        var detail = Assert.Single(confirm.GetVisualDescendants().OfType<TextBlock>(), text => text.Name == "Detail");
        Assert.Contains(Loc.Get("Sync_ToTrash"), detail.Text);
        Assert.Contains(Loc.Get("Sync_Permanent"), detail.Text);
        Assert.True(Assert.Single(confirm.GetVisualDescendants().OfType<Button>(), button => button.Name == "Cancel").IsDefault);
        ClickConfirmation(app, "Cancel");
        await pending;

        Assert.True(fs.Exists("/local/disk-only.txt"));
        Assert.Equal("server", server.ReadAllText("/share/server-only.txt"));
    }

    [AvaloniaFact]
    public async Task Compare_folders_between_disk_and_server_pairs_decomposed_and_composed_names()
    {
        var server = new FakeRemoteServer();
        server.AddFile("/share/й.txt", "same", FileTime);
        server.AddFile("/share/other", "remote", FileTime);
        var fs = TestApp.DefaultFileSystem();
        fs.AddFile("/local/и\u0306.txt", "same", FileTime);
        await using var provider = Remote(server);
        await using var app = await TestApp.StartAsync(fs, extraProviders: [provider]);
        await app.Left.NavigateToAsync(VfsPath.Local("/local"));
        await app.Right.NavigateToAsync(provider.Root.Combine("share"));

        Assert.True(await app.Main.Commands.ExecuteAsync(CommandIds.ToolsCompareDirs));
        Assert.Equal(0, app.Left.SelectedCount);
        Assert.Equal("other", Assert.Single(app.Right.Rows, row => row.IsSelected).Name);
    }

    [AvaloniaFact]
    public async Task Compare_files_between_an_archive_and_the_server()
    {
        var server = new FakeRemoteServer();
        server.AddFile("/share/a.txt", "a deliberately different line", FileTime);
        await using var provider = Remote(server);
        await using var app = await TestApp.StartAsync(ArchiveApp.FileSystem(), extraProviders: [provider]);
        await ArchiveApp.EnterAsync(app, "sample.zip");
        await app.Right.NavigateToAsync(provider.Root.Combine("share"));
        ArchiveApp.Cursor(app, app.Left, "a.txt");
        Assert.True(await app.Main.Commands.ExecuteAsync(CommandIds.ToolsCompareFiles));
        var model = Assert.IsType<FileCompareViewModel>(Assert.IsType<FileCompareWindow>(Assert.Single(app.ToolWindows.Open)).DataContext);
        await model.WhenReadyAsync();

        Assert.Null(model.Error);
        Assert.Contains("/home/sample.zip", model.LeftTitle);
        Assert.EndsWith("/share/a.txt", model.RightTitle);
        Assert.Contains(model.Rows, row => row.RightText == "a deliberately different line");
    }

    private static RemoteProvider Remote(FakeRemoteServer server) => new("net:tools",
        new ConnectionProfile(Guid.NewGuid(), "Tools server", NetProtocol.Sftp, "example.invalid", 22, "tools",
            NetAuth.Password), server.NewSession, new HostLocation(VfsPath.Local(TestApp.Home), null));

    private static void ClickConfirmation(TestApp app, string name)
    {
        var confirmation = Assert.Single(app.ToolWindows.Open.OfType<ConfirmDialog>());
        Assert.Single(confirmation.GetVisualDescendants().OfType<Button>(), button => button.Name == name)
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
}
