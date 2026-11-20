using Avalonia.Input;

namespace Tandem.App.Commands;

/// <summary>Which chords the user may bind to a command.</summary>
public static class KeyChordRules
{
    private const KeyModifiers CmdOrCtrl = KeyModifiers.Meta | KeyModifiers.Control;

    /// <summary>The chords macOS keeps for itself (quit, hide, hide others, the app switcher, Spotlight, force
    /// quit, log out, lock the screen, screenshots, the next window of the app): the app never sees them, or must not
    /// take them.</summary>
    private static readonly KeyChord[] SystemChords =
    [
        new(Key.Q, KeyModifiers.Meta),
        new(Key.H, KeyModifiers.Meta),
        new(Key.H, KeyModifiers.Meta | KeyModifiers.Alt),
        new(Key.Tab, KeyModifiers.Meta),
        new(Key.Space, KeyModifiers.Meta),
        new(Key.Escape, KeyModifiers.Meta | KeyModifiers.Alt),
        new(Key.Q, KeyModifiers.Shift | KeyModifiers.Meta),
        new(Key.Q, KeyModifiers.Control | KeyModifiers.Meta),
        new(Key.D3, KeyModifiers.Shift | KeyModifiers.Meta),
        new(Key.D4, KeyModifiers.Shift | KeyModifiers.Meta),
        new(Key.D5, KeyModifiers.Shift | KeyModifiers.Meta),
        new(Key.OemTilde, KeyModifiers.Meta),
    ];

    /// <summary>
    /// True for F1–F24 with any modifiers, for any key with Cmd or Ctrl, and for Tab, Space, Backspace, Delete and
    /// Insert alone or with Shift or Alt. False for Enter and Escape (the panel's own keys), for the arrows, Home, End,
    /// PageUp and PageDown without Cmd or Ctrl (they move the cursor), for letters, digits and punctuation without Cmd
    /// or Ctrl (they type quick search), for a modifier key on its own, and for the system's chords (Cmd+Q, Cmd+H,
    /// Cmd+Alt+H, Cmd+Tab, Cmd+Space, Cmd+Alt+Escape, Shift+Cmd+Q, Ctrl+Cmd+Q, Shift+Cmd+3/4/5, Cmd+`).
    /// </summary>
    public static bool IsAssignable(KeyChord chord)
    {
        var (key, modifiers) = (chord.Key, chord.Modifiers);
        if (key is Key.None or Key.Enter or Key.Escape || IsModifierKey(key) || SystemChords.Contains(chord))
            return false;
        if (key is >= Key.F1 and <= Key.F24) return true;
        if ((modifiers & CmdOrCtrl) != 0) return true;
        return key is Key.Tab or Key.Space or Key.Back or Key.Delete or Key.Insert;
    }

    private static bool IsModifierKey(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt
        or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;
}
