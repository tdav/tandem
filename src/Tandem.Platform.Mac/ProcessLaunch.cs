using Tandem.Core.Platform;

namespace Tandem.Platform.Mac;

/// <summary>Starting a helper program without waiting for it, and letting go of it once it exits.</summary>
internal static class ProcessLaunch
{
    /// <summary>Starts <paramref name="spec"/>; any failure to start becomes an <see cref="IOException"/>.</summary>
    public static IRunningProcess Start(IProcessRunner runner, ProcessSpec spec)
    {
        try
        {
            return runner.Start(spec);
        }
        catch (Exception ex) when (ex is not IOException)
        {
            throw new IOException($"Could not start '{spec.FileName}': {ex.Message}", ex);
        }
    }

    /// <summary>The most lines of a helper's output kept for an error message.</summary>
    private const int MaxOutputLines = 20;

    /// <summary>In the background: drains the process's output (keeping its last lines), waits for it to exit and
    /// disposes it. The task completes when it exited with 0, and faults with an <see cref="IOException"/> otherwise:
    /// its message is what the process wrote or, when it wrote nothing, its exit code.</summary>
    public static async Task ExitedAsync(IRunningProcess process, string fileName)
    {
        var lines = new Queue<string>();
        int exitCode;
        try
        {
            await foreach (var line in process.OutputLines.ConfigureAwait(false))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                lines.Enqueue(line.Trim());
                if (lines.Count > MaxOutputLines) lines.Dequeue();
            }
            exitCode = await process.ExitCode.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not IOException)
        {
            throw new IOException($"The helper did not finish: {ex.Message}", ex);
        }
        finally
        {
            await process.DisposeAsync().ConfigureAwait(false);
        }
        if (exitCode != 0)
            throw new IOException(lines.Count > 0
                ? string.Join("\n", lines)
                : $"'{fileName}' exited with code {exitCode}.");
    }

    /// <summary>In the background: drains the process's output, waits for it to exit, runs
    /// <paramref name="onExited"/>, and disposes it.</summary>
    public static void ReleaseWhenExited(IRunningProcess process, Action? onExited = null) =>
        _ = ReleaseAsync(process, onExited);

    private static async Task ReleaseAsync(IRunningProcess process, Action? onExited)
    {
        try
        {
            await foreach (var _ in process.OutputLines.ConfigureAwait(false))
            {
            }

            await process.ExitCode.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Nobody waits on a launched helper; however it ended, it only has to be let go.
        }
        finally
        {
            onExited?.Invoke();
            await process.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Throws unless <paramref name="path"/> is absolute, so it can never be read as an option.</summary>
    public static void RequireAbsolute(string path, string paramName)
    {
        ArgumentException.ThrowIfNullOrEmpty(path, paramName);
        if (!Path.IsPathFullyQualified(path))
            throw new ArgumentException($"The path '{path}' is not absolute.", paramName);
    }
}
