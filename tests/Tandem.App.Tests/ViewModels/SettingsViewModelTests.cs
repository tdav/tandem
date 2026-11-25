using Avalonia.Input;
using Tandem.App.Commands;
using Tandem.App.Services;
using Tandem.App.ViewModels.Settings;
using Tandem.Core.Settings;

namespace Tandem.App.Tests.ViewModels;

public sealed class SettingsViewModelTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "tandem-settings-vm-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private SettingsService Service(AppSettings? settings = null)
    {
        var store = new JsonSettingsStore(_directory);
        if (settings is not null) store.Save(settings);
        var service = new SettingsService(store);
        service.Load();
        return service;
    }

    private string SettingsFile => Path.Combine(_directory, "settings.json");

    private static KeyChord Chord(string text) => KeyChord.Parse(text);

    private static ShortcutRow Row(SettingsViewModel vm, string id) => vm.Shortcuts.Single(r => r.CommandId == id);

    [Fact]
    public void Lists_every_command_with_label_and_current_chords()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "macOS chords (Cmd).");
        var service = Service(AppSettings.Default with
        {
            KeyBindings = [new KeyBindingOverride(CommandIds.FileCopy, ["Ctrl+Cmd+K"])],
        });
        var vm = new SettingsViewModel(service);

        Assert.Equal(CommandIds.All, vm.Shortcuts.Select(r => r.CommandId));
        var copy = Row(vm, CommandIds.FileCopy);
        Assert.Equal("Copy…", copy.Label);
        Assert.Equal([Chord("Ctrl+Cmd+K")], copy.Chords);
        Assert.True(copy.IsChanged);
        var view = Row(vm, CommandIds.FileView);
        Assert.Equal([Chord("F3"), Chord("Cmd+3")], view.Chords);
        Assert.False(view.IsChanged);
        Assert.Equal("F3, ⌘3", view.ChordsText);
        Assert.Equal([Chord("Cmd+OemComma")], Row(vm, CommandIds.AppSettings).Chords);
        Assert.Empty(Row(vm, CommandIds.ShellReveal).Chords);
    }

    [Fact]
    public void Rows_come_from_the_effective_key_map_when_given()
    {
        var service = Service();
        var map = KeyMap.Default.WithOverrides([new KeyBindingOverride(CommandIds.FileMove, ["F5"])]);
        var vm = new SettingsViewModel(service, map);

        Assert.Equal([Chord("F5")], Row(vm, CommandIds.FileMove).Chords);
        Assert.Equal([Chord("Cmd+5")], Row(vm, CommandIds.FileCopy).Chords);
    }

    [Fact]
    public void Assign_adds_a_chord_and_marks_the_row_changed()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "macOS chords (Cmd).");
        var vm = new SettingsViewModel(Service());
        var mkdir = Row(vm, CommandIds.FileMkdir);
        var changed = new List<string?>();
        mkdir.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Assert.Equal(ChordAssignment.Assigned, vm.Assign(mkdir, Chord("Ctrl+Cmd+N")));

        Assert.Equal([Chord("F7"), Chord("Cmd+7"), Chord("Ctrl+Cmd+N")], mkdir.Chords);
        Assert.True(mkdir.IsChanged);
        Assert.Contains(nameof(ShortcutRow.Chords), changed);
        Assert.Contains(nameof(ShortcutRow.IsChanged), changed);
        // Assigning a chord the row already has changes nothing.
        Assert.Equal(ChordAssignment.Assigned, vm.Assign(mkdir, Chord("F7")));
        Assert.Equal(3, mkdir.Chords.Count);
    }

    [Fact]
    public void Unassignable_chord_is_refused()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "macOS chords (Cmd).");
        var vm = new SettingsViewModel(Service());
        var copy = Row(vm, CommandIds.FileCopy);

        Assert.Equal(ChordAssignment.NotAssignable, vm.Assign(copy, new KeyChord(Key.K, KeyModifiers.None)));
        Assert.Equal(ChordAssignment.NotAssignable, vm.Assign(copy, Chord("Cmd+Q"), takeFromOther: true));

        Assert.Equal([Chord("F5"), Chord("Cmd+5")], copy.Chords);
        Assert.False(copy.IsChanged);
    }

    [Fact]
    public void Taken_chord_reports_owner_and_take_moves_it()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "macOS chords (Cmd).");
        var vm = new SettingsViewModel(Service());
        var move = Row(vm, CommandIds.FileMove);
        var copy = Row(vm, CommandIds.FileCopy);

        Assert.Equal(ChordAssignment.TakenByOther, vm.Assign(move, Chord("F5")));
        Assert.Same(copy, vm.OwnerOf(Chord("F5")));
        Assert.Equal([Chord("F6"), Chord("Cmd+6")], move.Chords);

        Assert.Equal(ChordAssignment.Assigned, vm.Assign(move, Chord("F5"), takeFromOther: true));

        Assert.Same(move, vm.OwnerOf(Chord("F5")));
        Assert.DoesNotContain(Chord("F5"), copy.Chords);
        Assert.True(copy.IsChanged);
        Assert.Null(vm.OwnerOf(Chord("Cmd+Shift+F12")));
    }

    [Fact]
    public void RemoveChord_unbinds_only_that_chord()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "macOS chords (Cmd).");
        var vm = new SettingsViewModel(Service());
        var view = Row(vm, CommandIds.FileView);

        vm.RemoveChord(view, Chord("F3"));

        Assert.Equal([Chord("Cmd+3")], view.Chords);
        Assert.Null(vm.OwnerOf(Chord("F3")));
        Assert.True(view.IsChanged);
    }

    [Fact]
    public void Reset_one_restores_that_commands_defaults()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "macOS chords (Cmd).");
        var vm = new SettingsViewModel(Service());
        var move = Row(vm, CommandIds.FileMove);
        var copy = Row(vm, CommandIds.FileCopy);
        vm.Assign(move, Chord("F5"), takeFromOther: true);
        vm.RemoveChord(copy, Chord("Cmd+5"));

        vm.ResetShortcut(copy);

        Assert.Equal([Chord("F5"), Chord("Cmd+5")], copy.Chords);
        Assert.False(copy.IsChanged);
        // Its default chord comes back from the command that took it.
        Assert.Equal([Chord("F6"), Chord("Cmd+6")], move.Chords);
        Assert.False(move.IsChanged);
    }

    [Fact]
    public void Reset_all_then_Apply_saves_no_overrides()
    {
        var service = Service(AppSettings.Default with
        {
            KeyBindings =
            [
                new KeyBindingOverride(CommandIds.FileCopy, ["Ctrl+Cmd+K"]),
                new KeyBindingOverride(CommandIds.FileMkdir, []),
            ],
        });
        var vm = new SettingsViewModel(service);

        vm.ResetAllShortcuts();

        Assert.All(vm.Shortcuts, r => Assert.False(r.IsChanged, r.CommandId));
        Assert.True(vm.Apply());
        Assert.Empty(service.Current.KeyBindings);
        Assert.Empty(new JsonSettingsStore(_directory).Load().KeyBindings);
    }

    [Fact]
    public void Apply_saves_only_differences_from_defaults()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "macOS chords (Cmd).");
        var service = Service();
        var vm = new SettingsViewModel(service);
        var changes = 0;
        service.Changed += (_, _) => changes++;

        vm.Assign(Row(vm, CommandIds.FileMove), Chord("F5"), takeFromOther: true);
        vm.Assign(Row(vm, CommandIds.FileMkdir), Chord("Ctrl+Cmd+N"));
        vm.Theme = ThemeChoice.Dark;
        vm.ExternalEditor = "  ";

        Assert.True(vm.Apply());

        Assert.Equal(1, changes);
        var saved = new JsonSettingsStore(_directory).Load();
        Assert.Equal(ThemeChoice.Dark, saved.Theme);
        Assert.Null(saved.ExternalEditor);
        Assert.Equal(
            [
                (CommandIds.FileCopy, new[] { "Cmd+5" }),
                (CommandIds.FileMkdir, new[] { "F7", "Cmd+7", "Ctrl+Cmd+N" }),
                (CommandIds.FileMove, new[] { "F6", "Cmd+6", "F5" }),
            ],
            saved.KeyBindings.Select(b => (b.CommandId, b.Chords.ToArray())).OrderBy(b => b.CommandId, StringComparer.Ordinal));
        var map = KeyMap.Default.WithOverrides(saved.KeyBindings);
        Assert.Equal(CommandIds.FileMove, map.Find(Chord("F5")));
    }

    [Fact]
    public void Filter_matches_label_and_chord_text()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "macOS chords (Cmd).");
        var vm = new SettingsViewModel(Service());
        Assert.Equal(vm.Shortcuts.Count, vm.VisibleShortcuts.Count);

        vm.ShortcutFilter = "cOPy";
        Assert.Equal([CommandIds.FileCopy], vm.VisibleShortcuts.Select(r => r.CommandId));

        vm.ShortcutFilter = "Shift+F8";
        Assert.Equal([CommandIds.FileDeletePermanent], vm.VisibleShortcuts.Select(r => r.CommandId));
        vm.ShortcutFilter = "⇧F8"; // as shown
        Assert.Equal([CommandIds.FileDeletePermanent], vm.VisibleShortcuts.Select(r => r.CommandId));

        // A chord assigned now is found by its text.
        vm.Assign(Row(vm, CommandIds.FileMkdir), Chord("Ctrl+Cmd+N"));
        vm.ShortcutFilter = "ctrl+cmd+n";
        Assert.Equal([CommandIds.FileMkdir], vm.VisibleShortcuts.Select(r => r.CommandId));

        vm.ShortcutFilter = "";
        Assert.Equal(vm.Shortcuts.Count, vm.VisibleShortcuts.Count);
    }

    [Fact]
    public void Empty_mask_or_button_without_program_blocks_Apply()
    {
        var service = Service();
        var vm = new SettingsViewModel(service);
        var changes = 0;
        service.Changed += (_, _) => changes++;

        var rule = vm.AddFileColor();
        Assert.NotNull(vm.Validate());
        Assert.False(vm.Apply());
        Assert.NotNull(vm.Error);
        rule.Mask = "*.jpg";
        rule.Color = "Teal";
        Assert.Null(vm.Validate());

        var button = vm.AddButton();
        button.Title = "Zip";
        Assert.NotNull(vm.Validate());
        Assert.False(vm.Apply());
        button.Program = "/usr/bin/zip";
        button.Arguments = "-r x.zip %s";

        vm.FontSize = AppSettings.MaxFontSize + 1;
        Assert.NotNull(vm.Validate());
        vm.FontSize = 16;
        vm.RowHeight = AppSettings.MinRowHeight - 1;
        Assert.NotNull(vm.Validate());
        vm.RowHeight = 30;
        vm.LeftColumns[0].Width = AppSettings.MaxColumnWidth + 1;
        Assert.NotNull(vm.Validate());
        vm.LeftColumns[0].Width = 60;

        Assert.Equal(0, changes);
        Assert.True(vm.Apply());
        Assert.Null(vm.Error);
        Assert.Equal(1, changes);
        var current = service.Current;
        Assert.Equal([new FileColorRule("*.jpg", "Teal")], current.FileColors);
        Assert.Equal([new UserButton("Zip", "/usr/bin/zip", "-r x.zip %s")], current.Buttons);
        Assert.Equal(16, current.FontSize);
        Assert.Equal(30, current.RowHeight);
    }

    [Fact]
    public void Rows_of_lists_move_and_are_removed()
    {
        var vm = new SettingsViewModel(Service(AppSettings.Default with
        {
            FileColors = [new FileColorRule("*.a", "Red"), new FileColorRule("*.b", "Blue")],
            Buttons = [new UserButton("One", "/bin/one", ""), new UserButton("Two", "/bin/two", "")],
        }));

        vm.MoveDown(vm.FileColors[0]);
        Assert.Equal(["*.b", "*.a"], vm.FileColors.Select(r => r.Mask));
        vm.MoveDown(vm.FileColors[1]); // already last
        vm.MoveUp(vm.FileColors[0]); // already first
        Assert.Equal(["*.b", "*.a"], vm.FileColors.Select(r => r.Mask));
        vm.Remove(vm.FileColors[0]);
        Assert.Equal(["*.a"], vm.FileColors.Select(r => r.Mask));

        vm.MoveUp(vm.Buttons[1]);
        Assert.Equal(["Two", "One"], vm.Buttons.Select(b => b.Title));
        vm.Remove(vm.Buttons[1]);
        Assert.Equal(["Two"], vm.Buttons.Select(b => b.Title));

        Assert.True(vm.Apply());
    }

    [Fact]
    public void Column_order_and_visibility_roundtrip_per_panel()
    {
        var service = Service();
        var vm = new SettingsViewModel(service);
        Assert.Equal(AppSettings.DefaultColumns.Select(c => c.Column), vm.LeftColumns.Select(c => c.Column));

        vm.MoveDown(vm.LeftColumns[0]); // Size, Extension, Modified, Mode
        vm.LeftColumns.Single(c => c.Column == ColumnId.Mode).Visible = false;
        vm.MoveUp(vm.RightColumns[3]); // Extension, Size, Mode, Modified
        vm.RightColumns[0].Width = 70;
        Assert.True(vm.Apply());

        var saved = new JsonSettingsStore(_directory).Load();
        Assert.Equal(
            [
                new ColumnSetting(ColumnId.Size, true, 84), new ColumnSetting(ColumnId.Extension, true, 56),
                new ColumnSetting(ColumnId.Modified, true, 124), new ColumnSetting(ColumnId.Mode, false, 84),
            ],
            saved.LeftColumns);
        Assert.Equal(
            [
                new ColumnSetting(ColumnId.Extension, true, 70), new ColumnSetting(ColumnId.Size, true, 84),
                new ColumnSetting(ColumnId.Mode, true, 84), new ColumnSetting(ColumnId.Modified, true, 124),
            ],
            saved.RightColumns);

        var again = new SettingsViewModel(Service());
        Assert.Equal(saved.LeftColumns, again.LeftColumns.Select(c => c.ToSetting()));
        Assert.Equal(saved.RightColumns, again.RightColumns.Select(c => c.ToSetting()));
    }

    [Fact]
    public void Discarded_view_model_changes_nothing()
    {
        var service = Service(AppSettings.Default with { FontSize = 15 });
        var before = service.Current;
        var written = File.GetLastWriteTimeUtc(SettingsFile);
        var changes = 0;
        service.Changed += (_, _) => changes++;

        var vm = new SettingsViewModel(service);
        vm.Theme = ThemeChoice.Light;
        vm.FontSize = 20;
        vm.Assign(Row(vm, CommandIds.FileMove), Chord("F5"), takeFromOther: true);
        vm.ResetAllShortcuts();
        vm.AddButton();
        vm.LeftColumns[0].Visible = false;
        vm.ExternalEditor = "/Applications/X.app";

        Assert.Same(before, service.Current);
        Assert.Equal(0, changes);
        Assert.Equal(written, File.GetLastWriteTimeUtc(SettingsFile));
        Assert.Equal(15, new JsonSettingsStore(_directory).Load().FontSize);
    }

    [Fact]
    public void Starts_from_the_current_settings()
    {
        var vm = new SettingsViewModel(Service(AppSettings.Default with
        {
            Theme = ThemeChoice.Dark, FontSize = 16, RowHeight = 28, ExternalEditor = "/Applications/E.app",
            FileColors = [new FileColorRule("*.md", "Green")],
        }));

        Assert.Equal(ThemeChoice.Dark, vm.Theme);
        Assert.Equal(16, vm.FontSize);
        Assert.Equal(28, vm.RowHeight);
        Assert.Equal("/Applications/E.app", vm.ExternalEditor);
        Assert.Equal(("*.md", "Green"), (vm.FileColors[0].Mask, vm.FileColors[0].Color));
        Assert.Null(vm.Validate());
    }

    [Theory]
    [InlineData("K", ChordRefusal.NeedsCmdOrCtrl)]
    [InlineData("Down", ChordRefusal.NeedsCmdOrCtrl)]
    [InlineData("Enter", ChordRefusal.PanelKey)]
    [InlineData("Cmd+Q", ChordRefusal.System)]
    [InlineData("Cmd+LWin", ChordRefusal.NoKey)]
    public void Refused_chord_says_why(string chord, ChordRefusal reason)
    {
        Assert.Equal(reason, KeyChordRules.RefusalOf(Chord(chord)));
        var vm = new SettingsViewModel(Service());
        Assert.Equal(ChordAssignment.NotAssignable, vm.Assign(Row(vm, CommandIds.FileCopy), Chord(chord)));
        Assert.Contains(KeyChordText.Format(Chord(chord)), vm.ShortcutMessage);
    }

    [Fact]
    public void Apply_keeps_overrides_of_unknown_commands()
    {
        var service = Service(AppSettings.Default with
        {
            KeyBindings =
            [
                new KeyBindingOverride("future.command", ["Cmd+J"]),
                new KeyBindingOverride(CommandIds.FileCopy, ["F9"]),
            ],
        });
        var vm = new SettingsViewModel(service);

        Assert.True(vm.Apply());

        Assert.Equal(
            [("file.copy", new[] { "F9" }), ("future.command", new[] { "Cmd+J" })],
            new JsonSettingsStore(_directory).Load().KeyBindings.Select(b => (b.CommandId, b.Chords.ToArray()))
                .OrderBy(b => b.CommandId, StringComparer.Ordinal));
    }

    [Fact]
    public void Empty_number_field_blocks_Apply()
    {
        var service = Service();
        var vm = new SettingsViewModel(service);

        vm.FontSize = double.NaN;
        Assert.NotNull(vm.Validate());
        Assert.False(vm.Apply());
        vm.FontSize = 14;
        vm.RowHeight = double.NaN;
        Assert.NotNull(vm.Validate());
        vm.RowHeight = 22;
        vm.RightColumns[1].Width = double.NaN;
        Assert.NotNull(vm.Validate());
        vm.RightColumns[1].Width = 84;

        Assert.Same(AppSettings.Default, service.Current);
        Assert.True(vm.Apply());
    }

    [Fact]
    public void Row_lower_than_the_font_needs_blocks_Apply()
    {
        var service = Service();
        var vm = new SettingsViewModel(service);
        vm.FontSize = 20;
        vm.RowHeight = 25;

        var error = vm.Validate();

        Assert.NotNull(error);
        Assert.Contains("26", error);
        Assert.False(vm.Apply());
        vm.RowHeight = 26;
        Assert.True(vm.Apply());
        Assert.Equal(26, service.Current.RowHeight);
    }

    [Fact]
    public void Edited_row_stays_visible_and_selected_under_a_filter()
    {
        var vm = new SettingsViewModel(Service());
        vm.ShortcutFilter = "F7";
        var mkdir = Row(vm, CommandIds.FileMkdir);
        vm.SelectedShortcut = mkdir;
        var list = vm.VisibleShortcuts;

        vm.RemoveChord(mkdir, Chord("F7"));
        vm.Assign(mkdir, Chord("Ctrl+Cmd+N"));

        Assert.Same(list, vm.VisibleShortcuts);
        Assert.Contains(mkdir, vm.VisibleShortcuts);
        Assert.Same(mkdir, vm.SelectedShortcut);

        // A changed filter shows only what matches.
        vm.ShortcutFilter = "F7 ";
        Assert.DoesNotContain(mkdir, vm.VisibleShortcuts);
    }
}
