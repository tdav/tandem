using Avalonia.Input;

namespace Tandem.App.Commands;

/// <summary>
/// A key together with the modifiers held down, as text like "Cmd+Shift+G", "F5" or "Alt+F7". Modifier names are
/// Ctrl, Alt, Shift and Cmd (<see cref="KeyModifiers.Meta"/>); the key is an Avalonia <see cref="Key"/> name, or a
/// digit "0"–"9" for <see cref="Key.D0"/>–<see cref="Key.D9"/>.
/// </summary>
public readonly record struct KeyChord(Key Key, KeyModifiers Modifiers)
{
    private static readonly (string Name, KeyModifiers Modifier)[] ModifierNames =
    [
        ("Ctrl", KeyModifiers.Control),
        ("Alt", KeyModifiers.Alt),
        ("Shift", KeyModifiers.Shift),
        ("Cmd", KeyModifiers.Meta),
    ];

    // Several Key members share a value (Oem4 = OemOpenBrackets, Prior = PageUp, ...), and Enum.ToString may pick
    // either name. Write the descriptive one: never a bare "OemN" when another name exists.
    private static readonly Dictionary<Key, string> KeyNames =
        Enum.GetNames<Key>()
            .GroupBy(Enum.Parse<Key>)
            .ToDictionary(g => g.Key, g => g.OrderBy(IsNumberedOemName).ThenBy(n => n, StringComparer.Ordinal).First());

    /// <summary>Parses a chord. Throws <see cref="FormatException"/> for anything but exactly one key and distinct modifiers.</summary>
    public static KeyChord Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        Key? key = null;
        var modifiers = KeyModifiers.None;
        foreach (var rawPart in text.Split('+'))
        {
            var part = rawPart.Trim();
            if (part.Length == 0)
                throw new FormatException($"Key chord \"{text}\" has an empty part.");

            var modifier = ModifierNames.FirstOrDefault(m => string.Equals(m.Name, part, StringComparison.OrdinalIgnoreCase));
            if (modifier.Name is not null)
            {
                if (modifiers.HasFlag(modifier.Modifier))
                    throw new FormatException($"Key chord \"{text}\" repeats the modifier {modifier.Name}.");
                modifiers |= modifier.Modifier;
            }
            else
            {
                if (key is not null)
                    throw new FormatException($"Key chord \"{text}\" has more than one key.");
                key = ParseKey(part) ?? throw new FormatException($"Key chord \"{text}\" has an unknown key \"{part}\".");
            }
        }

        return key is { } k ? new KeyChord(k, modifiers) : throw new FormatException($"Key chord \"{text}\" has no key.");
    }

    /// <summary>The chord as text, modifiers in the order Ctrl, Alt, Shift, Cmd; <see cref="Parse"/> reads it back.</summary>
    public override string ToString()
    {
        var modifiers = Modifiers;
        var parts = ModifierNames.Where(m => modifiers.HasFlag(m.Modifier)).Select(m => m.Name).ToList();
        parts.Add(KeyName(Key));
        return string.Join("+", parts);
    }

    private static Key? ParseKey(string name)
    {
        if (name.Length == 1 && char.IsAsciiDigit(name[0]))
            return Key.D0 + (name[0] - '0');

        // Enum.TryParse also accepts numbers ("3" would be Key.Tab) and comma lists ("A,B"); only names are keys here.
        if (!char.IsAsciiLetter(name[0]) || !name.All(char.IsAsciiLetterOrDigit)
            || !Enum.TryParse<Key>(name, ignoreCase: true, out var key) || key == Key.None)
            return null;
        return key;
    }

    private static string KeyName(Key key)
    {
        if (key is >= Key.D0 and <= Key.D9)
            return ((char)('0' + (key - Key.D0))).ToString();
        return KeyNames.TryGetValue(key, out var name) ? name : key.ToString();
    }

    private static bool IsNumberedOemName(string name) =>
        name.Length > 3 && name.StartsWith("Oem", StringComparison.Ordinal) && name[3..].All(char.IsAsciiDigit);
}
