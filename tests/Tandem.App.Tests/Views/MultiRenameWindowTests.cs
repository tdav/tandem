using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Tandem.App.ViewModels.Tools;
using Tandem.App.Views.Tools;
using Tandem.Core.Vfs;

namespace Tandem.App.Tests.Views;

[Collection(LanguageCollection.Name)]
public sealed class MultiRenameWindowTests : LanguageTestBase
{
    [AvaloniaFact]
    public async Task Shows_both_names_and_escape_closes_with_no_rename_plan()
    {
        await using var app = await TestApp.StartAsync();
        var folder = VfsPath.Local(TestApp.Home);
        var entry = new FileEntry(folder.Combine("before.txt"), EntryKind.File, 0, default, default, null, false);
        var model = new MultiRenameViewModel([entry],
            new Dictionary<VfsPath, IReadOnlySet<string>> { [folder] = new HashSet<string> { entry.Name } },
            TimeZoneInfo.Utc)
        {
            NameTemplate = "after",
        };

        var result = app.ToolWindows.ShowDialogAsync(model);
        var window = Assert.IsType<MultiRenameWindow>(Assert.Single(app.ToolWindows.Open));
        window.UpdateLayout();
        app.Pump();
        var text = window.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text).ToArray();
        Assert.Contains("before.txt", text);
        Assert.Contains("after.txt", text);

        window.KeyPress(Key.Escape, TestKeys.Platform(RawInputModifiers.None), PhysicalKey.None, null);
        app.Pump();
        Assert.Null(await result);
        Assert.False(window.IsVisible);
    }
}
