using System.Text;

namespace Tandem.Net.Terminal;

public enum TerminalKey
{
    Enter, Tab, Escape, Backspace, Delete, Insert, Up, Down, Left, Right, Home, End, PageUp, PageDown,
    F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
}

[Flags]
public enum TerminalModifiers { None = 0, Shift = 1, Alt = 2, Control = 4 }

public static class TerminalInput
{
    public static byte[] Key(TerminalKey key, TerminalModifiers modifiers, bool applicationCursorKeys)
    {
        var modifier = 1 + (int)modifiers;
        var arrow = key switch
        {
            TerminalKey.Up => 'A', TerminalKey.Down => 'B', TerminalKey.Right => 'C', TerminalKey.Left => 'D',
            TerminalKey.Home => 'H', TerminalKey.End => 'F',
            TerminalKey.F1 => 'P', TerminalKey.F2 => 'Q', TerminalKey.F3 => 'R', TerminalKey.F4 => 'S',
            _ => '\0',
        };
        if (arrow != '\0')
            return Encoding.ASCII.GetBytes(modifier != 1 ? $"\e[1;{modifier}{arrow}"
                : applicationCursorKeys || key is >= TerminalKey.F1 and <= TerminalKey.F4 ? $"\eO{arrow}" : $"\e[{arrow}");

        var number = key switch
        {
            TerminalKey.Insert => 2, TerminalKey.Delete => 3, TerminalKey.PageUp => 5, TerminalKey.PageDown => 6,
            TerminalKey.F5 => 15, TerminalKey.F6 => 17, TerminalKey.F7 => 18, TerminalKey.F8 => 19,
            TerminalKey.F9 => 20, TerminalKey.F10 => 21, TerminalKey.F11 => 23, TerminalKey.F12 => 24,
            _ => 0,
        };
        if (number != 0)
            return Encoding.ASCII.GetBytes(modifier == 1 ? $"\e[{number}~" : $"\e[{number};{modifier}~");

        var text = key switch
        {
            TerminalKey.Enter => "\r",
            TerminalKey.Tab when modifiers.HasFlag(TerminalModifiers.Shift) => "\e[Z",
            TerminalKey.Tab => "\t",
            TerminalKey.Escape => "\e",
            TerminalKey.Backspace when modifiers.HasFlag(TerminalModifiers.Control) => "\b",
            TerminalKey.Backspace => "\x7f",
            _ => "",
        };
        return Encoding.ASCII.GetBytes(modifiers.HasFlag(TerminalModifiers.Alt) ? "\e" + text : text);
    }

    public static byte[] Text(string text, TerminalModifiers modifiers)
    {
        if (text.Length == 0) return [];
        if (modifiers.HasFlag(TerminalModifiers.Control) && text.Length == 1)
        {
            var c = char.ToUpperInvariant(text[0]);
            if (c is >= '@' and <= '_' || c == ' ')
                text = ((char)(c & 0x1f)).ToString();
            else if (c == '?')
                text = "\x7f";
        }
        return Encoding.UTF8.GetBytes(modifiers.HasFlag(TerminalModifiers.Alt) ? "\e" + text : text);
    }

    public static byte[] Paste(string text, bool bracketed)
    {
        text = text.Replace("\r\n", "\r", StringComparison.Ordinal).Replace('\n', '\r');
        if (!bracketed) return Encoding.UTF8.GetBytes(text);
        text = text.Replace("\e[200~", "", StringComparison.Ordinal)
            .Replace("\e[201~", "", StringComparison.Ordinal)
            .Replace("\e", "", StringComparison.Ordinal)
            .Replace("\u009b", "", StringComparison.Ordinal);
        return Encoding.UTF8.GetBytes("\e[200~" + text + "\e[201~");
    }
}
