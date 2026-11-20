using Tandem.App.Services;
using Tandem.App.Tests.ViewModels;
using Tandem.Core.Platform;

namespace Tandem.App.Tests.Services;

public class ProgramLauncherTests
{
    [Fact]
    public void App_bundle_is_started_through_open()
    {
        var spec = ProgramLauncher.Build("/Applications/X.app", ["/cur/a"], "/cur", "/Users/me");

        Assert.Equal("/usr/bin/open", spec.FileName);
        Assert.Equal(["-a", "/Applications/X.app", "/cur/a"], spec.Arguments);
        Assert.Equal("/cur", spec.WorkingDirectory);
    }

    [Theory]
    [InlineData("/Applications/Visual Studio Code.APP")]
    [InlineData("/Applications/X.app/")]
    public void App_bundle_is_recognised_whatever_the_case_or_a_trailing_slash(string program) =>
        Assert.Equal("/usr/bin/open", ProgramLauncher.Build(program, [], "/cur", "/Users/me").FileName);

    [Fact]
    public void Plain_program_gets_arguments_and_working_directory()
    {
        var spec = ProgramLauncher.Build("/usr/local/bin/tool", ["-x", "a b", "\"q\""], "/my folder", "/Users/me");

        Assert.Equal("/usr/local/bin/tool", spec.FileName);
        Assert.Equal(["-x", "a b", "\"q\""], spec.Arguments);
        Assert.Equal("/my folder", spec.WorkingDirectory);
    }

    [Theory]
    [InlineData("~/bin/tool", "/Users/me/bin/tool")]
    [InlineData("~", "/Users/me")]
    [InlineData("~/Applications/Ed.app", "/usr/bin/open")]
    [InlineData("/opt/~tool", "/opt/~tool")]
    [InlineData("~other/tool", "~other/tool")]
    public void Tilde_expands_to_home(string program, string fileName)
    {
        var spec = ProgramLauncher.Build(program, [], "/cur", "/Users/me");

        Assert.Equal(fileName, spec.FileName);
        if (fileName == "/usr/bin/open") Assert.Equal(["-a", "/Users/me/Applications/Ed.app"], spec.Arguments);
    }

    [Fact]
    public void Blank_program_is_refused() =>
        Assert.Throws<ArgumentException>(() => ProgramLauncher.Build(" ", [], "/cur", "/Users/me"));

    [Fact]
    public async Task Started_program_output_is_drained_and_the_process_released_after_it_exits_without_a_kill()
    {
        var runner = new FakeProcessRunner();
        var spec = new ProcessSpec("/usr/bin/true", ["x"], "/cur");

        var verdict = ProgramLauncher.Start(runner, spec);

        Assert.Equal([spec], runner.Started);
        var process = runner.Last;
        process.Write("some", "output");
        Assert.False(process.IsDisposed);
        process.Exit(3);
        await WaitUntil(() => process.IsDisposed);
        Assert.Equal(0, process.Kills);
        Assert.Equal(3, await process.ExitCode);
        Assert.Null(await verdict.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)); // a plain program's exit code is not looked at
    }

    [Theory]
    [InlineData(1, "Unable to find application named 'Missing'")]
    [InlineData(0, null)]
    public async Task Application_that_open_cannot_launch_gives_its_output_as_the_verdict(int code, string? verdict)
    {
        var runner = new FakeProcessRunner();
        var launch = ProgramLauncher.Start(runner,
            ProgramLauncher.Build("/Applications/Missing.app", ["/cur/a"], "/cur", "/Users/me"));

        runner.Last.Write("Unable to find application named 'Missing'");
        runner.Last.Exit(code);

        Assert.Equal(verdict, await launch.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        await WaitUntil(() => runner.Last.IsDisposed);
        Assert.Equal(0, runner.Last.Kills);
    }

    [Fact]
    public async Task Open_failing_without_output_still_gives_a_verdict()
    {
        var runner = new FakeProcessRunner();
        var launch = ProgramLauncher.Start(runner, ProgramLauncher.Build("X.app", [], "/cur", "/Users/me"));

        runner.Last.Exit(1);

        Assert.Equal("", await launch.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Failure_to_start_is_thrown_to_the_caller()
    {
        var runner = new FakeProcessRunner { StartFailure = new System.ComponentModel.Win32Exception("No such file") };

        var error = Assert.Throws<System.ComponentModel.Win32Exception>(() =>
        {
            _ = ProgramLauncher.Start(runner, new ProcessSpec("/nowhere", []));
        });
        Assert.Equal("No such file", error.Message);
    }

    [Fact]
    public async Task Real_program_starts_without_a_shell_and_takes_arguments_verbatim()
    {
        // A harmless real run: /bin/echo prints its arguments; a shell would have split or expanded them.
        var runner = new Tandem.Platform.Mac.ProcessRunner();
        string[] arguments = ["a b", "$HOME", "\"q\"", "*", "; ls"];
        await using var process = runner.Start(ProgramLauncher.Build("/bin/echo", arguments, "/", "/Users/me"));
        var lines = new List<string>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await foreach (var line in process.OutputLines.WithCancellation(timeout.Token)) lines.Add(line);

        Assert.Equal([string.Join(' ', arguments)], lines);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.ElapsedMilliseconds > 5000) throw new TimeoutException("The condition was not met in time.");
            await Task.Delay(10);
        }
    }
}
