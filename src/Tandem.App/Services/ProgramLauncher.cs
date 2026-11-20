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
    /// Starts <paramref name="spec"/> and lets it run on its own: its output is read and dropped (so it never blocks
    /// on a full pipe), and the process is released, not killed, once it has exited. Its exit code is not looked
    /// at.
    /// </summary>
    /// <exception cref="Exception">Whatever <see cref="IProcessRunner.Start"/> throws when the program cannot be
    /// started.</exception>
    public static void Start(IProcessRunner runner, ProcessSpec spec)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(spec);
        var process = runner.Start(spec);
        _ = Task.Run(() => LetRunAsync(process));
    }

    /// <summary>Whether <paramref name="error"/>, thrown by <see cref="Start"/>, means the program could not be
    /// started (missing, not executable, a bad working folder): something to tell the user, not a bug.</summary>
    public static bool IsStartFailure(Exception error) =>
        error is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException;

    /// <summary>Reads the output away while the process runs and releases it once it has exited (disposing it then
    /// kills nothing: a program it left running in the background keeps running).</summary>
    private static async Task LetRunAsync(IRunningProcess process)
    {
        var drained = DrainAsync(process);
        try
        {
            await process.ExitCode;
        }
        catch (Exception)
        {
            // how the program ends is its own business
        }
        try
        {
            await process.DisposeAsync();
            await drained;
        }
        catch (Exception)
        {
            // already let go
        }
    }

    private static async Task DrainAsync(IRunningProcess process)
    {
        try
        {
            await foreach (var _ in process.OutputLines)
            {
                // dropped
            }
        }
        catch (Exception)
        {
            // the output closed with the process
        }
    }
}
