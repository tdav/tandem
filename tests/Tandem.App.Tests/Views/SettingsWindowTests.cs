using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Tandem.App.Commands;
using Tandem.App.ViewModels.Settings;
using Tandem.App.Views.Settings;
using Tandem.Core.Settings;

namespace Tandem.App.Tests.Views;

// The theme is process-wide: these tests run in the language collection and put the theme back when they end.
[Collection(LanguageCollection.Name)]
public class SettingsWindowTests : LanguageTestBase, IDisposable
{
    void IDisposable.Dispose()
    {
        Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
        Dispose();
    }

    private static async Task<SettingsWindow> OpenAsync(TestApp app)
    {
        app.Press(Key.OemComma, RawInputModifiers.Meta, PhysicalKey.Comma);
        await TestApp.WaitUntil(() => app.Windows.Open.OfType<SettingsWindow>().Any());
        var window = app.Windows.Open.OfType<SettingsWindow>().Single();
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

    private static ShortcutRow Row(SettingsWindow window, string id) =>
        window.Settings.Shortcuts.Single(r => r.CommandId == id);

    private static void Record(SettingsWindow window, ShortcutRow row, Key key, RawInputModifiers modifiers,
        PhysicalKey physical = PhysicalKey.None)
    {
        window.Settings.SelectedShortcut = row;
        Pump(window);
        var recorder = Named<ShortcutRecorder>(window, "Recorder");
        recorder.Focus();
        Pump(window);
        window.KeyPress(key, modifiers, physical, null);
        window.KeyRelease(key, modifiers, physical, null);
        Pump(window);
    }

    [AvaloniaFact]
    public async Task Cmd_comma_opens_settings_window()
    {
        await using var app = await TestApp.StartAsync();
        Assert.True(app.Main.Commands.CanExecute(CommandIds.AppSettings));

        var window = await OpenAsync(app);

        Assert.True(window.IsVisible);
        Assert.True(app.Main.HasOpenWindows);
        var tabs = Named<TabControl>(window, "Tabs");
        Assert.Equal(5, tabs.ItemCount);

        // Cmd+, again (here: the command from the menu) brings the same window forward; no second one opens.
        await app.Main.ExecuteCommandAsync(CommandIds.AppSettings);
        Assert.Single(app.Windows.Open.OfType<SettingsWindow>());

        // Quitting closes it, as any window of the service; nothing is applied.
        var before = app.Main.Settings.Current;
        Assert.True(await app.Main.Services.Windows.CloseAllAsync());
        Pump(app.Window);
        Assert.Empty(app.Windows.Open);
        Assert.Same(before, app.Main.Settings.Current);
        Assert.Empty(app.Failures);
    }

    [AvaloniaFact]
    public async Task Application_menu_has_the_settings_item()
    {
        await using var app = await TestApp.StartAsync();
        var menu = NativeMenu.GetMenu(Application.Current!);
        Assert.NotNull(menu);
        var item = menu.Items.OfType<NativeMenuItem>().Single(i => i.CommandParameter as string == CommandIds.AppSettings);

        Assert.Equal("Settings…", item.Header);
        Assert.Equal(new KeyGesture(Key.OemComma, KeyModifiers.Meta), item.Gesture);
        Assert.True(item.Command!.CanExecute(item.CommandParameter));

        // The application menu follows a changed key map too.
        app.Main.Settings.Update(app.Main.Settings.Current with
        {
            KeyBindings = [new KeyBindingOverride(CommandIds.AppSettings, ["Cmd+Alt+OemComma"])],
        });
        Assert.Equal(new KeyGesture(Key.OemComma, KeyModifiers.Meta | KeyModifiers.Alt), item.Gesture);
    }

    [AvaloniaFact]
    public async Task Recorder_turns_a_key_press_into_a_chord()
    {
        await using var app = await TestApp.StartAsync();
        var window = await OpenAsync(app);
        var mkdir = Row(window, CommandIds.FileMkdir);
        var message = Named<TextBlock>(window, "ShortcutMessage");

        Record(window, mkdir, Key.N, RawInputModifiers.Control | RawInputModifiers.Meta, PhysicalKey.N);
        Assert.Equal([KeyChord.Parse("F7"), KeyChord.Parse("Cmd+7"), KeyChord.Parse("Ctrl+Cmd+N")], mkdir.Chords);

        // A plain letter cannot be a shortcut: the row stays and the window says why.
        Record(window, mkdir, Key.K, RawInputModifiers.None, PhysicalKey.K);
        Assert.Equal(3, mkdir.Chords.Count);
        Assert.True(message.IsVisible);
        Assert.False(string.IsNullOrEmpty(message.Text));

        // A chord another command has: the question is asked; "no" leaves both, "yes" moves it.
        var move = Row(window, CommandIds.FileMove);
        app.Dialogs.ReassignAnswers.Enqueue(false);
        Record(window, move, Key.F5, RawInputModifiers.None);
        Assert.Equal([("F5", "Copy…")], app.Dialogs.ReassignRequests.Select(r => (r.Chord, r.Owner)));
        Assert.Same(window, app.Dialogs.ReassignRequests[0].Window);
        Assert.Equal(CommandIds.FileCopy, window.Settings.OwnerOf(KeyChord.Parse("F5"))!.CommandId);
        app.Dialogs.ReassignAnswers.Enqueue(true);
        Record(window, move, Key.F5, RawInputModifiers.None);
        Assert.Equal(CommandIds.FileMove, window.Settings.OwnerOf(KeyChord.Parse("F5"))!.CommandId);

        // Esc stops recording and records nothing; the window stays open.
        var chords = move.Chords;
        Record(window, move, Key.Escape, RawInputModifiers.None);
        Assert.Equal(chords, move.Chords);
        Assert.True(window.IsVisible);
        Assert.False(Named<ShortcutRecorder>(window, "Recorder").IsRecording);

        // Nothing reaches the settings before OK or Apply.
        Assert.Empty(app.Main.Settings.Current.KeyBindings);
    }

    [AvaloniaFact]
    public async Task Ok_applies_theme_and_new_shortcut_immediately()
    {
        await using var app = await TestApp.StartAsync();
        var window = await OpenAsync(app);
        window.Settings.Theme = ThemeChoice.Dark;
        Record(window, Row(window, CommandIds.FileMkdir), Key.N, RawInputModifiers.Control | RawInputModifiers.Meta,
            PhysicalKey.N);

        Click(window, Named<Button>(window, "Ok"));

        Assert.Empty(app.Windows.Open);
        Assert.Equal(ThemeVariant.Dark, Application.Current!.RequestedThemeVariant);
        Assert.Equal(
            [new KeyBindingOverride(CommandIds.FileMkdir, ["F7", "Cmd+7", "Ctrl+Cmd+N"])],
            app.Main.Settings.Current.KeyBindings.Select(b => new KeyBindingOverride(b.CommandId, b.Chords.ToArray())),
            new OverrideComparer());

        app.Dialogs.NameAnswers.Enqueue(null);
        Assert.True(await app.Main.HandleKeyAsync(KeyChord.Parse("Ctrl+Cmd+N")));
        Assert.Single(app.Dialogs.NameRequests);
        Assert.Empty(app.Failures);
    }

    [AvaloniaFact]
    public async Task Apply_keeps_the_window_open_and_an_error_blocks_it()
    {
        await using var app = await TestApp.StartAsync();
        var window = await OpenAsync(app);
        window.Settings.AddFileColor(); // no mask yet

        Click(window, Named<Button>(window, "Apply"));

        var error = Named<TextBlock>(window, "ErrorText");
        Assert.True(error.IsVisible);
        Assert.Same(AppSettings.Default, app.Main.Settings.Current);

        window.Settings.FileColors[0].Mask = "*.txt";
        Click(window, Named<Button>(window, "Apply"));
        Assert.False(error.IsVisible);
        Assert.True(window.IsVisible);
        Assert.Equal([new FileColorRule("*.txt", "Red")], app.Main.Settings.Current.FileColors);

        Click(window, Named<Button>(window, "Cancel"));
        Assert.Empty(app.Windows.Open);
    }

    [AvaloniaFact]
    public async Task Cancel_discards_changes()
    {
        await using var app = await TestApp.StartAsync();
        var window = await OpenAsync(app);
        window.Settings.FontSize = 20;
        window.Settings.ResetAllShortcuts();
        window.Settings.RemoveChord(Row(window, CommandIds.FileCopy), KeyChord.Parse("F5"));

        Click(window, Named<Button>(window, "Cancel"));

        Assert.Empty(app.Windows.Open);
        Assert.Same(AppSettings.Default, app.Main.Settings.Current);
        Assert.Equal(CommandIds.FileCopy, app.Main.KeyMap.Find(KeyChord.Parse("F5")));
    }

    [AvaloniaFact]
    public async Task Fields_of_the_tabs_edit_the_model()
    {
        await using var app = await TestApp.StartAsync(settings: AppSettings.Default with
        {
            FileColors = [new FileColorRule("*.md", "Green")],
            Buttons = [new UserButton("Zip", "/usr/bin/zip", "%s")],
        });
        var window = await OpenAsync(app);
        var tabs = Named<TabControl>(window, "Tabs");
        var vm = window.Settings;

        tabs.SelectedIndex = 1;
        Pump(window);
        Named<ComboBox>(window, "ThemeChoice").SelectedIndex = 2;
        Assert.Equal(ThemeChoice.Dark, vm.Theme);
        Named<NumericUpDown>(window, "FontSizeField").Value = 18;
        Assert.Equal(18, vm.FontSize);
        vm.RowHeight = 30;
        Pump(window);
        Assert.Equal(30m, Named<NumericUpDown>(window, "RowHeightField").Value);
        var colors = Named<ItemsControl>(window, "FileColors");
        var colorBox = colors.GetVisualDescendants().OfType<ComboBox>().Single();
        Assert.Equal("Green", ((ColorOption)colorBox.SelectedItem!).Value);
        colorBox.SelectedIndex = 0;
        Assert.Equal("Red", vm.FileColors[0].Color);
        Click(window, colors.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("remove")));
        Assert.Empty(vm.FileColors);

        tabs.SelectedIndex = 2;
        Pump(window);
        var left = Named<ItemsControl>(window, "LeftColumns");
        Assert.Equal(4, left.GetVisualDescendants().OfType<CheckBox>().Count());
        Click(window, left.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("moveDown")));
        Assert.Equal(ColumnId.Size, vm.LeftColumns[0].Column);
        Assert.Equal(ColumnId.Extension, vm.RightColumns[0].Column);

        tabs.SelectedIndex = 3;
        Pump(window);
        var buttons = Named<ItemsControl>(window, "Buttons");
        Assert.Equal(["Zip", "/usr/bin/zip", "%s"],
            buttons.GetVisualDescendants().OfType<TextBox>().Select(t => t.Text));
        Click(window, Named<Button>(window, "AddButton"));
        Assert.Equal(2, vm.Buttons.Count);

        tabs.SelectedIndex = 0;
        Pump(window);
        vm.SelectedShortcut = vm.Shortcuts.Single(r => r.CommandId == CommandIds.FileView);
        Pump(window);
        var chords = Named<ItemsControl>(window, "SelectedChords");
        Click(window, chords.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("removeChord")));
        Assert.Equal([KeyChord.Parse("Cmd+3")], vm.SelectedShortcut!.Chords);
        Click(window, Named<Button>(window, "ResetShortcut"));
        Assert.False(vm.SelectedShortcut.IsChanged);
        Named<TextBox>(window, "ShortcutFilter").Text = "F3";
        Pump(window);
        Assert.Equal([CommandIds.FileView], vm.VisibleShortcuts.Select(r => r.CommandId));
    }

    [AvaloniaFact]
    public async Task Unbound_command_on_the_F_key_bar_shows_no_key()
    {
        await using var app = await TestApp.StartAsync(settings: AppSettings.Default with
        {
            KeyBindings = [new KeyBindingOverride(CommandIds.FileMkdir, [])],
        });
        var bar = app.Window.GetVisualDescendants().OfType<Tandem.App.Views.FKeyBar>().Single();
        var mkdir = bar.GetVisualDescendants().OfType<Button>().Single(b => b.Tag as string == CommandIds.FileMkdir);
        var copy = bar.GetVisualDescendants().OfType<Button>().Single(b => b.Tag as string == CommandIds.FileCopy);

        Assert.Equal(["Folder"], mkdir.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsVisible)
            .Select(t => t.Text));
        Assert.Equal(["F5", "Copy"], copy.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsVisible)
            .Select(t => t.Text));
    }

    private sealed class OverrideComparer : IEqualityComparer<KeyBindingOverride>
    {
        public bool Equals(KeyBindingOverride? x, KeyBindingOverride? y) =>
            x?.CommandId == y?.CommandId && (x?.Chords ?? []).SequenceEqual(y?.Chords ?? []);

        public int GetHashCode(KeyBindingOverride obj) => obj.CommandId.GetHashCode();
    }
}
