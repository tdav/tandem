using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Tandem.App.Commands;
using Tandem.App.Resources;
using Tandem.App.ViewModels.Dialogs;
using Tandem.App.ViewModels.Tools;
using Tandem.App.Views.Dialogs;
using Tandem.App.Views.Tools;
using Tandem.Core.Tools.Compare;
using Tandem.Core.Vfs;

namespace Tandem.App.Tests.Views;

[Collection(LanguageCollection.Name)]
public sealed class StageThreeCompareTests : LanguageTestBase
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [AvaloniaFact]
    public async Task Compare_directories_marks_both_sides_and_clears_marks_when_equal()
    {
        await using var app = await TestApp.StartAsync();
        await app.Right.NavigateToAsync(VfsPath.Local("/other"));
        Assert.True(await app.Main.Commands.ExecuteAsync(CommandIds.ToolsCompareDirs));
        Assert.Equal(5, app.Left.SelectedCount);
        Assert.Equal(1, app.Right.SelectedCount);
        Assert.Equal(Loc.Format("CompareDirs_Marked", 5, 1), app.Left.Notice);

        await app.Right.NavigateToAsync(VfsPath.Local(TestApp.Home));
        Assert.True(await app.Main.Commands.ExecuteAsync(CommandIds.ToolsCompareDirs));
        Assert.Equal(0, app.Left.SelectedCount);
        Assert.Equal(0, app.Right.SelectedCount);
        Assert.Equal(Loc.Get("CompareDirs_Equal"), app.Left.Notice);
    }

    [AvaloniaFact]
    public async Task Compare_files_opens_a_window_and_shows_mode_changes()
    {
        await using var app = await TestApp.StartAsync();
        app.Left.SelectWhere(entry => entry.Name is "notes.txt" or "README.md");
        Assert.True(await app.Main.Commands.ExecuteAsync(CommandIds.ToolsCompareFiles));
        var window = Assert.IsType<FileCompareWindow>(Assert.Single(app.ToolWindows.Open));
        var model = Assert.IsType<FileCompareViewModel>(window.DataContext);
        await model.WhenReadyAsync();
        Assert.Equal(DiffKind.Changed, Assert.Single(model.Rows).Kind);

        model.Mode = CompareMode.Binary;
        await model.WhenReadyAsync();
        Assert.NotEmpty(model.Ranges);
        Assert.Contains("6E", model.Ranges[0].LeftHex + model.Ranges[0].RightHex);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Conflict_compare_keeps_the_dialog_open_and_is_missing_for_links()
    {
        await using var app = await TestApp.StartAsync();
        var source = (await app.Fs.GetEntryAsync(VfsPath.Local(TestApp.Home + "/notes.txt"), Ct))!;
        var existing = (await app.Fs.GetEntryAsync(VfsPath.Local(TestApp.Home + "/README.md"), Ct))!;
        var hook = Assert.IsType<Func<FileEntry, FileEntry, Window, Task>>(app.Dialogs.CompareFiles);
        var dialog = new ConflictDialog(new(source, existing), hook);
        var answered = dialog.ShowDialog(app.Window);
        var button = Assert.Single(dialog.GetVisualDescendants().OfType<Button>(), b => b.Name == "CompareFiles");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var compare = Assert.IsType<FileCompareWindow>(Assert.Single(app.ToolWindows.Open));
        compare.Close();
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { });
        Assert.False(answered.IsCompleted);
        Assert.True(dialog.IsVisible);
        dialog.Close();
        await answered;

        var link = new ConflictDialog(new ConflictDialogViewModel(source with { Kind = EntryKind.Symlink }, existing), hook);
        link.Show();
        Assert.DoesNotContain(link.GetVisualDescendants().OfType<Button>(), b => b.Name == "CompareFiles");
        link.Close();
    }
}
