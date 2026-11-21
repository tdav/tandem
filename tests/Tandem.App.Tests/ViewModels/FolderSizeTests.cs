using Avalonia.Headless.XUnit;
using Tandem.App.Commands;
using Tandem.App.Tests.Views;
using Tandem.Core.Vfs;

namespace Tandem.App.Tests.ViewModels;

[Collection(LanguageCollection.Name)]
public sealed class FolderSizeTests : LanguageTestBase
{
    [AvaloniaFact]
    public async Task Computed_sizes_are_selected_bytes_and_survive_refresh_until_navigation()
    {
        var fs = TestApp.DefaultFileSystem();
        fs.AddFile("/home/docs/a", "abcdefg");
        fs.AddFile("/home/src/a", "abc");
        await using var app = await TestApp.StartAsync(fs, show: false);
        var path = VfsPath.Local(TestApp.Home);
        await app.Left.NavigateToAsync(path, "docs");
        app.Left.CursorRow!.IsSelected = true;
        Assert.True(await app.Main.Commands.ExecuteAsync(CommandIds.ToolsFolderSize));
        Assert.Equal(7, app.Left.CursorRow!.FolderSize!.Bytes);
        Assert.Equal(7, app.Left.SelectedBytes);
        Assert.True(await app.Main.Commands.ExecuteAsync(CommandIds.ToolsFolderSizeAll));

        await app.Left.RefreshAsync();
        Assert.Equal(7, app.Left.CursorRow!.FolderSize!.Bytes);
        Assert.Equal(3, app.Left.Rows.Single(row => row.Name == "src").FolderSize!.Bytes);
        Assert.Null(app.Left.Rows.Single(row => row.Name == "notes.txt").FolderSize);
        await app.Left.NavigateToAsync(VfsPath.Local("/other"));
        Assert.False(app.Main.Commands.CanExecute(CommandIds.ToolsFolderSizeAll));
        await app.Left.NavigateToAsync(path, "docs");
        Assert.Null(app.Left.CursorRow!.FolderSize);
    }
}
