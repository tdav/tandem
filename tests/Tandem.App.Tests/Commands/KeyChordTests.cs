using Avalonia.Input;
using Tandem.App.Commands;

namespace Tandem.App.Tests.Commands;

public class KeyChordTests
{
    [Theory]
    [InlineData("F5")]
    [InlineData("Shift+Cmd+G")]
    [InlineData("Ctrl+Shift+Tab")]
    [InlineData("Alt+F7")]
    [InlineData("Cmd+3")]
    [InlineData("Cmd+Back")]
    [InlineData("Alt+Cmd+Back")]
    [InlineData("Cmd+OemOpenBrackets")]
    [InlineData("Cmd+OemCloseBrackets")]
    [InlineData("Shift+Cmd+OemPeriod")]
    [InlineData("Ctrl+Alt+Shift+Cmd+A")]
    public void Chord_roundtrips(string text) => Assert.Equal(text, KeyChord.Parse(text).ToString());

    [Theory]
    [InlineData("Cmd+Shift+G", "Shift+Cmd+G")]
    [InlineData("Cmd+Alt+Back", "Alt+Cmd+Back")]
    [InlineData("Cmd+Shift+OemPeriod", "Shift+Cmd+OemPeriod")]
    public void Modifiers_in_any_order_are_written_in_canonical_order(string text, string canonical) =>
        Assert.Equal(canonical, KeyChord.Parse(text).ToString());

    [Fact]
    public void Parse_maps_names_to_avalonia_keys_and_modifiers()
    {
        Assert.Equal(new KeyChord(Key.G, KeyModifiers.Meta | KeyModifiers.Shift), KeyChord.Parse("Cmd+Shift+G"));
        Assert.Equal(new KeyChord(Key.Tab, KeyModifiers.Control), KeyChord.Parse("Ctrl+Tab"));
        Assert.Equal(new KeyChord(Key.F7, KeyModifiers.Alt), KeyChord.Parse("Alt+F7"));
        Assert.Equal(new KeyChord(Key.F5, KeyModifiers.None), KeyChord.Parse("F5"));
    }

    [Theory]
    [InlineData("Cmd+3", Key.D3)]
    [InlineData("Cmd+7", Key.D7)]
    [InlineData("Cmd+0", Key.D0)]
    public void Digits_parse_to_D_keys(string text, Key key) => Assert.Equal(key, KeyChord.Parse(text).Key);

    [Fact]
    public void D_key_is_written_as_a_digit() =>
        Assert.Equal("Cmd+4", new KeyChord(Key.D4, KeyModifiers.Meta).ToString());

    [Fact]
    public void Aliased_keys_are_written_with_their_descriptive_name()
    {
        // Key.Oem4 and Key.OemOpenBrackets share a value; Enum.ToString would print "Oem4".
        Assert.Equal("Cmd+OemOpenBrackets", new KeyChord(Key.Oem4, KeyModifiers.Meta).ToString());
        Assert.Equal("OemOpenBrackets", new KeyChord(Key.OemOpenBrackets, KeyModifiers.None).ToString());
    }

    [Fact]
    public void Modifiers_are_case_insensitive_and_spaces_around_plus_are_ignored() =>
        Assert.Equal(KeyChord.Parse("Cmd+Shift+G"), KeyChord.Parse(" cmd + SHIFT +G "));

    [Fact]
    public void ToString_uses_canonical_modifier_order() =>
        Assert.Equal("Ctrl+Alt+Shift+Cmd+X", KeyChord.Parse("Cmd+Shift+Alt+Ctrl+X").ToString());

    [Fact]
    public void Parse_rejects_unknown_key() => Assert.Throws<FormatException>(() => KeyChord.Parse("Cmd+Banana"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Cmd")]
    [InlineData("Cmd+Shift")]
    [InlineData("Cmd+")]
    [InlineData("+A")]
    [InlineData("A+B")]
    [InlineData("Cmd+Cmd+A")]
    [InlineData("Shift+shift+A")]
    [InlineData("None")]
    [InlineData("Cmd+None")]
    [InlineData("Cmd+12")]
    [InlineData("Win+A")]
    [InlineData("Cmd+A,B")]
    public void Parse_rejects_malformed_chords(string text) => Assert.Throws<FormatException>(() => KeyChord.Parse(text));
}
