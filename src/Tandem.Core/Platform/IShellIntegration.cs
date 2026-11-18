namespace Tandem.Core.Platform;

/// <summary>An application that can open files: its display name and the path of its bundle.</summary>
public sealed record AppInfo(string Name, string BundlePath);

/// <summary>
/// Hands items to the rest of the system: the default application, a chosen one, the file browser, the terminal.
/// Methods that launch something return once it is launched and never wait for the launched program.
/// <para>Every path (items, directories, application bundles) must be absolute; implementations throw
/// <see cref="ArgumentException"/> for a relative path.</para>
/// </summary>
public interface IShellIntegration
{
    /// <summary>Opens the item with its default application.</summary>
    /// <exception cref="IOException">The launcher could not be started.</exception>
    void OpenDefault(string path);

    /// <summary>Opens the file in the default text editor.</summary>
    /// <exception cref="IOException">The launcher could not be started.</exception>
    void OpenInTextEditor(string path);

    /// <summary>The applications that can open the item, de-duplicated and sorted by name; empty when there are none
    /// or the item does not exist.</summary>
    IReadOnlyList<AppInfo> GetAppsFor(string path);

    /// <summary>Opens the item with <paramref name="app"/>.</summary>
    /// <exception cref="IOException">The launcher could not be started.</exception>
    void OpenWith(string path, AppInfo app);

    /// <summary>Shows the item selected in the system file browser.</summary>
    /// <exception cref="IOException">The launcher could not be started.</exception>
    void RevealInFinder(string path);

    /// <summary>Opens a terminal window in <paramref name="directory"/>.</summary>
    /// <exception cref="IOException">The launcher could not be started.</exception>
    void OpenTerminal(string directory);

    /// <summary>Opens the system settings page where the user grants Full Disk Access.</summary>
    /// <exception cref="IOException">The launcher could not be started.</exception>
    void OpenFullDiskAccessSettings();
}
