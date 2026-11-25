using Tandem.App.Commands;

namespace Tandem.App.Tests.Commands;

public class KeyChordTextTests
{
    [Theory]
    [InlineData("F5", "F5")]
    [InlineData("Shift+F8", "⇧F8")]
    [InlineData("Cmd+OemComma", "⌘,")]
    [InlineData("Cmd+Back", "⌘⌫")]
    [InlineData("Cmd+Alt+Back", "⌥⌘⌫")]
    [InlineData("Ctrl+Cmd+4", "⌃⌘4")]
    [InlineData("Ctrl+Alt+Shift+Cmd+K", "⌃⌥⇧⌘K")]
    [InlineData("Cmd+K", "⌘K")]
    [InlineData("Ctrl+Q", "⌃Q")]
    [InlineData("Ctrl+Shift+Tab", "⌃⇧⇥")]
    [InlineData("Tab", "⇥")]
    [InlineData("Cmd+Shift+OemPeriod", "⇧⌘.")]
    [InlineData("Cmd+OemOpenBrackets", "⌘[")]
    [InlineData("Cmd+OemCloseBrackets", "⌘]")]
    [InlineData("Cmd+OemPlus", "⌘=")]
    [InlineData("Cmd+OemMinus", "⌘-")]
    [InlineData("Cmd+OemTilde", "⌘`")]
    [InlineData("Cmd+OemQuestion", "⌘/")]
    [InlineData("Cmd+OemSemicolon", "⌘;")]
    [InlineData("Cmd+OemQuotes", "⌘'")]
    [InlineData("Cmd+OemPipe", "⌘\\")]
    [InlineData("Cmd+Up", "⌘↑")]
    [InlineData("Cmd+Down", "⌘↓")]
    [InlineData("Cmd+Left", "⌘←")]
    [InlineData("Cmd+Right", "⌘→")]
    [InlineData("Cmd+Home", "⌘↖")]
    [InlineData("Cmd+End", "⌘↘")]
    [InlineData("Cmd+PageUp", "⌘⇞")]
    [InlineData("Cmd+PageDown", "⌘⇟")]
    [InlineData("Delete", "⌦")]
    [InlineData("Cmd+Enter", "⌘↩")]
    [InlineData("Cmd+Alt+Escape", "⌥⌘⎋")]
    [InlineData("Space", "Space")]
    [InlineData("Shift+Space", "⇧Space")]
    [InlineData("Insert", "Insert")]
    public void Chord_is_shown_in_mac_style(string chord, string shown) =>
        Assert.Equal(shown, KeyChordText.Format(KeyChord.Parse(chord), windows: false));
}
