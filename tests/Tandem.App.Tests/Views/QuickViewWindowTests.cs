using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Tandem.App.ViewModels;
using Tandem.App.Views.Viewing;

namespace Tandem.App.Tests.Views;

/// <summary>The quick-view panel through the real main window: Ctrl+Q puts the viewer in place of the passive side's
/// file list, keeping that side's tabs and path row, and a second Ctrl+Q brings the list back.</summary>
[Collection(LanguageCollection.Name)]
public class QuickViewWindowTests : LanguageTestBase
{
    private static int IndexOf(PanelViewModel panel, string name) => panel.Rows.ToList().FindIndex(r => r.Name == name);

    private static ViewerView? ShownViewer(TestApp app, PanelSide side) =>
        app.PanelView(side).GetVisualDescendants().OfType<ViewerView>().SingleOrDefault(v => v.IsEffectivelyVisible);

    private static void PressCtrlQ(TestApp app) => app.Press(Key.Q, RawInputModifiers.Control, PhysicalKey.Q, "q");

    [AvaloniaFact]
    public async Task Second_Ctrl_Q_closes_and_the_list_returns()
    {
        await using var app = await TestApp.StartAsync();
        app.Left.CursorIndex = IndexOf(app.Left, "notes.txt");
        app.Pump();

        PressCtrlQ(app);
        await app.Main.QuickView.IdleAsync();
        app.Pump();

        var viewer = ShownViewer(app, PanelSide.Right);
        Assert.NotNull(viewer);
        Assert.Same(app.Main.QuickView.Viewer, viewer.DataContext);
        Assert.False(app.FileList(PanelSide.Right).IsEffectivelyVisible);
        Assert.True(app.FileList(PanelSide.Left).IsEffectivelyVisible);
        Assert.Null(ShownViewer(app, PanelSide.Left));
        // The hidden side's viewer area has no page to lay out: the file is shown, and paged, once.
        Assert.All(app.PanelView(PanelSide.Left).GetVisualDescendants().OfType<ViewerView>(),
            v => Assert.Null(v.DataContext));
        // The replaced side keeps its tabs and path row; the keyboard stays with the active list.
        Assert.True(app.PanelView(PanelSide.Right).GetVisualDescendants().OfType<ListBox>()
            .Single(l => l.Name == "TabStrip").IsEffectivelyVisible);
        Assert.True(app.PanelView(PanelSide.Right).GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.Name == "PathLabel").IsEffectivelyVisible);
        Assert.Same(app.FileList(PanelSide.Left), (app.Focused as Control)?.FindAncestorOfType<ListBox>(includeSelf: true));

        PressCtrlQ(app);
        await app.Main.QuickView.IdleAsync();
        app.Pump();

        Assert.False(app.Main.QuickView.IsOpen);
        Assert.Null(ShownViewer(app, PanelSide.Right));
        Assert.True(app.FileList(PanelSide.Right).IsEffectivelyVisible);
        Assert.Empty(app.Failures);
    }

    [AvaloniaFact]
    public async Task Folder_under_cursor_shows_its_name_in_the_panel()
    {
        await using var app = await TestApp.StartAsync();
        app.Left.CursorIndex = IndexOf(app.Left, "docs");
        app.Pump();

        PressCtrlQ(app);
        await app.Main.QuickView.IdleAsync();
        app.Pump();

        Assert.Null(ShownViewer(app, PanelSide.Right));
        var message = app.PanelView(PanelSide.Right).GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.Name == "QuickViewMessage");
        Assert.True(message.IsEffectivelyVisible);
        Assert.Equal(app.Main.QuickView.Message, message.Text);
    }

    [AvaloniaFact]
    public async Task Clicking_the_other_panel_closes_quick_view()
    {
        await using var app = await TestApp.StartAsync();
        app.Left.CursorIndex = IndexOf(app.Left, "notes.txt");
        app.Pump();
        PressCtrlQ(app);
        await app.Main.QuickView.IdleAsync();
        app.Pump();

        app.Click(app.PanelView(PanelSide.Right).GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PathRow"));
        await app.Main.QuickView.IdleAsync();
        app.Pump();

        Assert.Equal(PanelSide.Right, app.Main.Workspace.ActiveSide);
        Assert.False(app.Main.QuickView.IsOpen);
        Assert.True(app.FileList(PanelSide.Right).IsEffectivelyVisible);
    }
}
