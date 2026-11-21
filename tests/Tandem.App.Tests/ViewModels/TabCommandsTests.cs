using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Tandem.App.Commands;
using Tandem.App.Tests.Views;
using Tandem.App.ViewModels;
using Tandem.App.Views;
using Tandem.Core.Settings;
using Tandem.Core.Vfs;

namespace Tandem.App.Tests.ViewModels;

// Start-up applies the saved language process-wide, so these tests run in the language collection.
[Collection(LanguageCollection.Name)]
public class TabCommandsTests : LanguageTestBase
{
    private static PanelTabsViewModel LeftTabs(TestApp app) => app.Main.Workspace.Left;

    /// <summary>The left side with tabs on home, /other, home/docs and home/src, the first one active.</summary>
    private static async Task<TestApp> StartWithFourTabsAsync()
    {
        static TabState Tab(string path) => new(VfsPath.LocalProviderId, path, Core.Listing.SortColumn.Name, false, false);
        var state = AppState.Default(TestApp.Home) with
        {
            Language = "en",
            Left = new PanelState(
                [Tab(TestApp.Home), Tab("/other"), Tab(TestApp.Home + "/docs"), Tab(TestApp.Home + "/src")], 0),
        };
        return await TestApp.StartAsync(state: state, show: false);
    }

    private static string[] Paths(PanelTabsViewModel tabs) => tabs.Tabs.Select(t => t.Panel.CurrentPath.Path).ToArray();

    [AvaloniaFact]
    public async Task Toggle_pin_pins_and_unpins_active_tab()
    {
        await using var app = await StartWithFourTabsAsync();
        LeftTabs(app).Activate(LeftTabs(app).Tabs[1]);

        Assert.True(await app.Main.Commands.ExecuteAsync(CommandIds.TabTogglePin));
        Assert.Equal([false, true, false, false], LeftTabs(app).Tabs.Select(t => t.IsPinned));
        Assert.False(app.Main.Workspace.Right.Active.IsPinned);

        Assert.True(await app.Main.Commands.ExecuteAsync(CommandIds.TabTogglePin));
        Assert.All(LeftTabs(app).Tabs, t => Assert.False(t.IsPinned));

        // It acts on the active side.
        app.Main.Workspace.SwitchPanel();
        await app.Main.Commands.ExecuteAsync(CommandIds.TabTogglePin);
        Assert.True(app.Main.Workspace.Right.Active.IsPinned);
        Assert.All(LeftTabs(app).Tabs, t => Assert.False(t.IsPinned));
    }

    [AvaloniaFact]
    public async Task Pinned_tab_is_not_closed_by_close_command()
    {
        await using var app = await StartWithFourTabsAsync();
        var pinned = LeftTabs(app).Active;
        pinned.IsPinned = true;
        var notices = 0;
        app.Main.NoticeRequested += (_, _) => notices++;

        Assert.False(await app.Main.Commands.ExecuteAsync(CommandIds.TabClose));
        Assert.False(await app.Main.HandleKeyAsync(KeyChord.Parse("Cmd+W")));
        app.Main.RunCommand.Execute(CommandIds.TabClose); // from the menu
        Assert.False(app.Main.Left.CloseTab(pinned)); // its close button, had it one

        Assert.Equal(4, LeftTabs(app).Tabs.Count);
        Assert.Same(pinned, LeftTabs(app).Active);
        Assert.Empty(app.Failures); // and nothing is shown
        Assert.Equal(0, notices);
        Assert.Empty(app.Dialogs.Messages);
        Assert.Empty(app.Dialogs.MessagesWithDetail);
    }

    [AvaloniaFact]
    public async Task Close_is_disabled_for_pinned_tab()
    {
        await using var app = await StartWithFourTabsAsync();
        var raised = 0;
        app.Main.Commands.CanExecuteChanged += (_, _) => raised++;
        Assert.True(app.Main.Commands.CanExecute(CommandIds.TabClose));
        Assert.True(app.Main.RunCommand.CanExecute(CommandIds.TabClose));

        LeftTabs(app).Active.IsPinned = true;

        Assert.False(app.Main.Commands.CanExecute(CommandIds.TabClose));
        Assert.False(app.Main.RunCommand.CanExecute(CommandIds.TabClose));
        Assert.Equal(1, raised); // announced once, when it flipped
        var close = app.Main.Left.ContextMenuForTab(LeftTabs(app).Active)
            .Single(i => i.CommandId == CommandIds.TabClose);
        Assert.False(close.IsEnabled);

        LeftTabs(app).Next(); // an unpinned tab: it can be closed again
        Assert.True(app.Main.Commands.CanExecute(CommandIds.TabClose));
        Assert.InRange(raised, 2, 3); // switching tabs can also change the tools available for the new panel

        // The last tab cannot be closed either, nor have others closed.
        await using var single = await TestApp.StartAsync(show: false);
        Assert.False(single.Main.Commands.CanExecute(CommandIds.TabClose));
        Assert.False(single.Main.Commands.CanExecute(CommandIds.TabCloseOthers));
    }

    [AvaloniaFact]
    public async Task Close_others_keeps_clicked_and_pinned_tabs()
    {
        await using var app = await StartWithFourTabsAsync();
        var tabs = LeftTabs(app);
        tabs.Tabs[3].IsPinned = true;
        var clicked = tabs.Tabs[1];

        var menu = app.Main.Left.ContextMenuForTab(clicked);
        await menu.Single(i => i.CommandId == CommandIds.TabCloseOthers).ExecuteAsync();

        Assert.Equal(["/other", TestApp.Home + "/src"], Paths(tabs));
        Assert.Same(clicked, tabs.Active);
        Assert.False(app.Main.Commands.CanExecute(CommandIds.TabCloseOthers)); // only a pinned tab is left
        Assert.Empty(app.Failures);
    }

    [AvaloniaFact]
    public async Task Duplicate_opens_a_tab_on_the_same_folder_next_to_it()
    {
        await using var app = await StartWithFourTabsAsync();
        LeftTabs(app).Activate(LeftTabs(app).Tabs[2]);

        Assert.True(await app.Main.Commands.ExecuteAsync(CommandIds.TabDuplicate));

        Assert.Equal([TestApp.Home, "/other", TestApp.Home + "/docs", TestApp.Home + "/docs", TestApp.Home + "/src"],
            Paths(LeftTabs(app)));
        Assert.Same(LeftTabs(app).Tabs[3], LeftTabs(app).Active);
    }

    [AvaloniaFact]
    public async Task Pin_state_survives_save_and_load()
    {
        await using var app = await StartWithFourTabsAsync();
        LeftTabs(app).Activate(LeftTabs(app).Tabs[2]);
        await app.Main.HandleKeyAsync(KeyChord.Parse("Ctrl+Cmd+P"));
        app.Main.Workspace.Save();

        var saved = new JsonStateStore(app.StateDirectory).Load(TestApp.Home);
        await using var restarted = await TestApp.StartAsync(state: saved, show: false);

        Assert.Equal([false, false, true, false], LeftTabs(restarted).Tabs.Select(t => t.IsPinned));
        Assert.False(restarted.Main.Commands.CanExecute(CommandIds.TabClose)); // the pinned tab is active again
    }

    [AvaloniaFact]
    public async Task Tab_menu_lists_items_and_swaps_pin_label()
    {
        await using var app = await StartWithFourTabsAsync();
        var tab = LeftTabs(app).Tabs[1];

        var menu = app.Main.Left.ContextMenuForTab(tab);

        Assert.Equal(["New Tab", "Duplicate", "Pin", "Close", "Close Others"], menu.Select(i => i.Label));
        Assert.Equal(
            [CommandIds.TabNew, CommandIds.TabDuplicate, CommandIds.TabTogglePin, CommandIds.TabClose,
                CommandIds.TabCloseOthers],
            menu.Select(i => i.CommandId));
        Assert.Equal(["Cmd+T", "", "Ctrl+Cmd+P", "Cmd+W", ""], menu.Select(i => i.Chord?.ToString() ?? ""));
        Assert.All(menu, i => Assert.True(i.IsEnabled));

        await menu.Single(i => i.CommandId == CommandIds.TabTogglePin).ExecuteAsync();
        Assert.True(tab.IsPinned);

        menu = app.Main.Left.ContextMenuForTab(tab);
        Assert.Equal(["New Tab", "Duplicate", "Unpin", "Close", "Close Others"], menu.Select(i => i.Label));
        Assert.Equal("Context_TabUnpin", menu[2].LabelKey);
        Assert.False(menu.Single(i => i.CommandId == CommandIds.TabClose).IsEnabled);
        Assert.True(menu.Single(i => i.CommandId == CommandIds.TabCloseOthers).IsEnabled);
    }

    [AvaloniaFact]
    public async Task Tab_menu_of_the_passive_side_activates_that_side_and_tab()
    {
        await using var app = await StartWithFourTabsAsync();
        app.Main.Workspace.SwitchPanel();
        var tab = LeftTabs(app).Tabs[2];

        var menu = app.Main.Left.ContextMenuForTab(tab);

        Assert.Equal(PanelSide.Left, app.Main.Workspace.ActiveSide);
        Assert.Same(tab, LeftTabs(app).Active);
        Assert.Equal(5, menu.Count);
    }

    [AvaloniaFact]
    public async Task Main_menu_pin_item_follows_the_active_tab()
    {
        await using var app = await StartWithFourTabsAsync();
        var items = MainMenuItems(MainMenu.Build(app.Main)).ToList();
        var pin = items.Single(i => i.CommandParameter as string == CommandIds.TabTogglePin);
        var window = MainMenuItems(MainMenu.Build(app.Main)).Single(i => i.Header == "Window");
        Assert.Contains(window.Menu!.Items.OfType<NativeMenuItem>(),
            i => i.CommandParameter as string == CommandIds.TabTogglePin);
        Assert.Equal("Pin Tab", pin.Header);
        Assert.Equal(new Avalonia.Input.KeyGesture(Avalonia.Input.Key.P,
            Avalonia.Input.KeyModifiers.Control | Avalonia.Input.KeyModifiers.Meta), pin.Gesture);

        pin.Command!.Execute(pin.CommandParameter);
        Assert.True(LeftTabs(app).Active.IsPinned);
        Assert.Equal("Unpin Tab", pin.Header);

        LeftTabs(app).Next();
        Assert.Equal("Pin Tab", pin.Header);
        app.Main.Workspace.SwitchPanel();
        Assert.Equal("Pin Tab", pin.Header);
        app.Main.Workspace.SwitchPanel();
        LeftTabs(app).Previous();
        Assert.Equal("Unpin Tab", pin.Header);
    }

    private static IEnumerable<NativeMenuItem> MainMenuItems(NativeMenu menu)
    {
        foreach (var item in menu.Items.OfType<NativeMenuItem>())
        {
            yield return item;
            if (item.Menu is { } sub)
                foreach (var child in MainMenuItems(sub))
                    yield return child;
        }
    }
}
