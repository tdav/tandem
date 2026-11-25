using System.Text;
using XTerm.Buffer;
using XTerm.Common;
using XTerm.Options;

namespace Tandem.Net.Terminal;

public enum TerminalColorKind { Default, Indexed, Rgb }
public readonly record struct TerminalColor(TerminalColorKind Kind, int Value);

[Flags]
public enum CellStyle { None = 0, Bold = 1, Dim = 2, Italic = 4, Underline = 8, Inverse = 16, Strikethrough = 32 }

/// <summary>Cursor shape, as selected by DECSCUSR (<c>ESC [ n SP q</c>).</summary>
public enum TerminalCursorShape { Bar, Block, Underline }

public readonly record struct TerminalCell(string Text, TerminalColor Foreground, TerminalColor Background,
    CellStyle Style, int Width);

/// <summary>The screen and scrollback for one terminal. The caller serializes access.</summary>
public sealed class TerminalEmulator : IDisposable
{
    public const int DefaultScrollback = 10_000;
    private readonly XTerm.Terminal _terminal;

    public TerminalEmulator(int columns, int rows, int scrollbackLines = DefaultScrollback)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(columns);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rows);
        ArgumentOutOfRangeException.ThrowIfNegative(scrollbackLines);
        _terminal = new XTerm.Terminal(new TerminalOptions { Cols = columns, Rows = rows, Scrollback = scrollbackLines });
        _terminal.DataReceived += (_, e) => Reply?.Invoke(Encoding.UTF8.GetBytes(e.Data));
        // DECSCUSR is parsed here rather than through CursorStyleChanged: XTerm.NET maps 0 to a blinking block (xterm's
        // default) while 0 should restore this terminal's own default, the blinking bar.
        _terminal.RegisterCsiHandler(" q", parameters =>
        {
            (CursorShape, CursorBlink) = (parameters.Length > 0 ? parameters.GetParam(0, 0) : 0) switch
            {
                1 => (TerminalCursorShape.Block, true),
                2 => (TerminalCursorShape.Block, false),
                3 => (TerminalCursorShape.Underline, true),
                4 => (TerminalCursorShape.Underline, false),
                6 => (TerminalCursorShape.Bar, false),
                _ => (TerminalCursorShape.Bar, true),
            };
            return true;
        });
    }

    public int Columns => _terminal.Cols;
    public int Rows => _terminal.Rows;
    public int ScrollbackCount => _terminal.Buffer.YBase;
    public (int Column, int Row) Cursor => (Math.Clamp(_terminal.Buffer.X, 0, Columns - 1), _terminal.Buffer.Y);
    public bool CursorVisible => _terminal.CursorVisible;
    /// <summary>A blinking bar unless the remote side asks for another shape with DECSCUSR.</summary>
    public TerminalCursorShape CursorShape { get; private set; } = TerminalCursorShape.Bar;
    public bool CursorBlink { get; private set; } = true;
    public bool IsAlternateScreen => _terminal.IsAlternateBufferActive;
    public bool ApplicationCursorKeys => _terminal.ApplicationCursorKeys;
    public bool BracketedPaste => _terminal.BracketedPasteMode;
    public string Title => _terminal.Title ?? "";

    public event Action? Changed;
    public event Action<ReadOnlyMemory<byte>>? Reply;

    public void Feed(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;
        _terminal.Write(data);
        Changed?.Invoke();
    }

    public void Resize(int columns, int rows)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(columns);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rows);
        if ((columns, rows) == (Columns, Rows)) return;
        _terminal.Resize(columns, rows);
        Changed?.Invoke();
    }

    public TerminalCell CellAt(int column, int line)
    {
        if (column < 0 || column >= Columns || LineAt(line) is not { } row || column >= row.Length)
            return new("", default, default, CellStyle.None, 1);
        var cell = row[column];
        var a = cell.Attributes;
        var style = (a.IsBold() ? CellStyle.Bold : 0) | (a.IsDim() ? CellStyle.Dim : 0)
                    | (a.IsItalic() ? CellStyle.Italic : 0) | (a.IsUnderline() ? CellStyle.Underline : 0)
                    | (a.IsInverse() ? CellStyle.Inverse : 0) | (a.IsStrikethrough() ? CellStyle.Strikethrough : 0);
        return new(a.IsInvisible() ? "" : cell.Content ?? "",
            Color(a.GetFgColorKind(), a.GetFgColor()), Color(a.GetBgColorKind(), a.GetBgColor()), style, cell.Width);
    }

    public string LineText(int line) => LineAt(line)?.TranslateToString(trimRight: true) ?? "";

    public bool IsWrapped(int line) => LineAt(line + 1)?.IsWrapped == true;

    public void Dispose() => _terminal.Dispose();

    private BufferLine? LineAt(int line) => line >= -ScrollbackCount && line < Rows
        ? _terminal.Buffer.Lines[ScrollbackCount + line]
        : null;

    private static TerminalColor Color(ColorMode mode, int value) => mode switch
    {
        ColorMode.RGB => new(TerminalColorKind.Rgb, value),
        ColorMode.Palette16 or ColorMode.Palette256 => new(TerminalColorKind.Indexed, value),
        _ => default,
    };
}
