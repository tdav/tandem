namespace Tandem.Core.Platform;

/// <summary>A program to run: the executable and its arguments, passed as a list and never through a shell.
/// <see cref="CommandLine"/>, when set, is the whole argument string as it is, instead of <see cref="Arguments"/>:
/// for cmd.exe, which reads its command line itself and does not understand the escaping of an argument
/// list.</summary>
public sealed record ProcessSpec(string FileName, IReadOnlyList<string> Arguments, string? WorkingDirectory = null)
{
    public string? CommandLine { get; init; }
}

/// <summary>A started process. Disposing it kills the process if it is still running.</summary>
public interface IRunningProcess : IAsyncDisposable
{
    /// <summary>The lines the process writes to standard output and standard error, interleaved in the order they
    /// arrive; the sequence ends when the process has exited and both streams are drained. Enumerate it once.</summary>
    IAsyncEnumerable<string> OutputLines { get; }

    /// <summary>Completes with the exit code when the process exits.</summary>
    Task<int> ExitCode { get; }

    /// <summary>Kills the process and its descendants; does nothing once it has exited. Safe to call repeatedly.</summary>
    void Kill();
}

/// <summary>Starts processes.</summary>
public interface IProcessRunner
{
    /// <summary>Starts the process described by <paramref name="spec"/>; throws when it cannot be started (for
    /// example, when the executable does not exist).</summary>
    IRunningProcess Start(ProcessSpec spec);
}
