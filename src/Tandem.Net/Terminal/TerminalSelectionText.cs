using System.Text;

namespace Tandem.Net.Terminal;

public static class TerminalSelectionText
{
    public static string Extract(TerminalEmulator screen, (int Column, int Line) from, (int Column, int Line) to)
    {
        if ((from.Line, from.Column).CompareTo((to.Line, to.Column)) > 0)
            (from, to) = (to, from);
        var result = new StringBuilder();
        var last = Math.Min(screen.Rows - 1, to.Line);
        for (var line = Math.Max(-screen.ScrollbackCount, from.Line); line <= last; line++)
        {
            var start = line == from.Line ? Math.Clamp(from.Column, 0, screen.Columns - 1) : 0;
            var end = line == to.Line ? Math.Clamp(to.Column, start, screen.Columns - 1) : screen.Columns - 1;
            if (start > 0 && screen.CellAt(start, line).Width == 0) start--;
            var rowStart = result.Length;
            for (var column = start; column <= end; column++)
            {
                var cell = screen.CellAt(column, line);
                if (cell.Width == 0) continue;
                result.Append(cell.Text.Length == 0 ? " " : cell.Text);
            }
            while (result.Length > rowStart && result[^1] == ' ') result.Length--;
            if (line < last && !screen.IsWrapped(line)) result.Append('\n');
        }
        return result.ToString();
    }
}
