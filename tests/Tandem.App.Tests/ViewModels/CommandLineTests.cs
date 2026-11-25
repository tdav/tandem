using Avalonia.Headless.XUnit;
using Tandem.App.Resources;
using Tandem.App.Tests.Views;
using Tandem.App.ViewModels;
using Tandem.Core.Platform;
using Tandem.Core.Settings;
using Tandem.Core.Vfs;
using Tandem.Core.Vfs.InMemory;

namespace Tandem.App.Tests.ViewModels;

/// <summary>The command line on the whole app over the in-memory "local disk" and a fake process runner: no real
/// process is started. The active (left) panel starts in /a/b; home is /home.</summary>
[Collection(LanguageCollection.Name)]
public class CommandLineTests : LanguageTestBase
{
    private const string Remote = "remote";

    private static InMemoryFileSystem FileSystem()
    {
        var fs = TestApp.DefaultFileSystem();
        fs.AddDirectory("/a/b/c");
        fs.AddDirectory("/a/b/my dir");
        fs.AddFile("/a/b/file.txt");
        fs.AddDirectory("/x");
        return fs;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required TestApp App { get; init; }
        public required FakeProcessRunner Runner { get; init; }
        public required CommandLineViewModel Line { get; init; }
        public required ManualFlush Flush { get; init; }
        /// <summary>The grace period after the shell exits: held (never over) unless released.</summary>
        public required ManualFlush Grace { get; init; }
        public TerminalChoice Terminal { get; set; } = TerminalChoice.Default;
        public PanelViewModel Panel => App.Left;

        public async ValueTask DisposeAsync()
        {
            Line.Dispose();
            await App.DisposeAsync();
        }
    }

    /// <summary>The output flush delay: completes at once unless <see cref="Hold"/> is set, then only when released.</summary>
    private sealed class ManualFlush
    {
        private readonly List<TaskCompletionSource> _held = [];
        public bool Hold { get; set; }
        public int Calls { get; private set; }

        public Task Wait(TimeSpan delay, CancellationToken ct)
        {
            lock (_held)
            {
                Calls++;
                if (!Hold) return Task.CompletedTask;
                var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _held.Add(done);
                return done.Task;
            }
        }

        public int HeldCount
        {
            get { lock (_held) return _held.Count; }
        }

        public void ReleaseAll()
        {
            lock (_held)
            {
                foreach (var done in _held) done.TrySetResult();
                _held.Clear();
            }
        }
    }

    private static async Task<Fixture> StartAsync(string folder = "/a/b")
    {
        var app = await TestApp.StartAsync(FileSystem(), show: false, extraProviders: [RemoteFileSystem()]);
        await app.Left.NavigateToAsync(VfsPath.Local(folder));
        var runner = new FakeProcessRunner();
        var flush = new ManualFlush();
        var grace = new ManualFlush { Hold = true };
        var holder = new Fixture[1];
        var line = new CommandLineViewModel(app.Main.Workspace, runner, TestApp.Home, flush.Wait, grace.Wait,
            app.Shell, () => holder[0].Terminal);
        return holder[0] = new Fixture { App = app, Runner = runner, Line = line, Flush = flush, Grace = grace };
    }

    private static InMemoryFileSystem RemoteFileSystem()
    {
        var fs = new InMemoryFileSystem(Remote);
        fs.AddDirectory("/shared/dir");
        return fs;
    }

    private static string[] OutputLines(CommandLineViewModel line) =>
        line.Output.Length == 0 ? [] : line.Output.Split('\n');

    /// <summary>Starts <paramref name="text"/> and returns the task that ends with the command.</summary>
    private static async Task<Task> StartCommandAsync(Fixture f, string text)
    {
        f.Line.Text = text;
        var running = f.Line.ExecuteAsync();
        await TestApp.WaitUntil(() => f.Runner.Processes.Count > 0 && f.Line.IsRunning);
        return running;
    }

    // ---- A bare shell name opens a terminal -----------------------------------------------------------------

    [AvaloniaTheory]
    [InlineData("cmd")]
    [InlineData("  powershell  ")]
    [InlineData("pwsh")]
    [InlineData("bash")]
    [InlineData("zsh")]
    [InlineData("sh")]
    [InlineData("terminal")]
    public async Task A_bare_shell_name_opens_the_terminal_in_the_folder_and_runs_nothing(string input)
    {
        await using var f = await StartAsync();
        f.Terminal = TerminalChoice.Cmd;
        f.Line.Text = input;

        await f.Line.ExecuteAsync();

        Assert.Equal([("terminal", "/a/b")], f.App.Shell.Calls);
        Assert.Equal([TerminalChoice.Cmd], f.App.Shell.Terminals);
        Assert.Empty(f.Runner.Started);
        Assert.Equal("", f.Line.Text);
        Assert.Equal("", f.Line.Output);
        Assert.False(f.Line.IsOutputVisible);
        Assert.Equal([input], f.Line.History);
    }

    [AvaloniaFact]
    public async Task Windows_shell_names_ignore_case_and_exe()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only: names are case-sensitive elsewhere");
        await using var f = await StartAsync();

        foreach (var input in new[] { "CMD", "cmd.exe", "PowerShell.EXE", "\"cmd\"" })
        {
            f.Line.Text = input;
            await f.Line.ExecuteAsync();
        }

        Assert.Equal(4, f.App.Shell.Calls.Count);
        Assert.Empty(f.Runner.Started);
    }

    [AvaloniaTheory]
    [InlineData("cmd /c dir")]
    [InlineData("cmd /k dir")]
    [InlineData("bash -c \"ls\"")]
    [InlineData("powershell -Command Get-Date")]
    [InlineData("command")]
    [InlineData("shell")]
    [InlineData("cmdx")]
    [InlineData("echo cmd")]
    public async Task A_shell_with_arguments_or_another_command_runs_as_before(string input)
    {
        await using var f = await StartAsync();

        var running = await StartCommandAsync(f, input);
        f.Runner.Processes[0].Exit(0);
        await running;

        Assert.Empty(f.App.Shell.Calls);
    }

    [AvaloniaFact]
    public async Task A_failed_terminal_goes_to_the_output_and_the_text_stays()
    {
        await using var f = await StartAsync();
        f.App.Shell.Failure = new IOException("no terminal");
        f.Line.Text = "cmd";

        await f.Line.ExecuteAsync();

        Assert.Equal("cmd", f.Line.Text);
        Assert.True(f.Line.IsOutputVisible);
        Assert.Contains("no terminal", f.Line.Output);
        Assert.Empty(f.Runner.Started);
    }

    [AvaloniaTheory]
    [InlineData("cmd", true)]
    [InlineData("cmd arg", false)]
    [InlineData("", false)]
    [InlineData("\"cmd", false)]
    public void IsBareShell_knows_a_lone_shell_name(string text, bool expected)
    {
        Assert.Equal(expected, CommandLineViewModel.IsBareShell(text));
    }

    // ---- cd ---------------------------------------------------------------------------------------------

    [AvaloniaTheory]
    [InlineData("cd ..", "/a")]
    [InlineData("cd c", "/a/b/c")]
    [InlineData("cd /x", "/x")]
    [InlineData("cd ~", "/home")]
    [InlineData("cd \"my dir\"", "/a/b/my dir")]
    [InlineData("cd", "/home")]
    public async Task Cd_navigates(string input, string expected)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "zsh and Unix cd; CdCommandParserWindowsTests covers Windows.");
        await using var f = await StartAsync();
        f.Line.Text = input;

        await f.Line.ExecuteAsync();

        Assert.Equal(VfsPath.Local(expected), f.Panel.CurrentPath);
        Assert.Equal(expected, f.Line.Prompt);
        Assert.Equal("", f.Line.Text);
        Assert.Empty(f.Runner.Started);
        Assert.Equal("", f.Line.Output);
        Assert.False(f.Line.IsOutputVisible);
    }

    [AvaloniaFact]
    public async Task Cd_missing_reports_and_stays()
    {
        await using var f = await StartAsync();
        f.Line.Text = "cd nowhere";

        await f.Line.ExecuteAsync();

        Assert.Equal(VfsPath.Local("/a/b"), f.Panel.CurrentPath);
        Assert.Equal("cd nowhere", f.Line.Text);
        Assert.True(f.Line.IsOutputVisible);
        // The panel's own reason, which names the folder once.
        Assert.Equal([Loc.Format("Panel_Error_NotFound", "/a/b/nowhere")], OutputLines(f.Line));
        Assert.Empty(f.Runner.Started);
    }

    [AvaloniaFact]
    public async Task A_cd_in_flight_keeps_the_command_line_busy()
    {
        await using var f = await StartAsync();
        f.Line.Text = "cd c";
        var cd = f.Line.ExecuteAsync(); // reading the folder happens in the background
        Assert.False(cd.IsCompleted);

        f.Line.Text = "ls";
        await f.Line.ExecuteAsync();
        f.Line.Text = "cd /x";
        await f.Line.ExecuteAsync();
        await cd;

        Assert.Empty(f.Runner.Started);
        Assert.Equal(VfsPath.Local("/a/b/c"), f.Panel.CurrentPath);
        Assert.Equal("cd /x", f.Line.Text);
        Assert.Equal(["cd c"], f.Line.History);
    }

    [AvaloniaFact]
    public async Task A_compound_command_after_cd_is_explained()
    {
        await using var f = await StartAsync();
        f.Line.Text = "cd c && make";

        await f.Line.ExecuteAsync();

        Assert.Equal([Loc.Get("CommandLine_CdTooManyArguments")], OutputLines(f.Line));
        Assert.Contains("cd", Loc.Get("CommandLine_CdTooManyArguments"));
        Assert.Equal(VfsPath.Local("/a/b"), f.Panel.CurrentPath);
    }

    [AvaloniaFact]
    public async Task Cd_to_a_file_reports_and_stays()
    {
        await using var f = await StartAsync();
        f.Line.Text = "cd file.txt";

        await f.Line.ExecuteAsync();

        Assert.Equal(VfsPath.Local("/a/b"), f.Panel.CurrentPath);
        Assert.Contains("/a/b/file.txt", f.Line.Output);
    }

    [AvaloniaTheory]
    [InlineData("cd -", "CommandLine_CdPreviousUnsupported")]
    [InlineData("cd ~user", "CommandLine_CdUserHomeUnsupported")]
    [InlineData("cd 'my dir", "CommandLine_CdUnterminated")]
    [InlineData("cd a b", "CommandLine_CdTooManyArguments")]
    public async Task Unsupported_cd_reports_and_stays(string input, string message)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "zsh and Unix cd; CdCommandParserWindowsTests covers Windows.");
        await using var f = await StartAsync();
        f.Line.Text = input;

        await f.Line.ExecuteAsync();

        Assert.Equal(VfsPath.Local("/a/b"), f.Panel.CurrentPath);
        Assert.Equal(input, f.Line.Text);
        Assert.Equal([Loc.Get(message)], OutputLines(f.Line));
        Assert.True(f.Line.IsOutputVisible);
        Assert.Empty(f.Runner.Started);
    }

    // ---- Running commands -------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task Other_command_runs_zsh_in_panel_directory()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "zsh and Unix cd; CdCommandParserWindowsTests covers Windows.");
        await using var f = await StartAsync();
        var running = await StartCommandAsync(f, "ls -la");

        var spec = Assert.Single(f.Runner.Started);
        Assert.Equal("/bin/zsh", spec.FileName);
        Assert.Equal(["-lc", "ls -la"], spec.Arguments);
        Assert.Equal("/a/b", spec.WorkingDirectory);
        Assert.Equal("", f.Line.Text);
        Assert.True(f.Line.IsOutputVisible);

        f.Runner.Last.Exit(0);
        await running;
        Assert.False(f.Line.IsRunning);
    }

    [AvaloniaTheory]
    [InlineData("  echo 'a  b' | wc -c  ")]
    [InlineData("cdx")]
    [InlineData("cd.")]
    [InlineData("echo \"$HOME\" *.txt; ls")]
    public async Task The_command_is_passed_as_one_argument_exactly_as_typed(string text)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "zsh and Unix cd; CdCommandParserWindowsTests covers Windows.");
        await using var f = await StartAsync();
        var running = await StartCommandAsync(f, text);

        Assert.Equal(["-lc", text], Assert.Single(f.Runner.Started).Arguments);
        f.Runner.Last.Exit(0);
        await running;
    }

    [AvaloniaFact]
    public async Task On_windows_cmd_gets_the_command_line_as_typed_quotes_and_all()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "cmd.exe on Windows");
        await using var f = await StartAsync();
        const string text = "dir \"C:\\Program Files\" & echo done";
        var running = await StartCommandAsync(f, text);

        var spec = Assert.Single(f.Runner.Started);
        Assert.Equal("cmd.exe", spec.FileName);
        Assert.Equal("/s /c \"" + text + "\"", spec.CommandLine);
        f.Runner.Last.Exit(0);
        await running;
    }

    [AvaloniaFact]
    public async Task Output_is_appended_as_it_arrives_and_ends_with_the_exit_code()
    {
        await using var f = await StartAsync();
        var running = await StartCommandAsync(f, "make");

        f.Runner.Last.Write("one", "two");
        await TestApp.WaitUntil(() => f.Line.Output.Contains("two"));
        Assert.True(f.Line.IsRunning);
        f.Runner.Last.Write("three");
        f.Runner.Last.Exit(2);
        await running;

        Assert.Equal(["/a/b> make", "one", "two", "three", Loc.Format("Output_ExitCode", 2)], OutputLines(f.Line));
        Assert.False(f.Line.IsRunning);
    }

    [AvaloniaFact]
    public async Task Output_is_flushed_in_batches()
    {
        await using var f = await StartAsync();
        f.Flush.Hold = true;
        var running = await StartCommandAsync(f, "chatty");
        var before = f.Line.Output;

        for (var i = 0; i < 1000; i++) f.Runner.Last.Write($"line {i}");
        await TestApp.WaitUntil(() => f.Flush.HeldCount > 0);
        await Task.Delay(50);
        Assert.Equal(before, f.Line.Output); // nothing shown until the flush interval has passed
        Assert.Equal(1, f.Flush.HeldCount); // one flush pending for the whole burst

        var changes = 0;
        f.Line.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CommandLineViewModel.Output)) changes++;
        };
        f.Flush.ReleaseAll();
        await TestApp.WaitUntil(() => f.Line.Output.Contains("line 999"));
        Assert.Equal(1, changes);

        f.Flush.Hold = false;
        f.Runner.Last.Exit(0);
        await running;
    }

    [AvaloniaFact]
    public async Task Output_is_appended_and_capped_at_5000_lines()
    {
        await using var f = await StartAsync();
        var running = await StartCommandAsync(f, "seq 6000");

        for (var i = 1; i <= 6000; i++) f.Runner.Last.Write($"line {i}");
        f.Runner.Last.Exit(0);
        await running;

        var lines = OutputLines(f.Line);
        Assert.Equal(CommandLineViewModel.MaxOutputLines + 1, lines.Length);
        Assert.Equal(Loc.Get("Output_EarlierDropped"), lines[0]);
        Assert.Equal("line 1002", lines[1]); // 6002 lines: the echo, 6000 output lines, the exit code
        Assert.Equal("line 6000", lines[^2]);
        Assert.Equal(Loc.Format("Output_ExitCode", 0), lines[^1]);
        Assert.Single(lines, l => l == Loc.Get("Output_EarlierDropped"));
    }

    [AvaloniaFact]
    public async Task The_dropped_marker_appears_once_over_several_commands()
    {
        await using var f = await StartAsync();
        for (var run = 0; run < 2; run++)
        {
            var running = await StartCommandAsync(f, "seq");
            for (var i = 0; i < 4000; i++) f.Runner.Last.Write($"r{run} {i}");
            f.Runner.Last.Exit(0);
            await running;
        }

        var lines = OutputLines(f.Line);
        Assert.Equal(CommandLineViewModel.MaxOutputLines + 1, lines.Length);
        Assert.Single(lines, l => l == Loc.Get("Output_EarlierDropped"));
        Assert.Equal(Loc.Get("Output_EarlierDropped"), lines[0]);
    }

    [AvaloniaFact]
    public async Task Ansi_escape_sequences_are_stripped()
    {
        await using var f = await StartAsync();
        var running = await StartCommandAsync(f, "ls --color");

        f.Runner.Last.Write("\e[1;31mred\e[0m plain", "\e]0;title\atext", "\e[2Kcleared\r");
        f.Runner.Last.Exit(0);
        await running;

        Assert.Equal(["red plain", "text", "cleared"], OutputLines(f.Line)[1..4]);
    }

    [AvaloniaFact]
    public async Task A_carriage_return_overwrites_the_line_and_control_characters_are_removed()
    {
        await using var f = await StartAsync();
        var running = await StartCommandAsync(f, "curl -O x");

        f.Runner.Last.Write("10%\r50%\r\e[32m100%\e[0m\r", "a\bb\ac\td\x7f\x01", "\r", "\e");
        f.Runner.Last.Exit(0);
        await running;

        Assert.Equal(["100%", "abc\td", "", ""], OutputLines(f.Line)[1..5]);
    }

    [AvaloniaFact]
    public async Task Exit_refreshes_the_panel_that_ran_the_command()
    {
        await using var f = await StartAsync();
        var running = await StartCommandAsync(f, "touch new.txt");

        f.App.Fs.AddFile("/a/b/new.txt");
        f.Runner.Last.Exit(0);
        await running;

        await TestApp.WaitUntil(() => f.Panel.Rows.Any(r => r.Name == "new.txt"));
    }

    [AvaloniaFact]
    public async Task Exit_does_not_refresh_a_panel_that_moved_elsewhere()
    {
        await using var f = await StartAsync();
        var running = await StartCommandAsync(f, "sleep 1");
        await f.Panel.NavigateToAsync(VfsPath.Local("/x"));
        var path = f.Panel.CurrentPath;
        var loads = 0;
        f.Panel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PanelViewModel.IsLoading) && f.Panel.IsLoading) loads++;
        };

        f.Runner.Last.Exit(0);
        await running;
        await Task.Delay(50);

        Assert.Equal(0, loads);
        Assert.Equal(path, f.Panel.CurrentPath);
    }

    [AvaloniaFact]
    public async Task Kill_stops_running_process()
    {
        await using var f = await StartAsync();
        var running = await StartCommandAsync(f, "sleep 100");

        f.Line.Kill();
        await running;

        Assert.Equal(1, f.Runner.Last.Kills);
        Assert.True(f.Runner.Last.IsDisposed);
        Assert.False(f.Line.IsRunning);
        Assert.Equal(Loc.Format("Output_ExitCode", FakeProcess.KilledExitCode), OutputLines(f.Line)[^1]);
        f.Line.Kill(); // nothing running: nothing happens
        Assert.Equal(1, f.Runner.Last.Kills);
    }

    [AvaloniaFact]
    public async Task Kill_ends_a_command_whose_shell_exited_but_whose_output_stays_open()
    {
        await using var f = await StartAsync();
        var running = await StartCommandAsync(f, "sleep 300 &");
        f.Runner.Last.ExitKeepingOutputOpen(0);
        await Task.Delay(50);
        Assert.True(f.Line.IsRunning); // the grace period is held

        f.Line.Kill();
        await running.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(f.Line.IsRunning);
        Assert.True(f.Runner.Last.IsDisposed);
        Assert.Equal(Loc.Format("Output_ExitCode", 0), OutputLines(f.Line)[^1]);
        await StartCommandAsync(f, "ls"); // not stuck
        Assert.Equal(2, f.Runner.Started.Count);
        f.Runner.Last.Exit(0);
    }

    [AvaloniaFact]
    public async Task A_command_ends_a_grace_period_after_its_shell_exits_even_if_output_stays_open()
    {
        await using var f = await StartAsync();
        var running = await StartCommandAsync(f, "python3 -m http.server &");
        var process = f.Runner.Last;

        process.ExitKeepingOutputOpen(0);
        await TestApp.WaitUntil(() => f.Grace.HeldCount == 1);
        process.Write("still writing");
        await TestApp.WaitUntil(() => f.Line.Output.Contains("still writing"));
        Assert.True(f.Line.IsRunning);
        Assert.Equal(0, process.Kills);

        f.Grace.ReleaseAll();
        await running.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(f.Line.IsRunning);
        Assert.True(process.IsDisposed); // closes the pipe of what the shell left behind
        Assert.False(process.IsOutputOpen);
        Assert.Equal(["still writing", Loc.Format("Output_ExitCode", 0)], OutputLines(f.Line)[^2..]);
    }

    [AvaloniaFact]
    public async Task Closing_the_pane_while_a_command_writes_keeps_it_closed()
    {
        await using var f = await StartAsync();
        var running = await StartCommandAsync(f, "tail -f log");
        Assert.True(f.Line.IsOutputVisible);

        f.Line.CloseOutputCommand.Execute(null);
        f.Runner.Last.Write("more");
        await TestApp.WaitUntil(() => f.Line.Output.Contains("more"));
        Assert.False(f.Line.IsOutputVisible);

        f.Runner.Last.Exit(0);
        await running;
        Assert.False(f.Line.IsOutputVisible);

        await StartCommandAsync(f, "ls"); // a new command opens it again
        Assert.True(f.Line.IsOutputVisible);
        f.Runner.Last.Exit(0);
    }

    [AvaloniaFact]
    public async Task A_flood_between_two_flushes_keeps_only_the_last_lines()
    {
        await using var f = await StartAsync();
        f.Flush.Hold = true;
        var running = await StartCommandAsync(f, "yes");

        for (var i = 0; i < 12000; i++) f.Runner.Last.Write($"y {i}");
        f.Runner.Last.Exit(0);
        await TestApp.WaitUntil(() => f.Flush.HeldCount > 0);
        f.Flush.Hold = false;
        f.Flush.ReleaseAll();
        await running;

        var lines = OutputLines(f.Line);
        Assert.Equal(CommandLineViewModel.MaxOutputLines + 1, lines.Length);
        Assert.Equal(Loc.Get("Output_EarlierDropped"), lines[0]);
        Assert.Equal("y 7001", lines[1]);
        Assert.Equal("y 11999", lines[^2]);
        Assert.Equal(Loc.Format("Output_ExitCode", 0), lines[^1]);
    }

    [AvaloniaFact]
    public async Task A_very_long_line_is_shortened()
    {
        await using var f = await StartAsync();
        var running = await StartCommandAsync(f, "cat big.json");

        f.Runner.Last.Write(new string('x', 10000), new string('y', CommandLineViewModel.MaxLineLength));
        f.Runner.Last.Exit(0);
        await running;

        var lines = OutputLines(f.Line);
        Assert.Equal(new string('x', CommandLineViewModel.MaxLineLength) + Loc.Get("Output_LineShortened"), lines[1]);
        Assert.Equal(new string('y', CommandLineViewModel.MaxLineLength), lines[2]);
    }

    [Fact]
    public void Output_is_flushed_about_ten_times_a_second()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(100), CommandLineViewModel.FlushInterval);
        Assert.Equal(TimeSpan.FromMilliseconds(500), CommandLineViewModel.ExitGracePeriod);
    }

    [AvaloniaFact]
    public async Task A_second_command_does_not_start_while_one_runs()
    {
        await using var f = await StartAsync();
        var running = await StartCommandAsync(f, "sleep 100");

        f.Line.Text = "ls";
        await f.Line.ExecuteAsync();
        f.Line.Text = "cd /x";
        await f.Line.ExecuteAsync();

        Assert.Single(f.Runner.Started);
        Assert.Equal(VfsPath.Local("/a/b"), f.Panel.CurrentPath);
        Assert.Equal("cd /x", f.Line.Text);
        f.Line.Kill();
        await running;
    }

    [AvaloniaFact]
    public async Task A_command_that_cannot_start_is_reported()
    {
        await using var f = await StartAsync();
        f.Runner.StartFailure = new System.ComponentModel.Win32Exception(2, "No such file");
        f.Line.Text = "ls";

        await f.Line.ExecuteAsync();

        Assert.False(f.Line.IsRunning);
        Assert.True(f.Line.IsOutputVisible);
        Assert.Contains(Loc.Format("CommandLine_StartFailed", "No such file"), OutputLines(f.Line));
        Assert.Equal("ls", f.Line.Text);
    }

    [AvaloniaFact]
    public async Task Dispose_kills_a_running_command()
    {
        await using var f = await StartAsync();
        var running = await StartCommandAsync(f, "sleep 100");

        f.Line.Dispose();
        await running;

        Assert.True(f.Runner.Last.IsDisposed);
        Assert.True(f.Runner.Last.HasExited);
    }

    [AvaloniaFact]
    public async Task Clear_and_close_the_output()
    {
        await using var f = await StartAsync();
        f.Line.Text = "cd -";
        await f.Line.ExecuteAsync();
        Assert.True(f.Line.IsOutputVisible);

        f.Line.ClearOutputCommand.Execute(null);
        Assert.Equal("", f.Line.Output);
        Assert.True(f.Line.IsOutputVisible);

        f.Line.CloseOutputCommand.Execute(null);
        Assert.False(f.Line.IsOutputVisible);
    }

    // ---- History and input ------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task History_records_and_dedupes_consecutive()
    {
        await using var f = await StartAsync();
        foreach (var text in new[] { "cd c", "cd ..", "cd ..", "cd b", "cd c" })
        {
            f.Line.Text = text;
            await f.Line.ExecuteAsync();
        }

        Assert.Equal(["cd c", "cd ..", "cd b", "cd c"], f.Line.History);

        f.Line.Text = "draft";
        f.Line.HistoryPrevious();
        Assert.Equal("cd c", f.Line.Text);
        f.Line.HistoryPrevious();
        Assert.Equal("cd b", f.Line.Text);
        f.Line.HistoryPrevious();
        f.Line.HistoryPrevious();
        Assert.Equal("cd c", f.Line.Text);
        f.Line.HistoryPrevious(); // the oldest stays
        Assert.Equal("cd c", f.Line.Text);
        f.Line.HistoryNext();
        Assert.Equal("cd ..", f.Line.Text);
        f.Line.HistoryNext();
        f.Line.HistoryNext();
        Assert.Equal("cd c", f.Line.Text);
        f.Line.HistoryNext(); // past the newest: the text being typed comes back
        Assert.Equal("draft", f.Line.Text);
        f.Line.HistoryNext();
        Assert.Equal("draft", f.Line.Text);
    }

    [AvaloniaFact]
    public async Task History_keeps_the_last_100()
    {
        await using var f = await StartAsync();
        for (var i = 0; i < 120; i++)
        {
            f.Line.Text = $"cd -{i}"; // a folder that does not exist: nothing is run
            await f.Line.ExecuteAsync();
        }

        Assert.Equal(CommandLineViewModel.MaxHistory, f.Line.History.Count);
        Assert.Equal("cd -20", f.Line.History[0]);
        Assert.Equal("cd -119", f.Line.History[^1]);
    }

    [AvaloniaFact]
    public async Task Typing_after_walking_the_history_starts_from_the_newest_again()
    {
        await using var f = await StartAsync();
        foreach (var text in new[] { "cd c", "cd .." })
        {
            f.Line.Text = text;
            await f.Line.ExecuteAsync();
        }

        f.Line.HistoryPrevious();
        f.Line.HistoryPrevious();
        Assert.Equal("cd c", f.Line.Text);
        f.Line.Text = "cd c/x";
        f.Line.HistoryPrevious();
        Assert.Equal("cd ..", f.Line.Text);
        f.Line.HistoryNext();
        Assert.Equal("cd c/x", f.Line.Text);
    }

    [AvaloniaTheory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public async Task Empty_input_does_nothing(string text)
    {
        await using var f = await StartAsync();
        f.Line.Text = text;

        await f.Line.ExecuteAsync();

        Assert.Empty(f.Runner.Started);
        Assert.Empty(f.Line.History);
        Assert.Equal("", f.Line.Output);
        Assert.False(f.Line.IsOutputVisible);
        Assert.Equal(VfsPath.Local("/a/b"), f.Panel.CurrentPath);
    }

    // ---- Availability -----------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task Prompt_and_availability_follow_the_active_panel()
    {
        await using var f = await StartAsync();
        Assert.True(f.Line.IsAvailable);
        Assert.Equal("/a/b", f.Line.Prompt);
        Assert.Null(f.Line.Placeholder);

        f.App.Main.Workspace.SwitchPanel();
        Assert.Equal(TestApp.Home, f.Line.Prompt);

        await f.App.Right.NavigateToAsync(new VfsPath(Remote, "/shared"));
        Assert.False(f.Line.IsAvailable);
        Assert.Equal(Loc.Get("CommandLine_Unavailable"), f.Line.Placeholder);

        f.Line.Text = "ls";
        await f.Line.ExecuteAsync();
        Assert.Empty(f.Runner.Started);

        f.App.Main.Workspace.SwitchPanel();
        Assert.True(f.Line.IsAvailable);
        await f.Panel.NavigateToAsync(VfsPath.Local("/x"));
        Assert.Equal("/x", f.Line.Prompt);
    }

    /// <summary>Hands the command line's spec to the real runner as a non-login shell (<c>-c</c> for <c>-lc</c>),
    /// so the test does not run the developer's profile; the production spec itself is asserted with the fake.</summary>
    private sealed class NonLoginRunner(IProcessRunner inner) : IProcessRunner
    {
        public List<ProcessSpec> Received { get; } = [];

        public IRunningProcess Start(ProcessSpec spec)
        {
            Received.Add(spec);
            return inner.Start(spec with { Arguments = ["-c", .. spec.Arguments.Skip(1)] });
        }
    }

    /// <summary>The one test with a real process: the spec's shape works end to end with the real runner.</summary>
    [AvaloniaFact]
    public async Task Real_zsh_runs_the_command_in_the_panel_folder()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "zsh and Unix cd; CdCommandParserWindowsTests covers Windows.");
        var folder = Directory.CreateTempSubdirectory("tandem-cmdline-").FullName;
        try
        {
            await using var f = await StartAsync();
            f.App.Fs.AddDirectory(folder); // the in-memory "local disk" shows the same path as the real one
            await f.Panel.NavigateToAsync(VfsPath.Local(folder));
            var runner = new NonLoginRunner(new Tandem.Platform.Mac.ProcessRunner());
            using var line = new CommandLineViewModel(f.App.Main.Workspace, runner, TestApp.Home);
            line.Text = "echo hi; echo made > made.txt";

            await line.ExecuteAsync().WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Equal(["-lc", "echo hi; echo made > made.txt"], Assert.Single(runner.Received).Arguments);
            Assert.Equal([$"{folder}> echo hi; echo made > made.txt", "hi", Loc.Format("Output_ExitCode", 0)],
                OutputLines(line));
            Assert.Equal("made\n", await File.ReadAllTextAsync(Path.Combine(folder, "made.txt")));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task The_main_model_has_a_command_line_over_its_services()
    {
        await using var f = await StartAsync();
        Assert.NotNull(f.App.Main.CommandLine);
        Assert.Equal("/a/b", f.App.Main.CommandLine.Prompt);
    }
}
