using Avalonia.Input;
using Tandem.App.Commands;
using Tandem.App.Input;

namespace Tandem.App.Tests.Commands;

public class KeyMapWindowsTests
{
    private static readonly KeyMap Windows = KeyMap.ForWindows(KeyMap.Default);

    private static IReadOnlyList<string> Chords(string commandId) =>
        Windows.ChordsFor(commandId).Select(c => c.ToString()).ToList();

    [Fact]
    public void No_chord_uses_the_windows_key() =>
        Assert.DoesNotContain(Windows.Bindings, b => b.Chord.Modifiers.HasFlag(KeyModifiers.Meta));

    [Fact]
    public void Every_command_of_the_mac_map_keeps_a_chord()
    {
        foreach (var id in KeyMap.Default.Bindings.Select(b => b.CommandId).Distinct())
            Assert.True(Windows.ChordsFor(id).Count > 0, id);
    }

    [Fact]
    public void Every_chord_is_assignable_on_windows()
    {
        foreach (var (id, chord) in Windows.Bindings)
            Assert.True(KeyChordRules.RefusalOf(chord, windows: true) is null, $"{id}: {chord}");
    }

    [Theory]
    [InlineData(CommandIds.FileCopy, "F5", "Ctrl+5")]
    [InlineData(CommandIds.FileDelete, "F8", "Ctrl+Back", "Delete")]
    [InlineData(CommandIds.FileDeletePermanent, "Shift+F8", "Ctrl+Alt+Back", "Shift+Delete")]
    [InlineData(CommandIds.PanelRefresh, "Ctrl+R")]
    [InlineData(CommandIds.PanelSwap, "Ctrl+U")]
    [InlineData(CommandIds.TabNew, "Ctrl+T")]
    [InlineData(CommandIds.TabNext, "Ctrl+Tab")]
    [InlineData(CommandIds.FavoritesShow, "Ctrl+D")]
    [InlineData(CommandIds.SortByName, "Ctrl+F3")]
    [InlineData(CommandIds.SortByExtension, "Ctrl+F4")]
    [InlineData(CommandIds.SortByDate, "Ctrl+F5")]
    [InlineData(CommandIds.SortBySize, "Ctrl+F6")]
    [InlineData(CommandIds.SortByMode, "Ctrl+F7")]
    [InlineData(CommandIds.SortReverse, "Ctrl+Alt+R")]
    [InlineData(CommandIds.TabTogglePin, "Ctrl+Alt+P")]
    [InlineData(CommandIds.SearchFiles, "Alt+F7")]
    [InlineData(CommandIds.NetConnect, "Ctrl+K", "Ctrl+F")]
    [InlineData(CommandIds.ToolsMultiRename, "Ctrl+M")]
    public void Windows_chords(string commandId, params string[] expected) =>
        Assert.Equal(expected, Chords(commandId));

    [Theory]
    [InlineData("Alt+F4")]
    [InlineData("Alt+Tab")]
    [InlineData("Alt+Space")]
    [InlineData("Ctrl+Escape")]
    [InlineData("Ctrl+Shift+Escape")]
    [InlineData("Cmd+E")]
    public void Windows_keeps_its_own_chords(string chord) =>
        Assert.Equal(ChordRefusal.System, KeyChordRules.RefusalOf(KeyChord.Parse(chord), windows: true));

    [Fact]
    public void Mac_rules_are_unchanged_by_the_windows_ones() =>
        Assert.Null(KeyChordRules.RefusalOf(KeyChord.Parse("Alt+F4"), windows: false));

    [Theory]
    [InlineData("Ctrl+C", false)]
    [InlineData("Ctrl+V", false)]
    [InlineData("Ctrl+Left", false)]
    [InlineData("Ctrl+Back", false)]
    [InlineData("Ctrl+Alt+Q", false)] // AltGr types characters on many layouts
    [InlineData("Ctrl+R", true)]
    [InlineData("F5", true)]
    public void Windows_editing_keeps_its_own_chords(string chord, bool routed) =>
        Assert.Equal(routed, KeyRouting.ShouldRouteWhileEditing(KeyChord.Parse(chord), windows: true));

    [Theory]
    [InlineData("Ctrl+Shift+F6", "Ctrl+Shift+F6")]
    [InlineData("Alt+F7", "Alt+F7")]
    [InlineData("Ctrl+OemComma", "Ctrl+,")]
    [InlineData("Shift+Delete", "Shift+Del")]
    [InlineData("Ctrl+Back", "Ctrl+Backspace")]
    public void Windows_text_spells_modifiers_out(string chord, string expected) =>
        Assert.Equal(expected, KeyChordText.Format(KeyChord.Parse(chord), windows: true));
}
