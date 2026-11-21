using Avalonia.Input;
using Tandem.App.Commands;

namespace Tandem.App.Tests.Commands;

public class KeyChordRulesTests
{
    [Theory]
    [InlineData("F5", true)]
    [InlineData("Alt+F7", true)]
    [InlineData("Ctrl+Shift+F12", true)]
    [InlineData("Cmd+K", true)]
    [InlineData("Ctrl+Q", true)]
    [InlineData("Tab", true)]
    [InlineData("Shift+Tab", true)]
    [InlineData("Space", true)]
    [InlineData("Alt+Back", true)]
    [InlineData("Delete", true)]
    [InlineData("Insert", true)]
    [InlineData("K", false)]
    [InlineData("Shift+K", false)]
    [InlineData("Alt+K", false)]
    [InlineData("5", false)]
    [InlineData("OemComma", false)]
    [InlineData("Enter", false)]
    [InlineData("Cmd+Enter", false)]
    [InlineData("Escape", false)]
    [InlineData("Down", false)]
    [InlineData("Shift+Home", false)]
    [InlineData("PageDown", false)]
    [InlineData("Cmd+Down", true)]
    [InlineData("Ctrl+End", true)]
    [InlineData("Cmd+Q", false)]
    [InlineData("Cmd+H", false)]
    [InlineData("Cmd+Alt+H", false)]
    [InlineData("Cmd+Tab", false)]
    [InlineData("Cmd+Space", false)]
    [InlineData("Cmd+Alt+Escape", false)]
    [InlineData("Cmd+Shift+Q", false)]
    [InlineData("Ctrl+Cmd+Q", false)]
    [InlineData("Cmd+Shift+3", false)]
    [InlineData("Cmd+Shift+4", false)]
    [InlineData("Cmd+Shift+5", false)]
    [InlineData("Cmd+OemTilde", false)]
    [InlineData("Cmd+Alt+Q", true)]
    [InlineData("Cmd+3", true)]
    [InlineData("Ctrl+Cmd+5", true)]
    [InlineData("Ctrl+H", true)]
    [InlineData("Cmd+LeftShift", false)]
    [InlineData("Ctrl+LWin", false)]
    public void IsAssignable(string chord, bool expected) =>
        Assert.Equal(expected, KeyChordRules.IsAssignable(KeyChord.Parse(chord)));

    [Fact]
    public void A_chord_without_a_key_is_not_assignable() =>
        Assert.False(KeyChordRules.IsAssignable(new KeyChord(Key.None, KeyModifiers.Meta)));

    [Fact]
    public void Every_default_chord_is_assignable() =>
        Assert.All(KeyMap.Default.Bindings, b => Assert.True(KeyChordRules.IsAssignable(b.Chord), b.Chord.ToString()));

    [Fact]
    public void Every_stage_3_chord_is_assignable()
    {
        string[] chords =
        [
            "Cmd+M", "Ctrl+M", "Ctrl+Shift+M", "Cmd+Alt+I", "Cmd+Shift+I", "Shift+F2", "Cmd+Shift+C", "Cmd+Shift+D",
            "Cmd+Shift+S", "Cmd+K", "Ctrl+F", "Cmd+Shift+K", "Ctrl+Shift+F", "Cmd+Alt+K",
        ];

        Assert.Equal(14, chords.Length);
        Assert.All(chords, chord => Assert.True(KeyChordRules.IsAssignable(KeyChord.Parse(chord)), chord));
    }
}
