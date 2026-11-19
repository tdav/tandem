using Avalonia.Controls;
using Avalonia.Input;
using Tandem.App.Commands;

namespace Tandem.App.Input;

/// <summary>
/// Decides where a key press goes: which chord it is for the key map, whether it reaches the commands while a text
/// editor has the focus, and whether typed text is quick search.
/// </summary>
public static class KeyRouting
{
    /// <summary>
    /// The chord for the key map. For the keys that type characters (letters, digits, punctuation) it names the
    /// physical key, as it is labelled on a US keyboard, so Cmd+R is Cmd+R on any layout, Russian included. Avalonia's
    /// <see cref="KeyEventArgs.Key"/> follows the layout for these keys (on macOS it maps the produced character, and a
    /// Russian "." is <see cref="Key.OemPeriod"/> on the physical Slash key). Other keys (function keys, arrows, Tab,
    /// Space, Backspace) keep the reported key, as does an event without a physical key.
    /// </summary>
    public static KeyChord ChordFor(Key key, PhysicalKey physicalKey, KeyModifiers modifiers)
    {
        if (IsCharacterKey(physicalKey) && physicalKey.ToQwertyKey() is var qwerty and not Key.None)
            key = qwerty;
        return new KeyChord(key, modifiers);
    }

    /// <summary>The focus rule: true when <paramref name="focused"/> is a text editor (the path editor, the command
    /// line, any other text box). Then the panel's plain-key commands do not run and the key stays with the editor.</summary>
    public static bool IsTextEditor(IInputElement? focused) => focused is TextBox;

    /// <summary>
    /// Whether a chord pressed in a text editor still goes to the commands: only chords with Cmd or Ctrl, and function
    /// keys, and not the chords text editing itself uses (Cmd+A/C/V/X/Z, Shift+Cmd+Z, Cmd or Alt with
    /// Backspace/Delete, Cmd with an arrow). Plain keys, Shift and Alt (which types characters on macOS) stay with the
    /// editor.
    /// </summary>
    public static bool ShouldRouteWhileEditing(KeyChord chord)
    {
        var (key, modifiers) = (chord.Key, chord.Modifiers);
        if (key is >= Key.F1 and <= Key.F24) return true;
        if ((modifiers & (KeyModifiers.Meta | KeyModifiers.Control)) == 0) return false;

        var cmd = modifiers.HasFlag(KeyModifiers.Meta);
        if (cmd && key is Key.A or Key.C or Key.V or Key.X or Key.Z) return false;
        if ((cmd || modifiers.HasFlag(KeyModifiers.Alt)) && key is Key.Back or Key.Delete) return false;
        if (cmd && key is Key.Left or Key.Right or Key.Up or Key.Down) return false;
        return true;
    }

    /// <summary>Whether text typed with these modifiers held is quick search: never with Cmd or Ctrl.</summary>
    public static bool AcceptsQuickSearch(KeyModifiers modifiers) =>
        (modifiers & (KeyModifiers.Meta | KeyModifiers.Control)) == 0;

    private static bool IsCharacterKey(PhysicalKey key) => key is
        (>= PhysicalKey.A and <= PhysicalKey.Z) or (>= PhysicalKey.Digit0 and <= PhysicalKey.Digit9) or
        PhysicalKey.Backquote or PhysicalKey.Backslash or PhysicalKey.BracketLeft or PhysicalKey.BracketRight or
        PhysicalKey.Comma or PhysicalKey.Equal or PhysicalKey.IntlBackslash or PhysicalKey.Minus or
        PhysicalKey.Period or PhysicalKey.Quote or PhysicalKey.Semicolon or PhysicalKey.Slash;
}
