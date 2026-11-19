using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Tandem.App.Resources;
using Tandem.App.Tests.ViewModels;
using Tandem.App.ViewModels;
using Tandem.App.Views;
using Tandem.Core.Vfs;
using Tandem.Core.Vfs.InMemory;

namespace Tandem.App.Tests.Views;

/// <summary>The command line and its output pane in the main window, on the in-memory "local disk" and a fake
/// process runner. Both panels start in /home.</summary>
[Collection(LanguageCollection.Name)]
public class CommandLineWindowTests : LanguageTestBase
{
    private sealed class Fixture : IAsyncDisposable
    {
        public required TestApp App { get; init; }
        public required FakeProcessRunner Runner { get; init; }
        public CommandLineViewModel Line => App.Main.CommandLine;
        public TextBox Field => App.Find<TextBox>("CommandLineBox");
        public OutputPane Pane => App.Find<OutputPane>("OutputPane");
        public ValueTask DisposeAsync() => App.DisposeAsync();
    }

    private static async Task<Fixture> StartAsync(IReadOnlyList<IFileSystemProvider>? extraProviders = null)
    {
        var runner = new FakeProcessRunner();
        var app = await TestApp.StartAsync(processes: runner, extraProviders: extraProviders);
        return new Fixture { App = app, Runner = runner };
    }

    private static void FocusField(Fixture f)
    {
        f.App.Press(Key.L, RawInputModifiers.Meta, PhysicalKey.L, "l");
        Assert.Same(f.Field, f.App.Focused);
    }

    private static async Task RunAsync(Fixture f, string text)
    {
        FocusField(f);
        f.App.Type(text);
        f.App.Press(Key.Enter, physical: PhysicalKey.Enter);
        await TestApp.WaitUntil(() => f.Runner.Processes.Count > 0 && f.Line.IsRunning);
        f.App.Pump();
    }

    [AvaloniaFact]
    public async Task Enter_runs_the_typed_command_and_opens_the_output_pane()
    {
        await using var f = await StartAsync();
        var splitter = f.App.Find<GridSplitter>("OutputSplitter");
        Assert.False(f.Pane.IsVisible);
        Assert.False(splitter.IsVisible);

        await RunAsync(f, "ls -la");

        var spec = Assert.Single(f.Runner.Started);
        Assert.Equal(["-lc", "ls -la"], spec.Arguments);
        Assert.Equal(TestApp.Home, spec.WorkingDirectory);
        Assert.Equal("", f.Field.Text);
        Assert.True(f.Pane.IsVisible);
        Assert.True(splitter.IsVisible);
        Assert.True(f.App.Find<TextBlock>("RunningNote").IsVisible);
        Assert.True(f.App.Find<Button>("KillButton").IsVisible);
        Assert.InRange(f.Pane.Bounds.Height, MainWindow.DefaultOutputHeight - 1, MainWindow.DefaultOutputHeight + 1);

        f.Runner.Last.Write("total 3");
        f.Runner.Last.Exit(0);
        await TestApp.WaitUntil(() => !f.Line.IsRunning);
        f.App.Pump();

        Assert.Equal($"/home> ls -la\ntotal 3\n{Loc.Format("Output_ExitCode", 0)}", f.Pane.Text.Text);
        Assert.False(f.App.Find<TextBlock>("RunningNote").IsVisible);
        Assert.False(f.App.Find<Button>("KillButton").IsVisible);
        Assert.Same(f.Field, f.App.Focused);
    }

    [AvaloniaFact]
    public async Task Cd_in_the_field_moves_the_active_panel()
    {
        await using var f = await StartAsync();
        FocusField(f);
        f.App.Type("cd docs");

        f.App.Press(Key.Enter, physical: PhysicalKey.Enter);
        await TestApp.WaitUntil(() => f.App.Left.CurrentPath == VfsPath.Local("/home/docs"));
        f.App.Pump();

        Assert.Equal("", f.Field.Text);
        Assert.Empty(f.Runner.Started);
        Assert.False(f.Pane.IsVisible);
    }

    [AvaloniaFact]
    public async Task Panel_keys_do_not_fire_while_the_field_has_the_focus()
    {
        await using var f = await StartAsync();
        var cursor = f.App.Left.CursorIndex;
        FocusField(f);

        f.App.Press(Key.Down, physical: PhysicalKey.ArrowDown);
        f.App.Press(Key.Space, physical: PhysicalKey.Space, symbol: " ");
        f.App.Type(" ");
        f.App.Type("do");
        f.App.Press(Key.Back, physical: PhysicalKey.Backspace);
        f.App.Press(Key.Enter, physical: PhysicalKey.Enter); // runs " d", not the cursor row

        Assert.Equal(cursor, f.App.Left.CursorIndex);
        Assert.DoesNotContain(f.App.Left.Rows, r => r.IsSelected);
        Assert.Equal(VfsPath.Local(TestApp.Home), f.App.Left.CurrentPath);
        Assert.Equal("", f.App.Left.QuickSearchText);
        await TestApp.WaitUntil(() => f.Runner.Started.Count == 1);
        Assert.Equal(["-lc", " d"], f.Runner.Started[0].Arguments);
        f.Runner.Last.Exit(0);
        await TestApp.WaitUntil(() => !f.Line.IsRunning);
    }

    [AvaloniaFact]
    public async Task Up_and_down_walk_the_history_in_the_field()
    {
        await using var f = await StartAsync();
        FocusField(f);
        foreach (var text in new[] { "cd docs", "cd .." })
        {
            f.App.Type(text);
            f.App.Press(Key.Enter, physical: PhysicalKey.Enter);
            await TestApp.WaitUntil(() => f.Field.Text == "");
        }
        f.App.Type("dra");

        f.App.Press(Key.Up, physical: PhysicalKey.ArrowUp);
        Assert.Equal("cd ..", f.Field.Text);
        Assert.Equal(5, f.Field.CaretIndex);
        f.App.Press(Key.Up, physical: PhysicalKey.ArrowUp);
        Assert.Equal("cd docs", f.Field.Text);
        f.App.Press(Key.Down, physical: PhysicalKey.ArrowDown);
        f.App.Press(Key.Down, physical: PhysicalKey.ArrowDown);
        Assert.Equal("dra", f.Field.Text);
        Assert.Equal(VfsPath.Local(TestApp.Home), f.App.Left.CurrentPath);
    }

    [AvaloniaFact]
    public async Task Esc_clears_the_text_then_returns_to_the_panel()
    {
        await using var f = await StartAsync();
        FocusField(f);
        f.App.Type("ls");

        f.App.Press(Key.Escape, physical: PhysicalKey.Escape);
        Assert.Equal("", f.Field.Text);
        Assert.Same(f.Field, f.App.Focused);

        f.App.Press(Key.Escape, physical: PhysicalKey.Escape);
        Assert.True(f.App.FileList(PanelSide.Left).IsKeyboardFocusWithin);
    }

    [AvaloniaFact]
    public async Task Ctrl_C_kills_the_running_command_from_the_field_or_the_output()
    {
        await using var f = await StartAsync();
        await RunAsync(f, "sleep 100");

        f.App.Press(Key.C, RawInputModifiers.Control, PhysicalKey.C, "c");
        await TestApp.WaitUntil(() => !f.Line.IsRunning);
        Assert.Equal(1, f.Runner.Last.Kills);

        await RunAsync(f, "sleep 200");
        f.Pane.Text.Focus();
        f.App.Press(Key.C, RawInputModifiers.Control, PhysicalKey.C, "c");
        await TestApp.WaitUntil(() => !f.Line.IsRunning);
        Assert.Equal(1, f.Runner.Last.Kills);
    }

    [AvaloniaFact]
    public async Task Cmd_C_in_the_output_does_not_kill()
    {
        await using var f = await StartAsync();
        await RunAsync(f, "sleep 100");
        f.Pane.Text.Focus();
        f.Pane.Text.SelectAll();

        f.App.Press(Key.C, RawInputModifiers.Meta, PhysicalKey.C, "c");

        Assert.True(f.Line.IsRunning);
        Assert.Equal(0, f.Runner.Last.Kills);
        f.Line.Kill();
        await TestApp.WaitUntil(() => !f.Line.IsRunning);
    }

    [AvaloniaFact]
    public async Task Kill_clear_and_close_buttons()
    {
        await using var f = await StartAsync();
        await RunAsync(f, "sleep 100");

        f.App.Click(f.App.Find<Button>("KillButton"));
        await TestApp.WaitUntil(() => !f.Line.IsRunning);
        f.App.Pump();
        Assert.Equal(1, f.Runner.Last.Kills);

        f.App.Click(f.App.Find<Button>("ClearButton"));
        Assert.Equal("", f.Pane.Text.Text);
        Assert.True(f.Pane.IsVisible);

        f.App.Click(f.App.Find<Button>("CloseButton"));
        Assert.False(f.Pane.IsVisible);
        Assert.False(f.App.Find<GridSplitter>("OutputSplitter").IsVisible);
        Assert.Equal(0, f.App.Find<Grid>("WorkArea").RowDefinitions[2].Height.Value);
    }

    [AvaloniaFact]
    public async Task A_closed_pane_is_not_updated_and_catches_up_when_it_opens_again()
    {
        await using var f = await StartAsync();
        await RunAsync(f, "tail -f log");
        f.App.Click(f.App.Find<Button>("CloseButton"));
        var shown = f.Pane.Text.Text;

        f.Runner.Last.Write("while closed");
        await TestApp.WaitUntil(() => f.Line.Output.Contains("while closed"));
        f.App.Pump();
        Assert.False(f.Pane.IsVisible);
        Assert.Equal(shown, f.Pane.Text.Text);

        f.Line.IsOutputVisible = true;
        f.App.Pump();
        Assert.Equal(f.Line.Output, f.Pane.Text.Text);
        f.Line.Kill();
        await TestApp.WaitUntil(() => !f.Line.IsRunning);
    }

    [AvaloniaFact]
    public async Task The_output_pane_keeps_its_height_when_it_opens_again()
    {
        await using var f = await StartAsync();
        f.Line.IsOutputVisible = true;
        f.App.Pump();
        f.App.Find<Grid>("WorkArea").RowDefinitions[2].Height = new GridLength(250); // as the splitter would
        f.App.Pump();

        f.Line.IsOutputVisible = false;
        f.App.Pump();
        f.Line.IsOutputVisible = true;
        f.App.Pump();

        Assert.InRange(f.Pane.Bounds.Height, 249, 251);
    }

    [AvaloniaFact]
    public async Task Output_follows_the_end_unless_scrolled_up()
    {
        await using var f = await StartAsync();
        await RunAsync(f, "seq");
        var process = f.Runner.Last;

        for (var i = 0; i < 200; i++) process.Write($"line {i}");
        await TestApp.WaitUntil(() => f.Line.Output.Contains("line 199"));
        f.App.Pump();
        var scroller = f.Pane.Scroller!;
        Assert.True(scroller.Extent.Height > scroller.Viewport.Height);
        Assert.Equal(scroller.Extent.Height - scroller.Viewport.Height, scroller.Offset.Y, 1);

        scroller.Offset = scroller.Offset.WithY(0);
        f.App.Pump();
        for (var i = 200; i < 400; i++) process.Write($"line {i}");
        await TestApp.WaitUntil(() => f.Line.Output.Contains("line 399"));
        f.App.Pump();
        Assert.Equal(0, scroller.Offset.Y, 1);

        scroller.Offset = scroller.Offset.WithY(scroller.Extent.Height - scroller.Viewport.Height);
        f.App.Pump();
        process.Exit(0);
        await TestApp.WaitUntil(() => !f.Line.IsRunning);
        f.App.Pump();
        Assert.Equal(scroller.Extent.Height - scroller.Viewport.Height, scroller.Offset.Y, 1);
    }

    [AvaloniaFact]
    public async Task The_field_is_disabled_with_a_placeholder_on_a_non_local_source()
    {
        var remote = new InMemoryFileSystem("remote");
        remote.AddDirectory("/shared/dir");
        await using var f = await StartAsync([remote]);
        Assert.True(f.Field.IsEnabled);

        await f.App.Left.NavigateToAsync(new VfsPath("remote", "/shared"));
        f.App.Pump();

        Assert.False(f.Field.IsEnabled);
        Assert.Equal(Loc.Get("CommandLine_Unavailable"), f.Field.PlaceholderText);
        f.App.Press(Key.L, RawInputModifiers.Meta, PhysicalKey.L, "l");
        Assert.NotSame(f.Field, f.App.Focused);
    }

    [AvaloniaFact]
    public async Task Cmd_L_does_nothing_while_the_command_line_is_hidden()
    {
        await using var f = await StartAsync();
        f.App.Main.ToggleCommandLine();
        f.App.Pump();

        f.App.Press(Key.L, RawInputModifiers.Meta, PhysicalKey.L, "l");

        Assert.NotSame(f.Field, f.App.Focused);
        Assert.False(f.App.Main.Workspace.View.CommandLine); // not shown by the command
        Assert.True(f.App.FileList(PanelSide.Left).IsKeyboardFocusWithin);
    }

    [AvaloniaFact]
    public async Task Closing_the_window_kills_a_running_command()
    {
        await using var f = await StartAsync();
        await RunAsync(f, "sleep 100");

        f.App.Window.Close();
        f.App.Pump();

        Assert.True(f.Runner.Last.IsDisposed);
        Assert.True(f.Runner.Last.HasExited);
    }
}
