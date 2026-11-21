using System.Text;
using System.Diagnostics;
using Tandem.Net.Terminal;

namespace Tandem.Net.Tests;

public sealed class TerminalTests
{
    [Fact]
    public void Split_utf8_colors_wide_cells_and_alternate_screen()
    {
        using var screen = new TerminalEmulator(10, 3);
        var bytes = Encoding.UTF8.GetBytes("\e[31mй中");
        screen.Feed(bytes.AsSpan(0, 6));
        screen.Feed(bytes.AsSpan(6));
        Assert.Equal("й中", screen.LineText(0));
        Assert.Equal(new TerminalColor(TerminalColorKind.Indexed, 1), screen.CellAt(0, 0).Foreground);
        Assert.Equal(2, screen.CellAt(1, 0).Width);
        Assert.Equal(0, screen.CellAt(2, 0).Width);
        screen.Feed("\e[?1049h"u8);
        Assert.True(screen.IsAlternateScreen);
        Assert.Empty(screen.LineText(0));
        screen.Feed("\e[?1049l"u8);
        Assert.Equal("й中", screen.LineText(0));
    }

    [Fact]
    public void Extracts_wrapped_text_including_scrollback()
    {
        using var screen = new TerminalEmulator(5, 2);
        screen.Feed("abcdefgh\r\nnext"u8);
        Assert.True(screen.IsWrapped(-1));
        Assert.Equal("abcdefgh\nnext", TerminalSelectionText.Extract(screen, (3, 1), (0, -1)));
    }

    [Fact]
    public void Cursor_reply_and_modes_follow_output()
    {
        using var screen = new TerminalEmulator(12, 4);
        byte[]? reply = null;
        screen.Reply += data => reply = data.ToArray();
        screen.Feed("\e[2;3H\e[6n\e[?1h\e[?2004h\e[?25l"u8);
        Assert.Equal((2, 1), screen.Cursor);
        Assert.Equal("\e[2;3R", Encoding.UTF8.GetString(reply!));
        Assert.True(screen.ApplicationCursorKeys);
        Assert.True(screen.BracketedPaste);
        Assert.False(screen.CursorVisible);
    }

    [Theory]
    [InlineData(TerminalKey.Up, TerminalModifiers.None, false, "\e[A")]
    [InlineData(TerminalKey.Up, TerminalModifiers.None, true, "\eOA")]
    [InlineData(TerminalKey.Up, TerminalModifiers.Control, true, "\e[1;5A")]
    [InlineData(TerminalKey.F1, TerminalModifiers.None, false, "\eOP")]
    [InlineData(TerminalKey.F5, TerminalModifiers.None, false, "\e[15~")]
    [InlineData(TerminalKey.Tab, TerminalModifiers.Shift, false, "\e[Z")]
    public void Encodes_keys(TerminalKey key, TerminalModifiers modifiers, bool application, string expected) =>
        Assert.Equal(expected, Encoding.UTF8.GetString(TerminalInput.Key(key, modifiers, application)));

    [Fact]
    public void Bracketed_paste_cannot_close_the_paste_early()
    {
        Assert.Equal("\e[200~first\rsecond\rthird\e[201~",
            Encoding.UTF8.GetString(TerminalInput.Paste("first\r\nsecond\e[201~\nthird", true)));
        Assert.Equal(new byte[] { 3 }, TerminalInput.Text("C", TerminalModifiers.Control));
        Assert.Equal(new byte[] { 27, (byte)'b' }, TerminalInput.Text("b", TerminalModifiers.Alt));
    }

    [Fact]
    public void Handles_scrolling_combining_marks_and_malformed_output()
    {
        using var screen = new TerminalEmulator(12, 3, scrollbackLines: 4);
        screen.Feed(Encoding.UTF8.GetBytes("e\u0301\r\n"));
        Assert.Equal("e\u0301", screen.CellAt(0, 0).Text);
        for (var i = 0; i < 12; i++) screen.Feed("line\r\n"u8);
        Assert.Equal(4, screen.ScrollbackCount);
        Assert.Equal("line", screen.LineText(-4));
        screen.Resize(16, 5);
        Assert.InRange(screen.Cursor.Column, 0, screen.Columns - 1);
        Assert.InRange(screen.Cursor.Row, 0, screen.Rows - 1);
        var bytes = new byte[1024 * 1024];
        new Random(1337).NextBytes(bytes);
        screen.Feed(bytes);
    }

    [Fact]
    [Trait("Category", "Timing")]
    public void Ten_megabytes_of_output_stays_within_the_terminal_time_budget()
    {
        using var screen = new TerminalEmulator(80, 24);
        var chunk = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(
            "The quick brown fox jumps over the lazy dog.\r\n", 256)));
        var watch = Stopwatch.StartNew();
        for (var length = 0; length < 10_000_000; length += chunk.Length) screen.Feed(chunk);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"Terminal processing took {watch.Elapsed}.");
    }
}
