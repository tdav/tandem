using System.Runtime.Versioning;
using Tandem.Core.Platform;

namespace Tandem.Platform.Mac.Tests;

/// <summary>Starts only harmless command-line tools (<c>/bin/echo</c>, <c>/bin/sh</c>, <c>/bin/sleep</c>, <c>/bin/pwd</c>).</summary>
[SupportedOSPlatform("macos")]
public sealed class ProcessRunnerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly ProcessRunner _runner = new();

    [Fact]
    public async Task ProcessRunner_streams_output_and_exit_code()
    {
        await using var process = _runner.Start(new ProcessSpec("/bin/echo", ["hello"]));

        Assert.Equal(["hello"], await ReadAll(process));
        Assert.Equal(0, await process.ExitCode.WaitAsync(Timeout, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_command_line_is_passed_as_the_whole_argument_string()
    {
        await using var process = _runner.Start(new ProcessSpec("/bin/echo", ["ignored"]) { CommandLine = "a \"b  c\"" });

        Assert.Equal(["a b  c"], await ReadAll(process));
    }

    [Fact]
    public async Task Arguments_are_passed_verbatim_without_a_shell()
    {
        await using var process = _runner.Start(new ProcessSpec("/bin/echo", ["a b \"c\"", "$HOME", "*"]));

        Assert.Equal(["a b \"c\" $HOME *"], await ReadAll(process));
    }

    [Fact]
    public async Task Stdout_and_stderr_are_merged_and_exit_code_is_reported()
    {
        await using var process = _runner.Start(new ProcessSpec("/bin/sh",
            ["-c", "echo out; sleep 0.2; echo err 1>&2; sleep 0.2; echo out2; exit 3"]));

        Assert.Equal(["out", "err", "out2"], await ReadAll(process));
        Assert.Equal(3, await process.ExitCode.WaitAsync(Timeout, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Working_directory_is_used()
    {
        using var temp = new TempDir();
        await using var process = _runner.Start(new ProcessSpec("/bin/pwd", [], temp.Path));

        var line = Assert.Single(await ReadAll(process));
        Assert.EndsWith(Path.GetFileName(temp.Path), line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Kill_ends_the_process_and_is_safe_twice_and_after_exit()
    {
        await using var process = _runner.Start(new ProcessSpec("/bin/sleep", ["30"]));

        process.Kill();
        process.Kill();

        await process.ExitCode.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.Empty(await ReadAll(process));
        process.Kill();
    }

    [Fact]
    public async Task Dispose_kills_a_running_process()
    {
        var process = _runner.Start(new ProcessSpec("/bin/sleep", ["30"]));

        await process.DisposeAsync().AsTask().WaitAsync(Timeout, TestContext.Current.CancellationToken);

        Assert.True(process.ExitCode.IsCompleted);
        process.Kill();
        await process.DisposeAsync();
    }

    [Fact]
    public void Missing_executable_throws_from_Start()
    {
        Assert.ThrowsAny<Exception>(() => _runner.Start(new ProcessSpec("/nonexistent/tandem-no-such-tool", [])));
    }

    private static async Task<List<string>> ReadAll(IRunningProcess process)
    {
        var lines = new List<string>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(Timeout);
        await foreach (var line in process.OutputLines.WithCancellation(cts.Token)) lines.Add(line);
        return lines;
    }
}
