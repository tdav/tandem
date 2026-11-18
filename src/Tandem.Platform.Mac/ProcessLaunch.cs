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
