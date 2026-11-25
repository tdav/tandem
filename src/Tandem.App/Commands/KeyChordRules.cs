using Avalonia.Input;

namespace Tandem.App.Commands;

/// <summary>Why a chord cannot be bound to a command.</summary>
public enum ChordRefusal
{
    /// <summary>No key, or a modifier key on its own.</summary>
    NoKey,

    /// <summary>Enter or Escape: the panel's own keys.</summary>
    PanelKey,

    /// <summary>A chord macOS keeps for itself.</summary>
    System,

    /// <summary>A key that moves the cursor or types quick search needs Cmd or Ctrl.</summary>
    NeedsCmdOrCtrl,
}

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
    public static bool IsAssignable(KeyChord chord) => RefusalOf(chord) is null;

    /// <summary>The chords Windows keeps for itself (close the window, switch apps, the window menu, Start, Task
    /// Manager); on Windows every chord with the Windows key (Meta) is the system's as well.</summary>
    private static readonly KeyChord[] WindowsSystemChords =
    [
        new(Key.F4, KeyModifiers.Alt),
        new(Key.Tab, KeyModifiers.Alt),
        new(Key.Tab, KeyModifiers.Alt | KeyModifiers.Shift),
        new(Key.Escape, KeyModifiers.Alt),
        new(Key.Space, KeyModifiers.Alt),
        new(Key.Escape, KeyModifiers.Control),
        new(Key.Escape, KeyModifiers.Control | KeyModifiers.Shift),
        new(Key.Delete, KeyModifiers.Control | KeyModifiers.Alt),
    ];

    /// <summary>Why <paramref name="chord"/> cannot be bound on the running system (see <see cref="IsAssignable"/>),
    /// or null when it can.</summary>
    public static ChordRefusal? RefusalOf(KeyChord chord) => RefusalOf(chord, Input.PlatformKeys.IsWindows);

    /// <summary>As <see cref="RefusalOf(KeyChord)"/> for macOS or for Windows.</summary>
    public static ChordRefusal? RefusalOf(KeyChord chord, bool windows)
    {
        var (key, modifiers) = (chord.Key, chord.Modifiers);
        if (key is Key.None || IsModifierKey(key)) return ChordRefusal.NoKey;
        if (windows && (modifiers.HasFlag(KeyModifiers.Meta) || WindowsSystemChords.Contains(chord)))
            return ChordRefusal.System;
        if (key is Key.Enter or Key.Escape) return ChordRefusal.PanelKey;
        if (!windows && SystemChords.Contains(chord)) return ChordRefusal.System;
        if (key is >= Key.F1 and <= Key.F24) return null;
        if ((modifiers & CmdOrCtrl) != 0) return null;
        return key is Key.Tab or Key.Space or Key.Back or Key.Delete or Key.Insert
            ? null
            : ChordRefusal.NeedsCmdOrCtrl;
    }

    private static bool IsModifierKey(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt
        or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;
}
