using Tandem.Core.Settings;

namespace Tandem.Core.Platform;

/// <summary>An application that can open files: its display name and the path of its bundle.</summary>
public sealed record AppInfo(string Name, string BundlePath);

/// <summary>
/// Hands items to the rest of the system: the default application, a chosen one, the file browser, the terminal.
/// Methods that launch something return once the launcher is started and never wait for the launched program. The
/// task they return is the launcher's verdict, which comes a moment later: it completes when the launcher has handed
/// the item on, and faults with an <see cref="IOException"/> (its message the launcher's own words) when the launcher
/// reports failure — no application for the item, an item that is gone, a broken application.
/// <para>Every path (items, directories, application bundles) must be absolute; implementations throw
/// <see cref="ArgumentException"/> for a relative path.</para>
/// </summary>
public interface IShellIntegration
{
    /// <summary>Opens the item with its default application.</summary>
    /// <exception cref="IOException">The launcher could not be started.</exception>
    Task OpenDefault(string path);

    /// <summary>Opens the file in the default text editor.</summary>
    /// <exception cref="IOException">The launcher could not be started.</exception>
    Task OpenInTextEditor(string path);

    /// <summary>The applications that can open the item, de-duplicated and sorted by name; empty when there are none
    /// or the item does not exist.</summary>
    IReadOnlyList<AppInfo> GetAppsFor(string path);

    /// <summary>Opens the item with <paramref name="app"/>.</summary>
    /// <exception cref="IOException">The launcher could not be started.</exception>
    Task OpenWith(string path, AppInfo app);

    /// <summary>Shows the item selected in the system file browser.</summary>
    /// <exception cref="IOException">The launcher could not be started.</exception>
    Task RevealInFinder(string path);

    /// <summary>Opens a terminal window in <paramref name="directory"/>: the one <paramref name="terminal"/> names,
    /// or the platform's usual one when the platform does not have it.</summary>
    /// <exception cref="IOException">The launcher could not be started.</exception>
    Task OpenTerminal(string directory, TerminalChoice terminal);

    /// <summary>Opens the system settings page where the user grants Full Disk Access.</summary>
    /// <exception cref="IOException">The launcher could not be started.</exception>
    Task OpenFullDiskAccessSettings();
}
