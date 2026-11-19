using System.Reflection;
using Tandem.App.Commands;

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
            "panel.switch=Tab", "panel.refresh=Cmd+R", "panel.swap=Cmd+U", "panel.mirror=Cmd+Shift+U",
            "nav.up=Back", "nav.up=Cmd+Up", "nav.back=Cmd+OemOpenBrackets", "nav.forward=Cmd+OemCloseBrackets",
            "path.edit=Cmd+Shift+G", "cmdline.focus=Cmd+L", "select.toggleDown=Space", "select.all=Cmd+A",
            "select.byMask=Cmd+OemPlus", "unselect.byMask=Cmd+OemMinus", "select.invert=Cmd+Shift+A",
            "tab.new=Cmd+T", "tab.close=Cmd+W", "tab.next=Ctrl+Tab", "tab.prev=Ctrl+Shift+Tab",
            "favorites.show=Cmd+D", "view.toggleHidden=Cmd+Shift+OemPeriod",
            "sort.byName=Ctrl+Cmd+1", "sort.byExtension=Ctrl+Cmd+2", "sort.bySize=Ctrl+Cmd+3", "sort.byDate=Ctrl+Cmd+4",
            "sort.byMode=Ctrl+Cmd+5", "sort.reverse=Ctrl+Cmd+R", "tab.togglePin=Ctrl+Cmd+P",
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
            [CommandIds.ShellOpenTerminal, CommandIds.ShellOpenWith, CommandIds.ShellReveal, CommandIds.TabCloseOthers,
                CommandIds.TabDuplicate],
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
}
