using Tandem.App.Services;
using Tandem.Core.Settings;

namespace Tandem.App.Tests.Services;

public sealed class SettingsServiceTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "tandem-settings-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        else if (File.Exists(_directory)) File.Delete(_directory);
    }

    private JsonSettingsStore Store() => new(_directory);

    [Fact]
    public void Current_is_the_default_until_loaded()
    {
        Store().Save(AppSettings.Default with { FontSize = 18 });
        var service = new SettingsService(Store());

        Assert.Same(AppSettings.Default, service.Current);

        var changes = 0;
        service.Changed += (_, _) => changes++;
        service.Load();

        Assert.Equal(18, service.Current.FontSize);
        Assert.Equal(0, changes); // loading happens before anything shows the settings
    }

    [Fact]
    public void Update_normalizes_saves_and_raises_Changed_once()
    {
        var service = new SettingsService(Store());
        var seen = new List<AppSettings>();
        service.Changed += (_, _) =>
        {
            seen.Add(service.Current);
            // Changed comes before the save: what a handler sees is the new value, not yet the file's.
            Assert.False(File.Exists(Path.Combine(_directory, "settings.json")));
        };

        service.Update(AppSettings.Default with
        {
            FontSize = 99,
            KeyBindings = [new KeyBindingOverride("file.copy", ["Cmd+K", " "])],
        });

        var current = Assert.Single(seen);
        Assert.Same(current, service.Current);
        Assert.Equal(AppSettings.MaxFontSize, current.FontSize);
        Assert.Equal(["Cmd+K"], current.KeyBindings.Single().Chords);
        var saved = Store().Load();
        Assert.Equal(AppSettings.MaxFontSize, saved.FontSize);
        Assert.Equal(["Cmd+K"], saved.KeyBindings.Single().Chords);
    }

    [Fact]
    public void Failed_save_is_reported_and_new_settings_stay_current()
    {
        File.WriteAllText(_directory, "a file where the folder should be");
        var failures = new List<Exception>();
        var service = new SettingsService(Store(), failures.Add);
        var changes = 0;
        service.Changed += (_, _) => changes++;

        service.Update(AppSettings.Default with { Theme = ThemeChoice.Dark });

        Assert.IsAssignableFrom<IOException>(Assert.Single(failures));
        Assert.Equal(ThemeChoice.Dark, service.Current.Theme);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void Failed_save_raises_SaveFailed()
    {
        File.WriteAllText(_directory, "a file where the folder should be");
        var logged = new List<Exception>();
        var service = new SettingsService(Store(), logged.Add);
        var raised = new List<Exception>();
        service.SaveFailed += (_, e) => raised.Add(e);

        service.Update(AppSettings.Default with { Theme = ThemeChoice.Dark });

        Assert.IsAssignableFrom<IOException>(Assert.Single(raised));
        Assert.Same(raised[0], Assert.Single(logged));
    }

    [Fact]
    public void A_throwing_Changed_handler_does_not_skip_the_save()
    {
        var service = new SettingsService(Store());
        var failure = new InvalidOperationException("handler");
        service.Changed += (_, _) => throw failure;

        var thrown = Assert.Throws<InvalidOperationException>(
            () => service.Update(AppSettings.Default with { Theme = ThemeChoice.Light }));

        Assert.Same(failure, thrown);
        Assert.Equal(ThemeChoice.Light, service.Current.Theme);
        Assert.Equal(ThemeChoice.Light, Store().Load().Theme);
    }

    [Fact]
    public void Failed_save_without_a_handler_does_not_throw()
    {
        File.WriteAllText(_directory, "a file where the folder should be");
        var service = new SettingsService(Store());

        service.Update(AppSettings.Default with { Theme = ThemeChoice.Light });

        Assert.Equal(ThemeChoice.Light, service.Current.Theme);
    }
}
