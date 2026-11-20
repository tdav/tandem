using System.ComponentModel;
using Tandem.Core.Platform;

namespace Tandem.App.Services;

/// <summary>
/// Starts a program the user named (a button of the button bar, the external editor): directly, with an argument
/// list, never through a shell.
/// </summary>
public static class ProgramLauncher
{
    private const string Open = "/usr/bin/open";

    /// <summary>
    /// How to start <paramref name="program"/> with <paramref name="arguments"/> in
    /// <paramref name="workingDirectory"/>. A leading <c>~</c> (alone or before <c>/</c>) is
    /// <paramref name="homeDirectory"/>. A program whose name ends in ".app" (any case) is an application bundle:
    /// <c>/usr/bin/open -a &lt;program&gt; &lt;arguments…&gt;</c>; any other is run itself with the arguments.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="program"/> is blank.</exception>
    public static ProcessSpec Build(string program, IReadOnlyList<string> arguments, string workingDirectory,
        string homeDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(program);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(homeDirectory);
        if (program == "~") program = homeDirectory;
        else if (program.StartsWith("~/", StringComparison.Ordinal))
            program = Path.Join(homeDirectory, program[2..]);

        return program.TrimEnd('/').EndsWith(".app", StringComparison.OrdinalIgnoreCase)
            ? new ProcessSpec(Open, ["-a", program, .. arguments], workingDirectory)
            : new ProcessSpec(program, arguments.ToArray(), workingDirectory);
    }

    /// <summary>
    /// Starts <paramref name="spec"/> and lets it run on its own: its output is read (so it never blocks on a full
    /// pipe), and the process is released, not killed, once it has exited. The result completes when it has exited:
    /// for an application started through <c>/usr/bin/open</c> that exited with a code other than 0 (the application
    /// is missing, say), with the launcher's output (its lines, possibly none); otherwise with null. The exit code of
    /// any other program is not looked at, and its output is dropped.
    /// </summary>
    /// <exception cref="Exception">Whatever <see cref="IProcessRunner.Start"/> throws when the program cannot be
    /// started (see <see cref="IsStartFailure"/>).</exception>
    public static Task<string?> Start(IProcessRunner runner, ProcessSpec spec)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(spec);
        var process = runner.Start(spec);
        var keepOutput = spec.FileName == Open;
        return Task.Run(() => LetRunAsync(process, keepOutput));
    }

    /// <summary>Whether <paramref name="error"/>, thrown by <see cref="Start"/>, means the program could not be
    /// started (missing, not executable, a bad working folder): something to tell the user, not a bug.</summary>
    public static bool IsStartFailure(Exception error) =>
        error is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException;

    /// <summary>The most lines of the launcher's output kept for its verdict.</summary>
    private const int VerdictLines = 20;

    /// <summary>Reads the output while the process runs and releases it once it has exited (disposing it then
    /// kills nothing: a program it left running in the background keeps running).</summary>
    private static async Task<string?> LetRunAsync(IRunningProcess process, bool keepOutput)
    {
        var lines = new List<string>();
        var drained = DrainAsync(process, keepOutput ? lines : null);
        int? code = null;
        try
        {
            code = await process.ExitCode;
        }
        catch (Exception)
        {
            // how the program ends is its own business
        }
        try
        {
            await drained.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception)
        {
            // a program it started still holds the output: give up on the rest
        }
        try
        {
            await process.DisposeAsync();
        }
        catch (Exception)
        {
            // already let go
        }
        if (!keepOutput || code is null or 0) return null;
        lock (lines) return string.Join('\n', lines);
    }

    private static async Task DrainAsync(IRunningProcess process, List<string>? keep)
    {
        try
        {
            await foreach (var line in process.OutputLines)
            {
                if (keep is null) continue;
                lock (keep)
                    if (keep.Count < VerdictLines)
                        keep.Add(line);
            }
        }
        catch (Exception)
        {
            // the output closed with the process
        }
    }
}
