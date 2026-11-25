using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Tandem.App.Resources;
using Tandem.App.Services;
using Tandem.App.Tests.Views;
using Tandem.App.ViewModels;
using Tandem.App.Views;
using Tandem.Core.Settings;
using Tandem.Core.Themes;

namespace Tandem.App.Tests.ViewModels;

// The theme on screen is process-wide: these tests run in the language collection (alone) and put the built-in
// theme back when they end.
[Collection(LanguageCollection.Name)]
public sealed class ThemeMenuTests : LanguageTestBase, IDisposable
{
    private const string ThemeA = """{"name":"Alpha","color":{"tokens":[{"name":"surface","value":"#112233"}]}}""";
    private const string ThemeB = """{"name":"beta","color":{"tokens":[{"name":"surface","value":"#445566"}]}}""";

    private readonly string stateDirectory =
        Path.Combine(Path.GetTempPath(), "tandem-theme-menu-tests-" + Guid.NewGuid().ToString("N"));

    void IDisposable.Dispose()
    {
        ThemeService.Apply(BuiltInThemes.Stone);
        if (Directory.Exists(stateDirectory)) Directory.Delete(stateDirectory, recursive: true);
        Dispose();
    }

    private string ThemesDirectory => ThemeCatalog.DirectoryIn(stateDirectory);

    private void WriteTheme(string id, string json)
    {
        Directory.CreateDirectory(ThemesDirectory);
        File.WriteAllText(Path.Combine(ThemesDirectory, id + ".json"), json);
    }

    private SettingsService Settings(AppSettings? settings = null)
    {
        var store = new JsonSettingsStore(stateDirectory);
        if (settings is not null) store.Save(settings);
        var service = new SettingsService(store) { Themes = new ThemeCatalog(ThemesDirectory) };
        service.Load();
        return service;
    }

    [Fact]
    public void Entries_are_the_built_in_theme_then_the_files_by_name_with_the_chosen_one_selected()
    {
        WriteTheme("b", ThemeB);
        WriteTheme("a", ThemeA);
        var model = new ThemeMenuModel(Settings(AppSettings.Default with { ThemeName = "b" }));

        var entries = model.Entries();

        Assert.Equal([BuiltInThemes.StoneId, "a", "b"], entries.Select(e => e.Id));
        Assert.Equal([BuiltInThemes.Stone.Name, "Alpha", "beta"], entries.Select(e => e.Label));
        Assert.Equal([false, false, true], entries.Select(e => e.IsSelected));
        Assert.All(entries, e => Assert.True(e.IsEnabled));
    }

    [Fact]
    public void The_built_in_theme_is_selected_when_no_name_is_saved()
    {
        WriteTheme("a", ThemeA);
        var model = new ThemeMenuModel(Settings());

        Assert.Equal([true, false], model.Entries().Select(e => e.IsSelected));
    }

    [Fact]
    public void A_broken_file_is_listed_disabled_with_its_error_and_cannot_be_chosen()
    {
        WriteTheme("a", ThemeA);
        WriteTheme("bad", "{");
        var settings = Settings(AppSettings.Default with { ThemeName = "a" });
        var model = new ThemeMenuModel(settings);

        var bad = model.Entries().Single(e => e.Id == "bad");

        Assert.False(bad.IsEnabled);
        Assert.False(bad.IsSelected);
        Assert.NotNull(bad.Error);
        Assert.Equal(Loc.Format("Settings_ColorSchemeBroken", "bad"), bad.Label);
        Assert.False(model.Choose("bad"));
        Assert.Equal("a", settings.Current.ThemeName);
    }

    [Fact]
    public void Choosing_a_theme_saves_its_name_and_the_built_in_one_saves_none()
    {
        WriteTheme("a", ThemeA);
        var settings = Settings();
        var changes = 0;
        settings.Changed += (_, _) => changes++;
        var model = new ThemeMenuModel(settings);

        Assert.True(model.Choose("A")); // ids are matched as file names are on Windows and macOS: case aside
        Assert.Equal("a", settings.Current.ThemeName);
        Assert.Equal("a", new JsonSettingsStore(stateDirectory).Load().ThemeName);
        Assert.Equal(1, changes);
        Assert.True(model.Choose("a")); // already chosen: nothing to save
        Assert.Equal(1, changes);

        Assert.True(model.Choose(BuiltInThemes.StoneId));
        Assert.Null(settings.Current.ThemeName);
        Assert.Null(new JsonSettingsStore(stateDirectory).Load().ThemeName);
        Assert.False(model.Choose("missing"));
    }

    [Fact]
    public void A_file_dropped_into_the_folder_is_listed_without_a_restart()
    {
        var model = new ThemeMenuModel(Settings());
        Assert.Single(model.Entries());

        WriteTheme("a", ThemeA);

        Assert.Equal([BuiltInThemes.StoneId, "a"], model.Entries().Select(e => e.Id));
    }

    // ---- The menu itself ------------------------------------------------------------------------------------------

    private static NativeMenu Submenu(NativeMenu menu, string labelKey) =>
        menu.Items.OfType<NativeMenuItem>().Single(i => i.Header == Loc.Get(labelKey)).Menu
        ?? throw new InvalidOperationException($"{labelKey} has no submenu.");

    private static NativeMenu ThemeMenu(TestApp app) =>
        Submenu(Submenu(NativeMenu.GetMenu(app.Window)!, "Menu_View"), "Menu_Theme");

    /// <summary>The theme items: those after the separator that ends the light and dark ones.</summary>
    private static List<NativeMenuItem> ThemeItems(NativeMenu menu) =>
        menu.Items.SkipWhile(i => i is not NativeMenuItemSeparator).Skip(1).Cast<NativeMenuItem>().ToList();

    private static void Click(NativeMenuItem item)
    {
        Assert.True(item.IsEnabled);
        ICommand command = item.Command!;
        command.Execute(item.CommandParameter);
    }

    [AvaloniaFact]
    public async Task The_theme_menu_lists_the_schemes_then_the_theme_files_and_choosing_one_applies_and_saves_it()
    {
        await using var app = await TestApp.StartAsync(configureServices: (services, state) =>
        {
            var directory = ThemeCatalog.DirectoryIn(state);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "x.json"), ThemeA);
            File.WriteAllText(Path.Combine(directory, "bad.json"), "{");
            return services;
        });
        var menu = ThemeMenu(app);

        // System, light and dark as before, then a separator, then the themes in the catalog's order.
        Assert.Equal(
            [Loc.Get("Menu_ThemeSystem"), Loc.Get("Menu_ThemeLight"), Loc.Get("Menu_ThemeDark")],
            menu.Items.TakeWhile(i => i is not NativeMenuItemSeparator).Cast<NativeMenuItem>().Select(i => i.Header));
        var catalog = app.Main.Settings.Themes!.List();
        var items = ThemeItems(menu);
        Assert.Equal(catalog.Count, items.Count);
        Assert.Equal(BuiltInThemes.Stone.Name, items[0].Header);
        Assert.True(items[0].IsChecked);
        Assert.Single(items, i => i.IsChecked);

        var bad = items[catalog.ToList().FindIndex(t => t.Id == "bad")];
        Assert.False(bad.IsEnabled);
        Assert.NotNull(bad.ToolTip);

        var x = items[catalog.ToList().FindIndex(t => t.Id == "x")];
        Assert.Equal("Alpha", x.Header);
        Click(x);
        Assert.Equal("x", ThemeService.Current.Id);
        Assert.Equal("x", app.Main.Settings.Current.ThemeName);
        Assert.Equal("x", new JsonSettingsStore(app.StateDirectory).Load().ThemeName);
        Assert.True(x.IsChecked);
        Assert.False(items[0].IsChecked);

        // A choice made elsewhere (the settings window) moves the check.
        app.Main.Settings.Update(app.Main.Settings.Current with { ThemeName = null });
        Assert.Equal(BuiltInThemes.StoneId, ThemeService.Current.Id);
        Assert.True(items[0].IsChecked);
        Assert.False(x.IsChecked);
        Assert.Empty(app.Failures);
    }

    [AvaloniaFact]
    public async Task Opening_the_theme_menu_reads_the_folder_again()
    {
        await using var app = await TestApp.StartAsync();
        var menu = ThemeMenu(app);
        var before = ThemeItems(menu).Count;
        File.WriteAllText(Path.Combine(ThemeCatalog.DirectoryIn(app.StateDirectory), "zz-new.json"), ThemeB);

        MainMenu.Opening(menu);

        var items = ThemeItems(menu);
        Assert.Equal(before + 1, items.Count);
        Assert.Contains(items, i => i.Header == "beta");
        // The light and dark items and the separator stay as they were.
        Assert.Equal(4, menu.Items.Count - items.Count);
    }
}
