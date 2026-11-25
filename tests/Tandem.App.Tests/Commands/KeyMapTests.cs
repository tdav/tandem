using System.Reflection;
using Tandem.App.Commands;
using Tandem.Core.Settings;

namespace Tandem.App.Tests.Commands;

public class KeyMapTests
{
    private static IEnumerable<string> ConstantIds() =>
        typeof(CommandIds)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!);

    [Fact]
    public void Default_map_has_no_duplicate_chords()
    {
        var chords = KeyMap.Default.Bindings.Select(b => b.Chord).ToList();

        Assert.Equal(chords.Count, chords.Distinct().Count());
    }

    [Theory]
    [InlineData("F5", "file.copy")]
    [InlineData("Cmd+5", "file.copy")]
    [InlineData("Cmd+Back", "file.delete")]
    [InlineData("Shift+F8", "file.deletePermanent")]
    [InlineData("Tab", "panel.switch")]
    [InlineData("Cmd+Y", "file.quickLook")]
    [InlineData("Cmd+L", "cmdline.focus")]
    [InlineData("Space", "select.toggleDown")]
    [InlineData("Cmd+Alt+Back", "file.deletePermanent")]
    [InlineData("Back", "nav.up")]
    [InlineData("Cmd+OemOpenBrackets", "nav.back")]
    [InlineData("Cmd+OemCloseBrackets", "nav.forward")]
    [InlineData("Cmd+OemPlus", "select.byMask")]
    [InlineData("Cmd+OemMinus", "unselect.byMask")]
    [InlineData("Ctrl+Shift+Tab", "tab.prev")]
    [InlineData("Cmd+Shift+OemPeriod", "view.toggleHidden")]
    [InlineData("Ctrl+Cmd+P", "tab.togglePin")]
    [InlineData("Cmd+OemComma", "app.settings")]
    [InlineData("Cmd+M", "tools.multiRename")]
    [InlineData("Ctrl+M", "tools.multiRename")]
    [InlineData("Ctrl+Shift+M", "tools.undoRename")]
    [InlineData("Cmd+Alt+I", "tools.folderSize")]
    [InlineData("Cmd+Shift+I", "tools.folderSizeAll")]
    [InlineData("Shift+F2", "tools.compareDirs")]
    [InlineData("Cmd+Shift+C", "tools.compareDirs")]
    [InlineData("Cmd+Shift+D", "tools.compareFiles")]
    [InlineData("Cmd+Shift+S", "tools.syncDirs")]
    [InlineData("Cmd+K", "net.connect")]
    [InlineData("Ctrl+F", "net.connect")]
    [InlineData("Cmd+Shift+K", "net.disconnect")]
    [InlineData("Ctrl+Shift+F", "net.disconnect")]
    [InlineData("Cmd+Alt+K", "net.console")]
    [InlineData("Cmd+Alt+C", "file.copyPath")]
    public void Default_binding(string chord, string id) => Assert.Equal(id, KeyMap.Default.Find(KeyChord.Parse(chord)));

    [Fact]
    public void Default_map_has_the_whole_table()
    {
        string[] expected =
        [
            "file.view=F3", "file.view=Cmd+3", "file.edit=F4", "file.edit=Cmd+4", "file.copy=F5", "file.copy=Cmd+5",
            "file.move=F6", "file.move=Cmd+6", "file.mkdir=F7", "file.mkdir=Cmd+7", "file.delete=F8",
            "file.delete=Cmd+Back", "file.deletePermanent=Shift+F8", "file.deletePermanent=Cmd+Alt+Back",
            "file.rename=Shift+F6", "file.rename=F2", "file.rename=Cmd+Shift+R", "file.quickLook=Cmd+Y", "file.properties=Cmd+I",
            "file.copyPath=Cmd+Alt+C",
            "panel.switch=Tab", "panel.refresh=Cmd+R", "panel.swap=Cmd+U", "panel.mirror=Cmd+Shift+U",
            "nav.up=Back", "nav.up=Cmd+Up", "nav.enter=Cmd+Down", "nav.back=Cmd+OemOpenBrackets",
            "nav.forward=Cmd+OemCloseBrackets",
            "path.edit=Cmd+Shift+G", "cmdline.focus=Cmd+L", "select.toggleDown=Space", "select.toggleDown=Insert",
            "select.all=Cmd+A",
            "select.byMask=Cmd+OemPlus", "unselect.byMask=Cmd+OemMinus", "select.invert=Cmd+Shift+A",
            "tab.new=Cmd+T", "tab.close=Cmd+W", "tab.next=Ctrl+Tab", "tab.prev=Ctrl+Shift+Tab",
            "favorites.show=Cmd+D", "view.toggleHidden=Cmd+Shift+OemPeriod", "view.quickView=Ctrl+Q",
            "view.brief=Ctrl+Cmd+B", "view.full=Ctrl+Cmd+D",
            "sort.byName=Ctrl+Cmd+1", "sort.byExtension=Ctrl+Cmd+2", "sort.bySize=Ctrl+Cmd+3", "sort.byDate=Ctrl+Cmd+4",
            "sort.byMode=Ctrl+Cmd+5", "sort.reverse=Ctrl+Cmd+R", "tab.togglePin=Ctrl+Cmd+P",
            "app.settings=Cmd+OemComma",
            "search.files=Alt+F7", "search.files=Cmd+F",
            "archive.pack=Alt+F5", "archive.pack=Cmd+Alt+5", "archive.unpack=Alt+F9", "archive.unpack=Cmd+Alt+9",
            "tools.multiRename=Cmd+M", "tools.multiRename=Ctrl+M", "tools.undoRename=Ctrl+Shift+M",
            "tools.folderSize=Cmd+Alt+I", "tools.folderSizeAll=Cmd+Shift+I",
            "tools.compareDirs=Shift+F2", "tools.compareDirs=Cmd+Shift+C", "tools.compareFiles=Cmd+Shift+D",
            "tools.syncDirs=Cmd+Shift+S",
            "net.connect=Cmd+K", "net.connect=Ctrl+F", "net.disconnect=Cmd+Shift+K", "net.disconnect=Ctrl+Shift+F",
            "net.console=Cmd+Alt+K",
        ];

        var expectedBindings = expected.Select(e => e.Split('=')).Select(p => (p[0], KeyChord.Parse(p[1])));

        Assert.Equal(expectedBindings, KeyMap.Default.Bindings.Select(b => (b.CommandId, b.Chord)));
    }

    [Fact]
    public void ChordsFor_returns_chords_in_table_order()
    {
        Assert.Equal(
            [KeyChord.Parse("Shift+F6"), KeyChord.Parse("F2"), KeyChord.Parse("Cmd+Shift+R")],
            KeyMap.Default.ChordsFor(CommandIds.FileRename));
        Assert.Equal([KeyChord.Parse("F3"), KeyChord.Parse("Cmd+3")], KeyMap.Default.ChordsFor(CommandIds.FileView));
    }

    [Fact]
    public void Menu_only_commands_have_no_chords()
    {
        Assert.Empty(KeyMap.Default.ChordsFor(CommandIds.ShellReveal));
        Assert.Empty(KeyMap.Default.ChordsFor(CommandIds.ShellOpenTerminal));
        Assert.Empty(KeyMap.Default.ChordsFor(CommandIds.ShellOpenWith));
        Assert.Empty(KeyMap.Default.ChordsFor(CommandIds.TabDuplicate));
        Assert.Empty(KeyMap.Default.ChordsFor(CommandIds.TabCloseOthers));
        Assert.Empty(KeyMap.Default.ChordsFor(CommandIds.ToolsChecksumCreate));
        Assert.Empty(KeyMap.Default.ChordsFor(CommandIds.ToolsChecksumVerify));
        Assert.Empty(KeyMap.Default.ChordsFor("no.such.command"));
    }

    [Fact]
    public void Every_F_key_command_also_has_a_chord_without_an_F_key()
    {
        // Apple keyboards send media keys for F1–F12 unless fn is held, so an F-key alone is not enough.
        static bool IsFKey(KeyChord chord) => chord.Key is >= Avalonia.Input.Key.F1 and <= Avalonia.Input.Key.F24;

        var commandsWithFKeys = KeyMap.Default.Bindings.Where(b => IsFKey(b.Chord)).Select(b => b.CommandId).Distinct();

        Assert.All(commandsWithFKeys, id => Assert.Contains(KeyMap.Default.ChordsFor(id), c => !IsFKey(c)));
    }

    [Theory]
    [InlineData("Ctrl+Cmd+1", "sort.byName")]
    [InlineData("Ctrl+Cmd+2", "sort.byExtension")]
    [InlineData("Ctrl+Cmd+3", "sort.bySize")]
    [InlineData("Ctrl+Cmd+4", "sort.byDate")]
    [InlineData("Ctrl+Cmd+5", "sort.byMode")]
    [InlineData("Ctrl+Cmd+R", "sort.reverse")]
    public void Default_sort_chords_resolve(string chord, string id)
    {
        Assert.Equal(id, KeyMap.Default.Find(KeyChord.Parse(chord)));
        Assert.Equal(chord, KeyMap.Default.ChordsFor(id)[0].ToString()); // the canonical spelling, shown in menus
    }

    [Fact]
    public void No_default_chord_uses_Ctrl_with_an_F_key()
    {
        // macOS keeps Ctrl+F1…F12 for moving the keyboard focus (menu bar, Dock, toolbars): the app never sees them.
        Assert.DoesNotContain(KeyMap.Default.Bindings, b =>
            b.Chord.Modifiers.HasFlag(Avalonia.Input.KeyModifiers.Control) &&
            b.Chord.Key is >= Avalonia.Input.Key.F1 and <= Avalonia.Input.Key.F24);
    }

    [Fact]
    public void Unbound_chord_finds_nothing() => Assert.Null(KeyMap.Default.Find(KeyChord.Parse("Cmd+Shift+F12")));

    [Fact]
    public void Every_default_binding_refers_to_a_CommandIds_constant()
    {
        var ids = ConstantIds().ToHashSet();

        Assert.All(KeyMap.Default.Bindings, b => Assert.Contains(b.CommandId, ids));
    }

    [Fact]
    public void Every_command_except_menu_only_ones_has_a_default_chord()
    {
        var bound = KeyMap.Default.Bindings.Select(b => b.CommandId).ToHashSet();
        var unbound = ConstantIds().Where(id => !bound.Contains(id)).Order();

        Assert.Equal(
            [CommandIds.NetConnectSsh, CommandIds.NetShowConsole, CommandIds.ShellOpenTerminal, CommandIds.ShellOpenWith, CommandIds.ShellReveal,
                CommandIds.TabCloseOthers, CommandIds.TabDuplicate, CommandIds.ToolsChecksumCreate, CommandIds.ToolsChecksumVerify],
            unbound);
    }

    [Fact]
    public void CommandIds_All_matches_the_constants_without_duplicates()
    {
        Assert.Equal(CommandIds.All.Count, CommandIds.All.Distinct().Count());
        Assert.Equal(ConstantIds().Order(), CommandIds.All.Order());
    }

    [Fact]
    public void Map_with_a_duplicate_chord_is_rejected() =>
        Assert.Throws<ArgumentException>(() => new KeyMap([("a", KeyChord.Parse("F5")), ("b", KeyChord.Parse("F5"))]));

    [Fact]
    public void Map_is_a_copy_of_its_input()
    {
        var bindings = new List<(string, KeyChord)> { ("a", KeyChord.Parse("F5")) };
        var map = new KeyMap(bindings);
        bindings.Add(("b", KeyChord.Parse("F6")));

        Assert.Single(map.Bindings);
        Assert.Null(map.Find(KeyChord.Parse("F6")));
    }

    // --- Overrides from the settings ---

    private static KeyChord C(string text) => KeyChord.Parse(text);

    private static KeyMap Override(params KeyBindingOverride[] overrides) => KeyMap.Default.WithOverrides(overrides);

    [Fact]
    public void Default_map_binds_Insert_to_select_toggleDown()
    {
        Assert.Equal(CommandIds.SelectToggleDown, KeyMap.Default.Find(C("Insert")));
        Assert.Equal([C("Space"), C("Insert")], KeyMap.Default.ChordsFor(CommandIds.SelectToggleDown));
    }

    [Fact]
    public void Override_replaces_chords_of_that_command_only()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "macOS chords (Cmd); KeyMapWindowsTests covers Windows.");
        var map = Override(new KeyBindingOverride(CommandIds.FileCopy, ["Ctrl+Cmd+K", "Shift+F5"]));

        Assert.Equal([C("Ctrl+Cmd+K"), C("Shift+F5")], map.ChordsFor(CommandIds.FileCopy));
        Assert.Null(map.Find(C("F5")));
        Assert.Null(map.Find(C("Cmd+5")));
        // Every other command keeps its chords, and the table its order.
        var others = KeyMap.Default.Bindings.Where(b => b.CommandId != CommandIds.FileCopy);
        Assert.Equal(others, map.Bindings.Where(b => b.CommandId != CommandIds.FileCopy));
        Assert.Equal(KeyMap.Default.Bindings.ToList().FindIndex(b => b.CommandId == CommandIds.FileCopy),
            map.Bindings.ToList().FindIndex(b => b.CommandId == CommandIds.FileCopy));
    }

    [Fact]
    public void Override_takes_a_chord_away_from_its_default_owner()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "macOS chords (Cmd); KeyMapWindowsTests covers Windows.");
        var map = Override(new KeyBindingOverride(CommandIds.FileCopy, ["Ctrl+Cmd+P"]));

        Assert.Equal(CommandIds.FileCopy, map.Find(C("Ctrl+Cmd+P")));
        Assert.Empty(map.ChordsFor(CommandIds.TabTogglePin));
    }

    [Fact]
    public void Empty_chord_list_unbinds_the_command()
    {
        var map = Override(new KeyBindingOverride(CommandIds.ViewQuickView, []));

        Assert.Empty(map.ChordsFor(CommandIds.ViewQuickView));
        Assert.Null(map.Find(C("Ctrl+Q")));
    }

    [Fact]
    public void Unknown_command_and_bad_chord_are_ignored()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "macOS chords (Cmd); KeyMapWindowsTests covers Windows.");
        var map = Override(
            new KeyBindingOverride("no.such.command", ["F5"]),
            new KeyBindingOverride(CommandIds.FileMove, ["Cmd+K", "Bogus+Q", "", null!, "K", "Enter", "Cmd+K"]),
            new KeyBindingOverride(CommandIds.FileEdit, ["Cmd+Cmd+E", "Down"]),
            new KeyBindingOverride(CommandIds.FileView, null!),
            null!);

        Assert.Equal(CommandIds.FileCopy, map.Find(C("F5")));
        Assert.Equal([C("Cmd+K")], map.ChordsFor(CommandIds.FileMove));
        // An override none of whose chords can be used, or without a list at all, changes nothing.
        Assert.Equal(KeyMap.Default.ChordsFor(CommandIds.FileEdit), map.ChordsFor(CommandIds.FileEdit));
        Assert.Equal(KeyMap.Default.ChordsFor(CommandIds.FileView), map.ChordsFor(CommandIds.FileView));
        Assert.Null(map.Find(C("K")));
    }

    [Fact]
    public void Later_override_wins_a_disputed_chord()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "macOS chords (Cmd); KeyMapWindowsTests covers Windows.");
        var map = Override(
            new KeyBindingOverride(CommandIds.FileCopy, ["Cmd+K", "Shift+F5"]),
            new KeyBindingOverride(CommandIds.FileMove, ["Cmd+K"]));

        Assert.Equal(CommandIds.FileMove, map.Find(C("Cmd+K")));
        Assert.Equal([C("Shift+F5")], map.ChordsFor(CommandIds.FileCopy));
        Assert.Equal([C("Cmd+K")], map.ChordsFor(CommandIds.FileMove));
    }

    [Fact]
    public void Override_can_bind_a_menu_only_command()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "macOS chords (Cmd); KeyMapWindowsTests covers Windows.");
        var map = Override(new KeyBindingOverride(CommandIds.ShellReveal, ["Cmd+Alt+R"]));

        Assert.Equal(CommandIds.ShellReveal, map.Find(C("Cmd+Alt+R")));
    }

    [Fact]
    public void OverridesAgainst_lists_only_changed_commands()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "macOS chords (Cmd); KeyMapWindowsTests covers Windows.");
        Assert.Empty(KeyMap.Default.OverridesAgainst(KeyMap.Default));

        var map = Override(new KeyBindingOverride(CommandIds.FileCopy, ["Ctrl+Cmd+P"]));
        var overrides = map.OverridesAgainst(KeyMap.Default);

        Assert.Equal([CommandIds.FileCopy, CommandIds.TabTogglePin], overrides.Select(o => o.CommandId).Order());
        Assert.Equal(["Ctrl+Cmd+P"], overrides.Single(o => o.CommandId == CommandIds.FileCopy).Chords);
        Assert.Empty(overrides.Single(o => o.CommandId == CommandIds.TabTogglePin).Chords);
    }

    [Fact]
    public void OverridesAgainst_roundtrips_through_WithOverrides()
    {
        var map = Override(
            new KeyBindingOverride(CommandIds.FileCopy, ["Ctrl+Cmd+P", "Shift+F5"]),
            new KeyBindingOverride(CommandIds.ViewQuickView, []),
            new KeyBindingOverride(CommandIds.ShellReveal, ["Cmd+Alt+R"]),
            new KeyBindingOverride(CommandIds.TabDuplicate, ["Cmd+Alt+T"]),
            new KeyBindingOverride(CommandIds.FileRename, ["F2", "Shift+F6"]));

        var again = KeyMap.Default.WithOverrides(map.OverridesAgainst(KeyMap.Default));

        Assert.Equal(map.Bindings, again.Bindings);
        Assert.Equal(["F2", "Shift+F6"],
            map.OverridesAgainst(KeyMap.Default).Single(o => o.CommandId == CommandIds.FileRename).Chords);
    }
}
