using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Tandem.App.Commands;
using Tandem.App.Services;
using Tandem.App.Tests.Views;
using Tandem.App.ViewModels.Tools;
using Tandem.App.Views.Editing;
using Tandem.App.Views.Viewing;
using Tandem.Core.Operations;
using Tandem.Core.Tools.Checksums;
using Tandem.Core.Vfs;
using Tandem.Net;
using Tandem.Net.Remote;
using Tandem.Net.Tests.Remote;

namespace Tandem.App.Tests.Integration;

[Collection(LanguageCollection.Name)]
public sealed class ServerScenarioTests : LanguageTestBase
{
    [AvaloniaFact]
    public async Task F5_from_a_zip_archive_copies_bytes_to_a_server_panel()
    {
        var server = new FakeRemoteServer();
        await using var remote = Provider(server);
        await using var app = await TestApp.StartAsync(ArchiveApp.FileSystem(), extraProviders: [remote]);
        await app.Right.NavigateToAsync(remote.Root);
        await ArchiveApp.EnterAsync(app, "sample.zip");
        ArchiveApp.Cursor(app, app.Left, "привет.txt");
        app.Dialogs.AnswerCopyMove();

        app.Press(Key.F5, physical: PhysicalKey.F5);
        await ArchiveApp.SettleAsync(app);

        Assert.Equal("привет\n", server.ReadAllText("/привет.txt"));
        Assert.Contains("привет.txt", ArchiveApp.Names(app.Right));
        Assert.Empty(app.Failures);
    }

    [AvaloniaFact]
    public async Task Copy_reconnects_and_retries_without_leaving_a_partial_file()
    {
        var server = new FakeRemoteServer();
        await using var remote = Provider(server);
        await using var app = await TestApp.StartAsync(extraProviders: [remote]);
        await app.Right.NavigateToAsync(remote.Root);
        ArchiveApp.Cursor(app, app.Left, "notes.txt");
        app.Dialogs.AnswerCopyMove();
        app.Dialogs.ErrorAnswers.Enqueue(ErrorAction.Retry);
        server.DropAfterBytes = 2;

        app.Press(Key.F5, physical: PhysicalKey.F5);
        await ArchiveApp.SettleAsync(app);

        Assert.True(Assert.Single(app.Dialogs.ErrorRequests).ConnectionLost);
        Assert.Equal("notes", server.ReadAllText("/notes.txt"));
        Assert.Equal(["/notes.txt"], server.AllPaths);
    }

    [AvaloniaFact]
    public async Task F6_copies_to_disk_then_removes_the_server_file()
    {
        var server = new FakeRemoteServer();
        server.AddFile("/move.txt", "move");
        await using var remote = Provider(server);
        await using var app = await TestApp.StartAsync(extraProviders: [remote]);
        await app.Left.NavigateToAsync(remote.Root);
        ArchiveApp.Cursor(app, app.Left, "move.txt");
        app.Dialogs.AnswerCopyMove();

        app.Press(Key.F6, physical: PhysicalKey.F6);
        await ArchiveApp.SettleAsync(app);

        Assert.Equal("move", app.Fs.ReadAllText("/home/move.txt"));
        Assert.False(server.Exists("/move.txt"));
    }

    [AvaloniaFact]
    public async Task F8_asks_for_permanent_deletion_on_a_server()
    {
        var server = new FakeRemoteServer();
        server.AddFile("/delete.txt", "delete");
        await using var remote = Provider(server);
        await using var app = await TestApp.StartAsync(extraProviders: [remote]);
        await app.Left.NavigateToAsync(remote.Root);
        ArchiveApp.Cursor(app, app.Left, "delete.txt");
        app.Dialogs.DeleteAnswers.Enqueue(true);

        app.Press(Key.F8, physical: PhysicalKey.F8);
        await ArchiveApp.SettleAsync(app);

        Assert.True(Assert.Single(app.Dialogs.DeleteRequests).Permanent);
        Assert.False(server.Exists("/delete.txt"));
    }

    [AvaloniaFact]
    public async Task Zip_on_server_is_a_read_only_mounted_folder()
    {
        var server = new FakeRemoteServer();
        server.AddFile("/sample.zip", ArchiveApp.Bytes(ArchiveApp.FileSystem(), "/home/sample.zip"));
        await using var remote = Provider(server);
        await using var app = await TestApp.StartAsync(extraProviders: [remote]);
        await app.Left.NavigateToAsync(remote.Root);
        await ArchiveApp.EnterAsync(app, "sample.zip");

        Assert.Equal(ArchiveApp.RootNames, ArchiveApp.Names(app.Left));
        var mounted = app.Main.Services.Providers.For(app.Left.CurrentPath);
        Assert.False(mounted.Capabilities.HasFlag(ProviderCapabilities.Write));
    }

    [AvaloniaFact]
    public async Task Disk_only_commands_are_disabled_for_a_server_file()
    {
        var server = new FakeRemoteServer();
        server.AddFile("/file.txt", "file");
        await using var remote = Provider(server);
        await using var app = await TestApp.StartAsync(extraProviders: [remote]);
        await app.Left.NavigateToAsync(remote.Root);
        ArchiveApp.Cursor(app, app.Left, "file.txt");

        foreach (var command in new[] { CommandIds.ShellReveal, CommandIds.ShellOpenTerminal, CommandIds.ShellOpenWith,
                     CommandIds.SearchFiles })
            Assert.False(app.Main.Commands.CanExecute(command), command);
        foreach (var command in new[] { CommandIds.FileView, CommandIds.FileEdit, CommandIds.FileCopy,
                     CommandIds.ToolsCompareDirs, CommandIds.ToolsChecksumCreate })
            Assert.True(app.Main.Commands.CanExecute(command), command);
    }

    [AvaloniaFact]
    public async Task F3_opens_a_remote_file_in_the_viewer()
    {
        var server = new FakeRemoteServer();
        server.AddFile("/view.txt", "remote viewer text");
        await using var remote = Provider(server);
        await using var app = await TestApp.StartAsync(extraProviders: [remote]);
        await app.Left.NavigateToAsync(remote.Root);
        ArchiveApp.Cursor(app, app.Left, "view.txt");

        app.Press(Key.F3, physical: PhysicalKey.F3);
        await TestApp.WaitUntil(() =>
        {
            app.Pump();
            return app.Windows.Open.OfType<ViewerWindow>().Any();
        });

        var viewer = Assert.Single(app.Windows.Open.OfType<ViewerWindow>()).Viewer;
        await TestApp.WaitUntil(() => viewer.Lines.Contains("remote viewer text"));
        Assert.Empty(app.Failures);
    }

    [AvaloniaFact]
    public async Task F4_prompts_and_uploads_through_the_remote_file_provider()
    {
        var server = new FakeRemoteServer();
        server.AddFile("/edit.txt", "old");
        await using var remote = Provider(server);
        await using var app = await TestApp.StartAsync(extraProviders: [remote]);
        await app.Left.NavigateToAsync(remote.Root);
        ArchiveApp.Cursor(app, app.Left, "edit.txt");

        app.Press(Key.F4, physical: PhysicalKey.F4);
        await TestApp.WaitUntil(() =>
        {
            app.Pump();
            return app.Windows.Open.OfType<EditorWindow>().Any();
        });
        var editor = Assert.Single(app.Windows.Open.OfType<EditorWindow>()).Editor;
        editor.Document.Text = "new";
        app.Dialogs.EditorSaveAnswers.Enqueue(true);

        Assert.True(await editor.SaveAsync());
        Assert.Equal(EditorSaveQuestion.UploadToSource, Assert.Single(app.Dialogs.EditorSaveRequests).Question);
        Assert.Equal("new", server.ReadAllText("/edit.txt"));
        Assert.Equal(["/edit.txt"], server.AllPaths);
        Assert.Empty(app.Failures);
    }

    [AvaloniaFact]
    public async Task Declining_upload_keeps_the_source_and_cancel_keeps_the_editor_open()
    {
        var server = new FakeRemoteServer();
        server.AddFile("/edit.txt", "old");
        await using var remote = Provider(server);
        await using var app = await TestApp.StartAsync(extraProviders: [remote]);
        await app.Left.NavigateToAsync(remote.Root);
        ArchiveApp.Cursor(app, app.Left, "edit.txt");
        await app.Main.Commands.ExecuteAsync(CommandIds.FileEdit);
        var window = Assert.Single(app.Windows.Open.OfType<EditorWindow>());
        var editor = window.Editor;
        editor.Document.Text = "unsubmitted";
        app.Dialogs.EditorSaveAnswers.Enqueue(false);

        Assert.True(await editor.SaveAsync());
        Assert.True(editor.HasPendingUpload);
        Assert.False(editor.IsModified);
        Assert.Equal("old", server.ReadAllText("/edit.txt"));
        app.Dialogs.UploadBeforeCloseAnswers.Enqueue(SaveChangesAnswer.Cancel);
        Assert.False(await window.CloseAsync());
        app.Dialogs.UploadBeforeCloseAnswers.Enqueue(SaveChangesAnswer.Discard);
        Assert.True(await window.CloseAsync());
        Assert.Equal("old", server.ReadAllText("/edit.txt"));
        Assert.DoesNotContain(app.Fs.AllPaths, path => path.StartsWith(app.Temp.Directory + "/", StringComparison.Ordinal)
                                                       && path.EndsWith("/edit.txt", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task Upload_preserves_a_changed_source_until_replacement_is_confirmed()
    {
        var server = new FakeRemoteServer();
        server.AddFile("/edit.txt", "old");
        await using var remote = Provider(server);
        await using var app = await TestApp.StartAsync(extraProviders: [remote]);
        await app.Left.NavigateToAsync(remote.Root);
        ArchiveApp.Cursor(app, app.Left, "edit.txt");
        await app.Main.Commands.ExecuteAsync(CommandIds.FileEdit);
        var window = Assert.Single(app.Windows.Open.OfType<EditorWindow>());
        var editor = window.Editor;
        editor.Document.Text = "mine";
        server.AddFile("/edit.txt", "someone else's change");
        app.Dialogs.EditorSaveAnswers.Enqueue(true);
        app.Dialogs.EditorSaveAnswers.Enqueue(false);

        Assert.False(await editor.SaveAsync());
        Assert.Equal("someone else's change", server.ReadAllText("/edit.txt"));
        Assert.True(editor.HasPendingUpload);
        Assert.Equal(EditorSaveQuestion.OverwriteChangedAtSource, app.Dialogs.EditorSaveRequests[^1].Question);

        app.Dialogs.EditorSaveAnswers.Enqueue(true);
        Assert.True(await editor.UploadAsync());
        Assert.Equal("mine", server.ReadAllText("/edit.txt"));
        Assert.False(editor.HasPendingUpload);
    }

    [AvaloniaFact]
    public async Task Checksum_creation_and_verification_work_inside_a_server_panel()
    {
        var server = new FakeRemoteServer();
        server.AddFile("/source.txt", "checksum me");
        await using var remote = Provider(server);
        await using var app = await TestApp.StartAsync(extraProviders: [remote], scriptedToolWindows: true);
        await app.Left.NavigateToAsync(remote.Root);
        ArchiveApp.Cursor(app, app.Left, "source.txt");
        app.ScriptedToolWindows.OnDialog = model =>
        {
            var create = Assert.IsType<ChecksumCreateViewModel>(model);
            return new ChecksumCreateRequest(create.Algorithm, create.Name);
        };

        Assert.True(await app.Main.Commands.ExecuteAsync(CommandIds.ToolsChecksumCreate));
        Assert.True(server.Exists("/source.txt.sha256"));
        Assert.Equal("source.txt.sha256", app.Left.CursorRow?.Name);
        Assert.True(await app.Main.Commands.ExecuteAsync(CommandIds.ToolsChecksumVerify));
        var verified = Assert.IsType<ChecksumResultsViewModel>(app.ScriptedToolWindows.Shown.Last());
        Assert.Equal(VerifyStatus.Ok, Assert.Single(verified.Rows).Status);
    }

    internal static RemoteProvider Provider(FakeRemoteServer server) =>
        new("net:test", new(Guid.NewGuid(), "Test server", NetProtocol.Sftp, "127.0.0.1", 22, "test", NetAuth.Password),
            server.NewSession, new HostLocation(VfsPath.Local(TestApp.Home), null));
}
