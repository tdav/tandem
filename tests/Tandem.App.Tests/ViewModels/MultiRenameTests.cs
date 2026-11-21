using Avalonia.Headless.XUnit;
using Tandem.App.Commands;
using Tandem.App.Services;
using Tandem.App.Tests.Integration;
using Tandem.App.Tests.Views;
using Tandem.App.ViewModels.Tools;
using Tandem.Core.Tools.Rename;
using Tandem.Core.Vfs;
using Tandem.Net.Tests.Remote;

namespace Tandem.App.Tests.ViewModels;

[Collection(LanguageCollection.Name)]
public sealed class MultiRenameTests : LanguageTestBase
{
    [AvaloniaFact]
    public async Task Command_renames_through_the_queue_and_undo_restores_names()
    {
        await using var app = await TestApp.StartAsync(show: false, scriptedToolWindows: true);
        await app.Left.NavigateToAsync(VfsPath.Local(TestApp.Home), "notes.txt");
        var commands = app.Main.Commands;
        Assert.True(commands.CanExecute(CommandIds.ToolsMultiRename));
        Assert.False(commands.CanExecute(CommandIds.ToolsUndoRename));

        app.ScriptedToolWindows.OnDialog = model =>
        {
            if (model is ConfirmModel) return true;
            var rename = Assert.IsType<MultiRenameViewModel>(model);
            rename.NameTemplate = "[unknown]";
            Assert.NotNull(rename.RuleError);
            Assert.False(rename.CanAccept);
            rename.NameTemplate = "[N]-2026";
            Assert.Equal("notes-2026.txt", Assert.Single(rename.Rows).NewName);
            Assert.True(rename.CanAccept);
            RenamePlan? plan = null;
            rename.CloseRequested += (_, result) => plan = result;
            rename.AcceptCommand.Execute(null);
            return plan;
        };

        Assert.True(await commands.ExecuteAsync(CommandIds.ToolsMultiRename));
        await app.Main.Files.WhenIdleAsync();
        Assert.True(app.Fs.Exists("/home/notes-2026.txt"));
        Assert.Contains(app.Left.Rows, row => row.Name == "notes-2026.txt");
        Assert.True(commands.CanExecute(CommandIds.ToolsUndoRename));

        Assert.True(await commands.ExecuteAsync(CommandIds.ToolsUndoRename));
        await app.Main.Files.WhenIdleAsync();
        Assert.True(app.Fs.Exists("/home/notes.txt"));
        Assert.False(commands.CanExecute(CommandIds.ToolsUndoRename));
    }

    [AvaloniaFact]
    public async Task Rename_is_disabled_on_a_read_only_panel()
    {
        await using var app = await TestApp.StartAsync(show: false, scriptedToolWindows: true,
            removedCapabilities: ProviderCapabilities.Write);
        await app.Left.NavigateToAsync(VfsPath.Local(TestApp.Home), "notes.txt");
        Assert.False(app.Main.Commands.CanExecute(CommandIds.ToolsMultiRename));
    }

    [AvaloniaFact]
    public async Task Rename_and_undo_use_the_remote_providers_normal_move()
    {
        var server = new FakeRemoteServer();
        server.AddFile("/file.txt", "contents");
        await using var provider = ServerScenarioTests.Provider(server);
        await using var app = await TestApp.StartAsync(show: false, scriptedToolWindows: true, extraProviders: [provider]);
        await app.Left.NavigateToAsync(provider.Root, "file.txt");
        app.ScriptedToolWindows.OnDialog = model =>
        {
            if (model is ConfirmModel) return true;
            var rename = Assert.IsType<MultiRenameViewModel>(model);
            rename.NameTemplate = "renamed";
            RenamePlan? plan = null;
            rename.CloseRequested += (_, answer) => plan = answer;
            rename.AcceptCommand.Execute(null);
            return plan;
        };

        Assert.True(await app.Main.Commands.ExecuteAsync(CommandIds.ToolsMultiRename));
        Assert.Equal(["/renamed.txt"], server.AllPaths);
        Assert.Equal("contents", server.ReadAllText("/renamed.txt"));
        Assert.True(await app.Main.Commands.ExecuteAsync(CommandIds.ToolsUndoRename));
        await app.Main.Files.WhenIdleAsync();

        Assert.Equal(["/file.txt"], server.AllPaths);
        Assert.Equal("contents", server.ReadAllText("/file.txt"));
        Assert.Empty(app.Failures);
    }
}
