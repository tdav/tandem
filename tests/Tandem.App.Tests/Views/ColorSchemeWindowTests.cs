using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Tandem.App.Services;
using Tandem.App.ViewModels.Settings;
using Tandem.App.Views.Settings;
using Tandem.Core.Settings;
using Tandem.Core.Themes;

namespace Tandem.App.Tests.Views;

// The theme on screen is process-wide: these tests run in the language collection (alone) and put the built-in
// theme back when they end.
[Collection(LanguageCollection.Name)]
public class ColorSchemeWindowTests : LanguageTestBase, IDisposable
{
    private const string ThemeJson = """{"name":"X","color":{"tokens":[{"name":"surface","value":"#112233"}]}}""";

    void IDisposable.Dispose()
    {
        ThemeService.Apply(BuiltInThemes.Stone);
        Dispose();
    }

    /// <summary>Writes the theme files into the state folder before the app starts.</summary>
    private static Func<AppServices, string, AppServices> WithThemes(params (string Id, string Json)[] files) =>
        (services, stateDirectory) =>
        {
            var directory = ThemeCatalog.DirectoryIn(stateDirectory);
            Directory.CreateDirectory(directory);
            foreach (var (id, json) in files) File.WriteAllText(Path.Combine(directory, id + ".json"), json);
            return services;
        };

    private static async Task<SettingsWindow> OpenAsync(TestApp app)
    {
        app.Press(Key.OemComma, RawInputModifiers.Meta, PhysicalKey.Comma);
        await TestApp.WaitUntil(() => app.Windows.Open.OfType<SettingsWindow>().Any());
        var window = app.Windows.Open.OfType<SettingsWindow>().Single();
        Named<TabControl>(window, "Tabs").SelectedIndex = 1;
        Pump(window);
        return window;
    }

    private static void Pump(Window window)
    {
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

    private static T Named<T>(Window window, string name) where T : Control =>
        window.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name)
        ?? throw new InvalidOperationException($"No {typeof(T).Name} named {name}.");

    private static void Click(Window window, Button button)
    {
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Pump(window);
    }

    [AvaloniaFact]
    public async Task Settings_window_lists_the_theme_files_and_applying_one_saves_and_shows_it()
    {
        await using var app = await TestApp.StartAsync(configureServices: WithThemes(("x", ThemeJson)));
        Assert.Equal(BuiltInThemes.StoneId, ThemeService.Current.Id);
        var window = await OpenAsync(app);

        var choice = Named<ComboBox>(window, "ColorSchemeChoice");
        var options = window.Settings.ColorSchemes;
        // The built-in theme first, then the files: the bundled themes installed on start and the user's "x".
        var expected = BundledThemes.FileNames.Select(Path.GetFileNameWithoutExtension).Append("x").Order();
        Assert.Equal(BuiltInThemes.StoneId, options[0].Id);
        Assert.Equal(expected, options.Skip(1).Select(o => o.Id).Order());
        var x = options.Single(o => o.Id == "x");
        Assert.Equal("X", x.Label);
        Assert.Same(options[0], choice.SelectedItem);

        choice.SelectedItem = x;
        Pump(window);
        Click(window, Named<Button>(window, "Apply"));

        Assert.Equal("x", app.Main.Settings.Current.ThemeName);
        Assert.Equal("x", new JsonSettingsStore(app.StateDirectory).Load().ThemeName);
        Assert.Equal("x", ThemeService.Current.Id);
        Assert.Null(app.Main.ThemeError);

        // Back to the built-in theme: no name is saved.
        choice.SelectedItem = options[0];
        Pump(window);
        Click(window, Named<Button>(window, "Ok"));
        Assert.Null(app.Main.Settings.Current.ThemeName);
        Assert.Equal(BuiltInThemes.StoneId, ThemeService.Current.Id);
        Assert.Empty(app.Failures);
    }

    [AvaloniaFact]
    public async Task A_broken_theme_is_listed_marked_and_cannot_be_chosen()
    {
        await using var app = await TestApp.StartAsync(configureServices: WithThemes(("x", ThemeJson), ("bad", "{")));
        var window = await OpenAsync(app);

        var broken = window.Settings.ColorSchemes.Single(o => o.Id == "bad");
        Assert.False(broken.IsSelectable);
        Assert.NotNull(broken.Error);
        Assert.Equal("bad (error)", broken.Label);

        window.Settings.SelectedColorScheme = broken;
        Pump(window);
        Assert.Equal(BuiltInThemes.StoneId, window.Settings.SelectedColorScheme?.Id);
        Click(window, Named<Button>(window, "Apply"));
        Assert.Null(app.Main.Settings.Current.ThemeName);
    }

    [AvaloniaFact]
    public async Task Start_applies_the_saved_theme()
    {
        await using var app = await TestApp.StartAsync(show: false,
            settings: AppSettings.Default with { ThemeName = "x" }, configureServices: WithThemes(("x", ThemeJson)));

        Assert.Equal("x", ThemeService.Current.Id);
        Assert.Null(app.Main.ThemeError);
        Assert.Null(app.Main.TakeStartNotice());
    }

    [AvaloniaFact]
    public async Task A_broken_saved_theme_falls_back_to_the_built_in_one_and_is_told()
    {
        var themes = WithThemes(("x", ThemeJson), ("bad", "{"));
        await using var app = await TestApp.StartAsync(show: false,
            settings: AppSettings.Default with { ThemeName = "bad" },
            configureServices: (services, stateDirectory) =>
            {
                themes(services, stateDirectory);
                // Another theme on screen before: the start must replace it with the built-in one.
                ThemeService.Apply(new ThemeCatalog(ThemeCatalog.DirectoryIn(stateDirectory)).Load("x", out _));
                Assert.Equal("x", ThemeService.Current.Id);
                return services;
            });

        Assert.Equal(BuiltInThemes.StoneId, ThemeService.Current.Id);
        Assert.NotNull(app.Main.ThemeError);
        var notice = app.Main.TakeStartNotice();
        Assert.NotNull(notice);
        Assert.Contains("bad.json", notice);
        Assert.Null(app.Main.TakeStartNotice());
        // The name stays saved, so the theme shows once its file is mended: the next settings change tries again.
        Assert.Equal("bad", app.Main.Settings.Current.ThemeName);
        var notices = new List<string>();
        app.Main.NoticeRequested += (_, text) => notices.Add(text);
        app.Main.Settings.Update(app.Main.Settings.Current with { FontSize = 16 });
        Assert.Empty(notices); // still broken: not told again
        File.WriteAllText(Path.Combine(ThemeCatalog.DirectoryIn(app.StateDirectory), "bad.json"), ThemeJson);
        app.Main.Settings.Update(app.Main.Settings.Current with { FontSize = 15 });
        Assert.Equal("bad", ThemeService.Current.Id);
        Assert.Null(app.Main.ThemeError);
        Assert.Empty(notices);
    }

    [AvaloniaFact]
    public async Task Open_themes_folder_makes_the_folder_and_opens_it()
    {
        await using var app = await TestApp.StartAsync();
        var directory = ThemeCatalog.DirectoryIn(app.StateDirectory);
        // The start installs the bundled themes there; removed, the button must make the folder again.
        Directory.Delete(directory, recursive: true);
        var window = await OpenAsync(app);

        Click(window, Named<Button>(window, "OpenThemesFolder"));
        await TestApp.WaitUntil(() => app.Shell.Calls.Count > 0);

        Assert.True(Directory.Exists(directory));
        Assert.Equal([("open", directory)], app.Shell.Calls);
    }
}
