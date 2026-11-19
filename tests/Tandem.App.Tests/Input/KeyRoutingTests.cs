using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Tandem.App.Commands;
using Tandem.App.Input;

namespace Tandem.App.Tests.Input;

public class KeyRoutingTests
{
    [Theory]
    // Russian layout: the letter keys produce Cyrillic; the chord still names the physical key.
    [InlineData(Key.None, PhysicalKey.R, KeyModifiers.Meta, "Cmd+R")]
    [InlineData(Key.K, PhysicalKey.R, KeyModifiers.Meta, "Cmd+R")]
    [InlineData(Key.OemPeriod, PhysicalKey.Slash, KeyModifiers.Meta, "Cmd+OemQuestion")]
    [InlineData(Key.None, PhysicalKey.Period, KeyModifiers.Meta | KeyModifiers.Shift, "Shift+Cmd+OemPeriod")]
    [InlineData(Key.None, PhysicalKey.BracketLeft, KeyModifiers.Meta, "Cmd+OemOpenBrackets")]
    [InlineData(Key.None, PhysicalKey.Digit3, KeyModifiers.Meta, "Cmd+3")]
    [InlineData(Key.None, PhysicalKey.Equal, KeyModifiers.Meta, "Cmd+OemPlus")]
    // Keys that are not characters keep the reported key.
    [InlineData(Key.F5, PhysicalKey.F5, KeyModifiers.None, "F5")]
    [InlineData(Key.Tab, PhysicalKey.Tab, KeyModifiers.Control, "Ctrl+Tab")]
    [InlineData(Key.Back, PhysicalKey.Backspace, KeyModifiers.Meta, "Cmd+Back")]
    [InlineData(Key.Up, PhysicalKey.ArrowUp, KeyModifiers.Meta, "Cmd+Up")]
    // Without a physical key (some synthetic events) the reported key is used.
    [InlineData(Key.R, PhysicalKey.None, KeyModifiers.Meta, "Cmd+R")]
    public void Chord_uses_the_physical_key_for_character_keys(Key key, PhysicalKey physical, KeyModifiers modifiers,
        string expected)
    {
        Assert.Equal(KeyChord.Parse(expected), KeyRouting.ChordFor(key, physical, modifiers));
    }

    [Theory]
    [InlineData("Space", false)]
    [InlineData("Back", false)]
    [InlineData("Tab", false)]
    [InlineData("Enter", false)]
    [InlineData("Up", false)]
    [InlineData("Shift+Down", false)]
    [InlineData("A", false)]
    [InlineData("Alt+A", false)]
    [InlineData("Cmd+A", false)]
    [InlineData("Cmd+C", false)]
    [InlineData("Cmd+V", false)]
    [InlineData("Cmd+X", false)]
    [InlineData("Cmd+Z", false)]
    [InlineData("Shift+Cmd+Z", false)]
    [InlineData("Cmd+Back", false)]
    [InlineData("Alt+Back", false)]
    [InlineData("Cmd+Left", false)]
    [InlineData("Cmd+Up", false)]
    [InlineData("Shift+Cmd+Right", false)]
    [InlineData("Cmd+R", true)]
    [InlineData("Cmd+T", true)]
    [InlineData("Shift+Cmd+G", true)]
    [InlineData("Ctrl+Tab", true)]
    [InlineData("F5", true)]
    [InlineData("Shift+F8", true)]
    public void While_editing_only_cmd_chords_the_editor_does_not_need_reach_commands(string chord, bool routed)
    {
        Assert.Equal(routed, KeyRouting.ShouldRouteWhileEditing(KeyChord.Parse(chord)));
    }

    [Theory]
    [InlineData(KeyModifiers.None, true)]
    [InlineData(KeyModifiers.Shift, true)]
    [InlineData(KeyModifiers.Alt, true)]
    [InlineData(KeyModifiers.Meta, false)]
    [InlineData(KeyModifiers.Control, false)]
    [InlineData(KeyModifiers.Meta | KeyModifiers.Shift, false)]
    public void Text_typed_with_cmd_or_ctrl_is_never_quick_search(KeyModifiers modifiers, bool accepted)
    {
        Assert.Equal(accepted, KeyRouting.AcceptsQuickSearch(modifiers));
    }

    [AvaloniaFact]
    public void Text_editors_are_recognised()
    {
        Assert.True(KeyRouting.IsTextEditor(new TextBox()));
        Assert.False(KeyRouting.IsTextEditor(new ListBox()));
        Assert.False(KeyRouting.IsTextEditor(new Button()));
        Assert.False(KeyRouting.IsTextEditor(null));
    }
}
