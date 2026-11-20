using Avalonia.Input;
using Tandem.App.Resources;

namespace Tandem.App.Commands;

/// <summary>
/// A chord as the Mac shows it to people: the modifier symbols in the order ⌃⌥⇧⌘, then the key as its symbol
/// (⌫, ↩, ⇥, ↑ …), its character (",", "[", "`" …), its letter or digit, or its name ("F5", Space). For the settings
/// window and the F-key bar; <see cref="KeyChord.ToString"/> stays the form settings are saved in.
/// </summary>
public static class KeyChordText
{
    private static readonly (KeyModifiers Modifier, string Symbol)[] Modifiers =
    [
        (KeyModifiers.Control, "⌃"),
        (KeyModifiers.Alt, "⌥"),
        (KeyModifiers.Shift, "⇧"),
        (KeyModifiers.Meta, "⌘"),
    ];

    private static readonly Dictionary<Key, string> Symbols = new()
    {
        [Key.Back] = "⌫", [Key.Delete] = "⌦", [Key.Enter] = "↩", [Key.Escape] = "⎋", [Key.Tab] = "⇥",
        [Key.Up] = "↑", [Key.Down] = "↓", [Key.Left] = "←", [Key.Right] = "→",
        [Key.Home] = "↖", [Key.End] = "↘", [Key.PageUp] = "⇞", [Key.PageDown] = "⇟",
        [Key.OemComma] = ",", [Key.OemPeriod] = ".", [Key.OemPlus] = "=", [Key.OemMinus] = "-",
        [Key.OemOpenBrackets] = "[", [Key.OemCloseBrackets] = "]", [Key.OemSemicolon] = ";",
        [Key.OemQuotes] = "'", [Key.OemQuestion] = "/", [Key.OemPipe] = "\\", [Key.OemTilde] = "`",
    };

    public static string Format(KeyChord chord)
    {
        var modifiers = string.Concat(Modifiers.Where(m => chord.Modifiers.HasFlag(m.Modifier)).Select(m => m.Symbol));
        return modifiers + KeyText(chord.Key);
    }

    private static string KeyText(Key key)
    {
        if (Symbols.TryGetValue(key, out var symbol)) return symbol;
        return key switch
        {
            Key.Space => Loc.Get("Key_Space"),
            Key.Insert => Loc.Get("Key_Insert"),
            // Letters, digits and F-keys read as the chord writes them ("K", "4", "F5").
            _ => new KeyChord(key, KeyModifiers.None).ToString(),
        };
    }
}
