using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Tandem.App.Commands;
using Tandem.App.ViewModels.Tools;
using Tandem.App.Views.Dialogs;
using Tandem.App.Views.Tools;
using Tandem.Core.Tools.Sync;
using Tandem.Core.Vfs;

namespace Tandem.App.Tests.Views;

[Collection(LanguageCollection.Name)]
public sealed class StageThreeSyncTests : LanguageTestBase
{
    [AvaloniaFact]
    public async Task Sync_opens_one_window_compares_confirms_and_reruns_the_comparison()
    {
        await using var app = await TestApp.StartAsync();
        Assert.False(app.Main.Commands.CanExecute(CommandIds.ToolsSyncDirs));
        await app.Right.NavigateToAsync(VfsPath.Local("/other"));
        Assert.True(await app.Main.Commands.ExecuteAsync(CommandIds.ToolsSyncDirs));
        Assert.True(await app.Main.Commands.ExecuteAsync(CommandIds.ToolsSyncDirs));
        var window = Assert.IsType<SyncWindow>(Assert.Single(app.ToolWindows.Open));
        var model = Assert.IsType<SyncViewModel>(window.DataContext);
        await model.CompareAsync();
        Assert.Equal(5, model.Totals.ToRight);
        Assert.Equal(1, model.Totals.ToLeft);
        var row = Assert.Single(model.Rows, r => r.Item.RelativePath == "inside.txt");
        row.Action = SyncAction.CopyToRight;
        Assert.Equal(SyncAction.CopyToLeft, row.Action);
        row.Action = SyncAction.DeleteRight;
        Assert.Equal(1, model.Totals.DeleteRight);

        var pending = model.SynchronizeAsync();
        var confirmation = Assert.Single(app.ToolWindows.Open.OfType<ConfirmDialog>());
        Assert.True(Assert.Single(confirmation.GetVisualDescendants().OfType<Button>(), b => b.Name == "Cancel").IsDefault);
        Assert.True(app.Fs.Exists("/other/inside.txt"));
        Assert.Single(confirmation.GetVisualDescendants().OfType<Button>(), b => b.Name == "Confirm")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await pending;
        Assert.False(app.Fs.Exists("/other/inside.txt"));
        Assert.True(app.Fs.Exists("/other/notes.txt"));
        Assert.Empty(model.Rows);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Synchronization_updates_both_a_zip_archive_and_the_local_folder()
    {
        await using var app = await ArchiveApp.StartAsync();
        app.Fs.AddFile("/dst/new.txt", "new");
        await ArchiveApp.EnterAsync(app, "sample.zip");
        Assert.True(await app.Main.Commands.ExecuteAsync(CommandIds.ToolsSyncDirs));
        var window = Assert.IsType<SyncWindow>(Assert.Single(app.ToolWindows.Open));
        var model = Assert.IsType<SyncViewModel>(window.DataContext);
        await model.CompareAsync();
        var pending = model.SynchronizeAsync();
        var confirmation = Assert.Single(app.ToolWindows.Open.OfType<ConfirmDialog>());
        Assert.Single(confirmation.GetVisualDescendants().OfType<Button>(), b => b.Name == "Confirm")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await pending;
        Assert.Equal("new", ArchiveApp.ZipEntries(app.Fs, TestApp.Home + "/sample.zip")["new.txt"]);
        Assert.Equal(ArchiveApp.ZipEntries(app.Fs, TestApp.Home + "/sample.zip")["a.txt"], app.Fs.ReadAllText("/dst/a.txt"));
        Assert.Empty(model.Rows);
        window.Close();
    }
}
