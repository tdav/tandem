using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Text;
using XTerm;
using XTermTerminal = XTerm.Terminal;
using XTerm.Buffer;
using XTerm.Common;
using XTerm.Input;
using XTerm.Options;

namespace Tandem.Net.Tests;

/// <summary>
/// Pins what XTerm.NET 3.0.3 (MIT, built for net10.0, no package dependencies) does, for the console (tasks 27, 28).
/// The calls:
/// <list type="bullet">
/// <item><c>new Terminal(new TerminalOptions { Cols, Rows, Scrollback })</c>; <c>IDisposable</c>. Feed what the
/// server sent with <c>Write(ReadOnlySpan&lt;byte&gt;)</c>: it decodes UTF-8 itself and keeps a character split
/// across two writes. <c>Write(string)</c> also exists.</item>
/// <item>Screen: <c>Buffer.Lines[Buffer.YDisp + row]</c> is a <c>BufferLine?</c> (nullable); <c>line[col]</c> a
/// <c>BufferCell</c> with <c>Content</c> (string), <c>Width</c> (1; 2 for a wide character, whose next cell has
/// <c>Width == 0</c> and empty <c>Content</c>) and <c>Attributes</c>. <c>line.TranslateToString(trimRight: true)</c>
/// is the text. <c>GetVisibleLines()</c> gives the screen rows; <c>GetLine(i)</c> counts from the <b>top of the
/// scrollback</b>, not the screen.</item>
/// <item>Colours: use <c>Attributes.GetFgColorKind()</c>/<c>GetBgColorKind()</c> (<c>ColorMode.Default</c>,
/// <c>Palette16</c>, <c>Palette256</c>, <c>RGB</c>) with <c>GetFgColor()</c>/<c>GetBgColor()</c> (the index, or
/// 0xRRGGBB). <b>Not</b> <c>GetFgColorMode()</c>: contrary to the package README it gives 0 for both palettes and 1
/// for RGB. Styles: <c>IsBold()</c>, <c>IsDim()</c>, <c>IsItalic()</c>, <c>IsUnderline()</c>, <c>IsInverse()</c>,
/// <c>IsInvisible()</c>, <c>IsStrikethrough()</c>.</item>
/// <item>Cursor: <c>Buffer.X</c>, <c>Buffer.Y</c> (0-based, on the screen); <c>CursorVisible</c> (true at start,
/// ESC[?25l hides, ESC[?25h shows).</item>
/// <item>Alternate screen: <c>IsAlternateBufferActive</c> / <c>ActiveBuffer</c> (<c>BufferType</c>), event
/// <c>BufferChanged</c> (<c>e.Buffer</c>). Scroll region: <c>Buffer.ScrollTop</c>, <c>Buffer.ScrollBottom</c> (0-based, inclusive).
/// Scrollback: <c>Buffer.Lines.Length</c> lines in all, <c>Buffer.YBase</c> of them above the screen.</item>
/// <item>Title: event <c>TitleChanged</c> (<c>e.Title</c>) for OSC 0 and OSC 2, and the <c>Title</c> property.
/// Replies (cursor position for ESC[6n, device attributes, …): event <c>DataReceived</c> (<c>e.Data</c>, a string)
/// — the host sends it to the server.</item>
/// <item>Modes: <c>ApplicationCursorKeys</c> (DECCKM, ESC[?1h) and <c>BracketedPasteMode</c> (ESC[?2004h). Keys:
/// <c>GenerateKeyInput(Key, KeyModifiers)</c> and <c>GenerateCharInput(char, KeyModifiers)</c> follow the modes
/// (Up is ESC[A, or ESCOA in application mode; Enter is CR, Backspace DEL).</item>
/// <item>Paste: <c>Paste(text)</c> raises <c>DataReceived</c> with what to send: line breaks become CR, ESC
/// characters are removed (so pasted text cannot end bracketed paste early) and, in bracketed paste mode, the text
/// is wrapped in ESC[200~ … ESC[201~.</item>
/// <item>Size: <c>Resize(cols, rows)</c>; <c>Cols</c>, <c>Rows</c>; event <c>Resized</c>.</item>
/// </list>
/// </summary>
public sealed class XTermNetProbeTests
{
    private static XTermTerminal Create(int cols = 20, int rows = 5, int scrollback = 100) =>
        new(new TerminalOptions { Cols = cols, Rows = rows, Scrollback = scrollback });

    private static void Feed(XTermTerminal terminal, string text) => terminal.Write(Encoding.UTF8.GetBytes(text));

    private static BufferLine ScreenLine(XTermTerminal terminal, int row) =>
        terminal.Buffer.Lines[terminal.Buffer.YDisp + row] ?? throw new InvalidOperationException($"no row {row}");

    [Fact]
    public void Feeds_bytes_and_exposes_cells_with_text_colours_and_attributes()
    {
        using var terminal = Create();

        Feed(terminal, "\e[1;31mR\e[0m\e[38;5;200mP\e[48;2;1;2;3mT\e[0m\e[4;7mU");

        var line = ScreenLine(terminal, 0);
        Assert.Equal("RPTU", line.TranslateToString(trimRight: true));
        Assert.Equal(["RPTU", "", "", "", ""], terminal.GetVisibleLines());
        var red = line[0];
        Assert.Equal("R", red.Content);
        Assert.Equal(1, red.Width);
        Assert.Equal(ColorMode.Palette16, red.Attributes.GetFgColorKind());
        Assert.Equal(1, red.Attributes.GetFgColor());
        Assert.True(red.Attributes.IsBold());
        Assert.Equal(ColorMode.Default, red.Attributes.GetBgColorKind());
        var indexed = line[1];
        Assert.Equal(ColorMode.Palette256, indexed.Attributes.GetFgColorKind());
        Assert.Equal(200, indexed.Attributes.GetFgColor());
        Assert.Equal(0, indexed.Attributes.GetFgColorMode()); // not 1, as the package README says
        var rgb = line[2];
        Assert.Equal(ColorMode.RGB, rgb.Attributes.GetBgColorKind());
        Assert.Equal(0x010203, rgb.Attributes.GetBgColor());
        Assert.Equal(1, rgb.Attributes.GetBgColorMode()); // not 2
        var styled = line[3];
        Assert.True(styled.Attributes.IsUnderline());
        Assert.True(styled.Attributes.IsInverse());
        Assert.False(styled.Attributes.IsBold());
        Assert.Equal(ColorMode.Default, styled.Attributes.GetFgColorKind());
    }

    [Fact]
    public void Tracks_cursor_alternate_screen_scroll_region_and_scrollback()
    {
        using var terminal = Create();
        var buffers = new List<BufferType>();
        terminal.BufferChanged += (_, e) => buffers.Add(e.Buffer);

        Feed(terminal, "main\e[3;5H");
        Assert.Equal((4, 2), (terminal.Buffer.X, terminal.Buffer.Y));

        Feed(terminal, "\e[?1049h");
        Assert.True(terminal.IsAlternateBufferActive);
        Assert.Equal(BufferType.Alternate, terminal.ActiveBuffer);
        Assert.Equal("", ScreenLine(terminal, 0).TranslateToString(trimRight: true));
        Feed(terminal, "\e[?1049l");
        Assert.False(terminal.IsAlternateBufferActive);
        Assert.Equal("main", ScreenLine(terminal, 0).TranslateToString(trimRight: true));
        Assert.Equal([BufferType.Alternate, BufferType.Normal], buffers);

        Feed(terminal, "\e[2;4r");
        Assert.Equal((1, 3), (terminal.Buffer.ScrollTop, terminal.Buffer.ScrollBottom));
        Feed(terminal, "\e[r\e[2J\e[H");
        for (var i = 0; i < 12; i++) Feed(terminal, $"L{i}\r\n");

        Assert.Equal(["L8", "L9", "L10", "L11", ""], terminal.GetVisibleLines());
        Assert.Equal(8, terminal.Buffer.YBase);
        Assert.Equal(13, terminal.Buffer.Lines.Length);
        Assert.Equal("L0", terminal.GetLine(0)); // counted from the top of the scrollback
    }

    [Fact]
    public void Handles_utf8_split_across_writes_and_wide_characters()
    {
        using var terminal = Create(cols: 10, rows: 3);
        var bytes = Encoding.UTF8.GetBytes("й中x"); // 2 + 3 + 1 bytes

        terminal.Write(bytes.AsSpan(0, 1));
        terminal.Write(bytes.AsSpan(1, 2));
        terminal.Write(bytes.AsSpan(3));

        var line = ScreenLine(terminal, 0);
        Assert.Equal(("й", 1), (line[0].Content, line[0].Width));
        Assert.Equal(("中", 2), (line[1].Content, line[1].Width));
        Assert.Equal(("", 0), (line[2].Content, line[2].Width));
        Assert.Equal(("x", 1), (line[3].Content, line[3].Width));
        Assert.Equal(4, terminal.Buffer.X);
        Assert.Equal("й中x", line.TranslateToString(trimRight: true));
    }

    [Fact]
    public void Reports_title_changes_and_terminal_replies()
    {
        using var terminal = Create();
        var titles = new List<string>();
        var replies = new List<string>();
        terminal.TitleChanged += (_, e) => titles.Add(e.Title);
        terminal.DataReceived += (_, e) => replies.Add(e.Data);

        Feed(terminal, "\e]0;first\a\e]2;second\e\\");
        Feed(terminal, "\e[2;3H\e[6n");

        Assert.Equal(["first", "second"], titles);
        Assert.Equal("second", terminal.Title);
        Assert.Equal(["\e[2;3R"], replies);
    }

    [Fact]
    public void Exposes_application_cursor_keys_and_bracketed_paste_modes()
    {
        using var terminal = Create();
        var sent = new List<string>();
        terminal.DataReceived += (_, e) => sent.Add(e.Data);

        Assert.False(terminal.ApplicationCursorKeys);
        Assert.False(terminal.BracketedPasteMode);
        Assert.Equal("\e[A", terminal.GenerateKeyInput(Key.UpArrow));
        Assert.Equal("\r", terminal.GenerateKeyInput(Key.Enter));
        Assert.Equal("\x7f", terminal.GenerateKeyInput(Key.Backspace));
        Assert.Equal("\x03", terminal.GenerateCharInput('c', KeyModifiers.Control));
        terminal.Paste("x\ny");
        Assert.Equal(["x\ry"], sent);

        Feed(terminal, "\e[?1h\e[?2004h");
        Assert.True(terminal.ApplicationCursorKeys);
        Assert.True(terminal.BracketedPasteMode);
        Assert.Equal("\eOA", terminal.GenerateKeyInput(Key.UpArrow));
        sent.Clear();
        terminal.Paste("ab\e[201~c\r\nd");
        Assert.Equal(["\e[200~ab[201~c\rd\e[201~"], sent); // the pasted ESC is dropped

        Feed(terminal, "\e[?1l\e[?2004l");
        Assert.False(terminal.ApplicationCursorKeys);
        Assert.False(terminal.BracketedPasteMode);
    }

    [Fact]
    public void Hides_and_shows_the_cursor()
    {
        using var terminal = Create();

        Assert.True(terminal.CursorVisible);
        Feed(terminal, "\e[?25l");
        Assert.False(terminal.CursorVisible);
        Feed(terminal, "\e[?25h");
        Assert.True(terminal.CursorVisible);
    }

    [Fact]
    public void Writes_a_string_like_bytes()
    {
        using var terminal = Create();

        terminal.Write("\e[1mй中\e[0mx");

        Assert.Equal("й中x", ScreenLine(terminal, 0).TranslateToString(trimRight: true));
        Assert.True(ScreenLine(terminal, 0)[0].Attributes.IsBold());
        Assert.Equal(4, terminal.Buffer.X);
    }

    [Fact]
    public void Resizes()
    {
        using var terminal = Create();
        var sizes = new List<(int, int)>();
        terminal.Resized += (_, e) => sizes.Add((e.Cols, e.Rows));
        Feed(terminal, "kept");

        terminal.Resize(30, 8);

        Assert.Equal((30, 8), (terminal.Cols, terminal.Rows));
        Assert.Equal(30, ScreenLine(terminal, 0).Length);
        Assert.Equal(8, terminal.GetVisibleLines().Length);
        Assert.Equal("kept", ScreenLine(terminal, 0).TranslateToString(trimRight: true));
        Assert.Equal([(30, 8)], sizes);
    }

    [Fact]
    public void Package_is_pure_managed()
    {
        var assembly = typeof(XTermTerminal).Assembly;
        var output = AppContext.BaseDirectory;

        Assert.Equal(Path.Combine(output, "XTerm.NET.dll"), assembly.Location);
        var runtimes = Path.Combine(output, "runtimes");
        if (Directory.Exists(runtimes))
            Assert.Empty(Directory.EnumerateFiles(runtimes, "*XTerm*", SearchOption.AllDirectories));
        Assert.All(assembly.GetReferencedAssemblies(), name =>
            Assert.True(name.Name!.StartsWith("System.", StringComparison.Ordinal) || name.Name is "netstandard",
                name.FullName));
        using var pe = new PEReader(File.OpenRead(assembly.Location));
        Assert.True(pe.PEHeaders.CorHeader!.Flags.HasFlag(CorFlags.ILOnly));
        Assert.DoesNotContain(assembly.GetTypes().SelectMany(t =>
                t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static |
                             BindingFlags.Instance | BindingFlags.DeclaredOnly)),
            m => m.Attributes.HasFlag(MethodAttributes.PinvokeImpl));
    }
}
