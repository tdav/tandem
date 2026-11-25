using System.ComponentModel;
using System.Diagnostics;
using Tandem.Core.Platform;

namespace Tandem.Platform.Windows;

/// <summary>
/// Hands items to the Windows shell with <see cref="Process.Start(ProcessStartInfo)"/>: the default application
/// through ShellExecute, Notepad as the text editor, Explorer to reveal an item, Windows Terminal (<c>wt.exe -d</c>)
/// when it is on the PATH or else <c>cmd.exe</c> to open a terminal. ShellExecute gives its verdict when it is
/// called, so a launch that fails (no application, an item that is gone) throws an <see cref="IOException"/> at once
/// and a launch that returns has succeeded. There is no list of applications for an item (<see cref="GetAppsFor"/>
/// is empty) and no Full Disk Access to grant.
/// </summary>
public sealed class WinShellIntegration : IShellIntegration
{
    private readonly Action<ProcessStartInfo> _start;
    private readonly Func<string, string?> _findOnPath;

    public WinShellIntegration() : this(StartProcess, FindOnPath) { }

    /// <summary>For tests: another way to start a process, and to find an executable on the PATH.</summary>
    internal WinShellIntegration(Action<ProcessStartInfo> start, Func<string, string?> findOnPath)
    {
        _start = start ?? throw new ArgumentNullException(nameof(start));
        _findOnPath = findOnPath ?? throw new ArgumentNullException(nameof(findOnPath));
    }

    public Task OpenDefault(string path)
    {
        WinPath.RequireAbsolute(path, nameof(path));
        return Launch(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    public Task OpenInTextEditor(string path)
    {
        WinPath.RequireAbsolute(path, nameof(path));
        return Launch(new ProcessStartInfo("notepad.exe") { ArgumentList = { path }, UseShellExecute = true });
    }

    public IReadOnlyList<AppInfo> GetAppsFor(string path)
    {
        WinPath.RequireAbsolute(path, nameof(path));
        return [];
    }

    public Task OpenWith(string path, AppInfo app)
    {
        WinPath.RequireAbsolute(path, nameof(path));
        ArgumentNullException.ThrowIfNull(app);
        WinPath.RequireAbsolute(app.BundlePath, nameof(app));
        return Launch(new ProcessStartInfo(app.BundlePath) { ArgumentList = { path }, UseShellExecute = true });
    }

    public Task RevealInFinder(string path)
    {
        WinPath.RequireAbsolute(path, nameof(path));
        // Explorer reads its own command line, not argv: the switch and the quoted path are one argument.
        return Launch(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
    }

    public Task OpenTerminal(string directory)
    {
        WinPath.RequireAbsolute(directory, nameof(directory));
        // Shell-executed, so a console program gets a console window of its own.
        return Launch(_findOnPath("wt.exe") is { } terminal
            ? new ProcessStartInfo(terminal) { ArgumentList = { "-d", directory }, UseShellExecute = true }
            : new ProcessStartInfo("cmd.exe") { WorkingDirectory = directory, UseShellExecute = true });
    }

    public Task OpenFullDiskAccessSettings() => Task.CompletedTask;

    /// <exception cref="IOException">The program could not be started (its message is the system's).</exception>
    private Task Launch(ProcessStartInfo info)
    {
        try
        {
            _start(info);
        }
        catch (Win32Exception e)
        {
            throw new IOException(e.Message, e);
        }
        return Task.CompletedTask;
    }

    private static void StartProcess(ProcessStartInfo info) => Process.Start(info)?.Dispose();

    /// <summary>The full path of <paramref name="fileName"/> in a directory on the PATH (an app execution alias
    /// such as <c>wt.exe</c> in WindowsApps counts), or null.</summary>
    private static string? FindOnPath(string fileName)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(Environment.ExpandEnvironmentVariables(directory.Trim('"')), fileName);
                if (Path.IsPathFullyQualified(candidate) && File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry.
            }
        }
        return null;
    }
}
