using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Tandem.App.Commands;
using Tandem.App.ViewModels;
using Tandem.App.Views;
using Tandem.Core.Platform;
using Tandem.Core.Settings;
using Tandem.Core.Vfs;

namespace Tandem.App.Tests.Views;

/// <summary>Paths, folder names, volume names, favourites and history entries are shown exactly as they are: a "_" in
/// them never becomes an access-key marker (which Avalonia's AccessText would hide).</summary>
[Collection(LanguageCollection.Name)]
public class LiteralLabelTests : LanguageTestBase
{
    private const string Project = TestApp.Home + "/my_project";
    private const string Doubled = "/other/a__b";

    private static async Task<TestApp> StartAsync()
    {
        var fs = TestApp.DefaultFileSystem();
        fs.AddDirectory(Project);
        fs.AddDirectory(Doubled);
        var state = AppState.Default(TestApp.Home) with
        {
            Language = "en",
            Favorites = [Project, Doubled],
            View = new ViewState(Sidebar: true),
        };
        var app = await TestApp.StartAsync(fs, state);
        await app.Volumes.ChangeAsync(
        [
            new VolumeInfo("Macintosh HD", "/", 1000, 250, false),
            new VolumeInfo("USB_DISK", "/Volumes/USB_DISK", 100, 50, true),
        ]);
        await TestApp.WaitUntil(() => { app.Pump(); return app.Main.Volumes.Count == 2; });
        app.Pump();
        return app;
    }

    /// <summary>The text a control shows: from a plain text block, never an access text.</summary>
    private static string[] ShownTexts(Control control)
    {
        var texts = control.GetVisualDescendants().OfType<TextBlock>().ToArray();
        Assert.DoesNotContain(texts, t => t is AccessText);
        return texts.Select(t => t.Text ?? "").ToArray();
    }

    private static string HeaderText(MenuItem item) => Assert.IsType<TextBlock>(item.Header).Text!;

    [AvaloniaFact]
    public async Task Sidebar_favourites_and_volumes_show_underscores()
    {
        await using var app = await StartAsync();
        var sidebar = app.Find<SidebarView>("Sidebar");

        var favorites = sidebar.GetVisualDescendants().OfType<ItemsControl>().Single(c => c.Name == "FavoriteItems")
            .GetVisualDescendants().OfType<Button>().ToArray();
        var volumes = sidebar.GetVisualDescendants().OfType<ItemsControl>().Single(c => c.Name == "VolumeItems")
            .GetVisualDescendants().OfType<Button>().ToArray();

        Assert.Equal([[Project], [Doubled]], favorites.Select(ShownTexts));
        Assert.Contains(["USB_DISK"], volumes.Select(ShownTexts));
    }

    [AvaloniaFact]
    public async Task Volume_buttons_of_a_panel_show_underscores()
    {
        await using var app = await StartAsync();

        var buttons = app.PanelView(PanelSide.Left).GetVisualDescendants().OfType<ItemsControl>()
            .Single(c => c.Name == "VolumeButtons").GetVisualDescendants().OfType<Button>();

        Assert.Contains(["USB_DISK"], buttons.Select(ShownTexts));
    }

    [AvaloniaFact]
    public async Task Tab_titles_show_underscores()
    {
        await using var app = await StartAsync();

        await app.Left.NavigateToAsync(VfsPath.Local(Project));
        await app.Right.NavigateToAsync(VfsPath.Local(Doubled));
        app.Pump();

        var left = app.PanelView(PanelSide.Left).GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "TabTitle");
        var right = app.PanelView(PanelSide.Right).GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.Name == "TabTitle");
        Assert.IsNotType<AccessText>(left);
        Assert.Equal("my_project", left.Text);
        Assert.Equal("a__b", right.Text);
    }

    [AvaloniaFact]
    public async Task The_favourites_menu_shows_underscores()
    {
        await using var app = await StartAsync();

        Assert.True(await app.Main.Commands.ExecuteAsync(CommandIds.FavoritesShow));
        app.Pump();

        var menu = app.Window!.FavoritesMenu!;
        var items = menu.Items.OfType<MenuItem>().Take(2).ToArray();
        Assert.Equal([Project, Doubled], items.Select(HeaderText));
        menu.Hide();
    }

    [AvaloniaFact]
    public async Task The_history_menu_shows_underscores()
    {
        await using var app = await StartAsync();
        await app.Left.NavigateToAsync(VfsPath.Local(Project));
        await app.Left.NavigateToAsync(VfsPath.Local(Doubled));
        await app.Left.NavigateToAsync(VfsPath.Local(TestApp.Home));
        app.Pump();
        var back = app.PanelView(PanelSide.Left).GetVisualDescendants().OfType<Button>().Single(b => b.Name == "BackButton");
        var flyout = Assert.IsType<MenuFlyout>(back.ContextFlyout);

        flyout.ShowAt(back);
        app.Pump();

        var headers = flyout.Items.OfType<MenuItem>().Select(HeaderText).ToArray();
        Assert.Contains(Project, headers);
        Assert.Contains(Doubled, headers);
        flyout.Hide();
    }
}
