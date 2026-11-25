using Avalonia;
using Avalonia.Controls;
using Path = Avalonia.Controls.Shapes.Path;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Tandem.App.Commands;
using Tandem.App.ViewModels;
using Tandem.App.Views;
using Tandem.Core.Listing;
using Tandem.Core.Settings;
using Tandem.Core.Vfs;

namespace Tandem.App.Tests.Views;

// Start-up applies the saved language process-wide, so these tests run in the language collection.
[Collection(LanguageCollection.Name)]
public class TabStripWindowTests : LanguageTestBase
{
    /// <summary>Both sides with tabs on home and /other; the left side is active, each on its first tab.</summary>
    private static Task<TestApp> StartWithTwoTabsAsync()
    {
        static TabState Tab(string path) => new(VfsPath.LocalProviderId, path, SortColumn.Name, false, false);
        var panel = new PanelState([Tab(TestApp.Home), Tab("/other")], 0);
        return TestApp.StartAsync(state: AppState.Default(TestApp.Home) with
        {
            Language = "en", Left = panel, Right = panel,
        });
    }

    private static ListBox TabStrip(TestApp app, PanelSide side) =>
        app.PanelView(side).GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "TabStrip");

    private static Control TabItem(TestApp app, PanelSide side, int index) =>
        TabStrip(app, side).ContainerFromIndex(index) ?? throw new InvalidOperationException("The tab is not realized.");

    private static T Named<T>(Control item, string name) where T : Control =>
        item.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    private static string HeaderText(MenuItem item) => item.Header as string ?? (item.Header as TextBlock)?.Text ?? "";

    [AvaloniaFact]
    public async Task Right_click_on_tab_activates_it_before_showing_menu()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "macOS chords (Cmd); KeyMapWindowsTests and WindowsStartTests cover Windows.");
        await using var app = await StartWithTwoTabsAsync();
        var right = app.Main.Workspace.Right;
        var clicked = right.Tabs[1];
        Assert.Equal(PanelSide.Left, app.Main.Workspace.ActiveSide);

        var point = app.CenterOf(Named<TextBlock>(TabItem(app, PanelSide.Right, 1), "TabTitle"));
        app.Window.MouseDown(point, MouseButton.Right);
        app.Window.MouseUp(point, MouseButton.Right);
        app.Pump();

        Assert.Equal(PanelSide.Right, app.Main.Workspace.ActiveSide);
        Assert.Same(clicked, right.Active);
        var menu = app.PanelView(PanelSide.Right).OpenTabMenu;
        Assert.NotNull(menu);
        Assert.Equal(["New Tab", "Duplicate", "Pin", "Close", "Close Others"],
            menu.Items.OfType<MenuItem>().Select(HeaderText));
        Assert.Equal(new KeyGesture(Key.W, KeyModifiers.Meta),
            menu.Items.OfType<MenuItem>().Single(i => i.Tag as string == CommandIds.TabClose).InputGesture);

        // "Close Others" acts on the clicked tab of that side only.
        menu.Items.OfType<MenuItem>().Single(i => i.Tag as string == CommandIds.TabCloseOthers)
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        app.Pump();
        Assert.Same(clicked, Assert.Single(right.Tabs));
        Assert.Equal(2, app.Main.Workspace.Left.Tabs.Count);
        menu.Close();
        Assert.Empty(app.Failures);
    }

    [AvaloniaFact]
    public async Task Ctrl_Cmd_P_pins_the_active_tab_and_its_close_button_disappears()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "macOS chords (Cmd); KeyMapWindowsTests and WindowsStartTests cover Windows.");
        await using var app = await StartWithTwoTabsAsync();
        var item = TabItem(app, PanelSide.Left, 0);
        var close = Named<Button>(item, "CloseTabButton");
        var pin = Named<Path>(item, "PinGlyph");
        Assert.True(close.IsVisible);
        Assert.False(pin.IsVisible);

        app.Press(Key.P, RawInputModifiers.Control | RawInputModifiers.Meta, PhysicalKey.P);

        Assert.True(app.Main.Workspace.Left.Tabs[0].IsPinned);
        Assert.False(close.IsVisible);
        Assert.True(pin.IsVisible);
        Assert.True(Named<Button>(TabItem(app, PanelSide.Left, 1), "CloseTabButton").IsVisible); // the other tab
        Assert.NotNull(pin.Data); // drawn from the app's geometry resource

        app.Press(Key.W, RawInputModifiers.Meta, PhysicalKey.W); // Cmd+W: nothing happens
        Assert.Equal(2, app.Main.Workspace.Left.Tabs.Count);

        app.Press(Key.P, RawInputModifiers.Control | RawInputModifiers.Meta, PhysicalKey.P);
        Assert.False(app.Main.Workspace.Left.Tabs[0].IsPinned);
        Assert.True(close.IsVisible);
        Assert.Empty(app.Failures);
    }

    [AvaloniaFact]
    public async Task Close_button_closes_its_tab_and_keeps_the_active_one()
    {
        await using var app = await StartWithTwoTabsAsync();
        app.Main.Workspace.SwitchPanel();
        app.Pump();
        var left = app.Main.Workspace.Left;
        var active = left.Active;

        app.Click(Named<Button>(TabItem(app, PanelSide.Left, 1), "CloseTabButton"));

        Assert.Same(active, Assert.Single(left.Tabs));
        Assert.Equal(PanelSide.Left, app.Main.Workspace.ActiveSide);
        Assert.Empty(app.Failures);
    }
}
