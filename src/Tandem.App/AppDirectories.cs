namespace Tandem.App;

/// <summary>The app's own folders. macOS: <c>~/Library/Application Support/Tandem</c> (state, settings,
/// connections), <c>~/Library/Logs/Tandem</c>, <c>~/Library/Caches/Tandem/tmp</c>. Windows:
/// <c>%APPDATA%\Tandem</c>, <c>%LOCALAPPDATA%\Tandem\Logs</c>, <c>%LOCALAPPDATA%\Tandem\Temp</c>.</summary>
public static class AppDirectories
{
    /// <summary>Saved state, settings, connections, trusted keys and the credential vault.</summary>
    public static string State(string homeDirectory) => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Tandem")
        : Path.Combine(homeDirectory, "Library", "Application Support", "Tandem");

    /// <summary>Where unexpected errors are written.</summary>
    public static string LogFile(string homeDirectory) => OperatingSystem.IsWindows()
        ? Path.Combine(LocalAppData(), "Tandem", "Logs", "tandem.log")
        : Path.Combine(homeDirectory, "Library", "Logs", "Tandem", "tandem.log");

    /// <summary>The temporary copies of files opened from archives and servers (emptied at start and exit).</summary>
    public static string Temp(string homeDirectory) => OperatingSystem.IsWindows()
        ? Path.Combine(LocalAppData(), "Tandem", "Temp")
        : Core.Vfs.TempFileStore.DefaultDirectory(homeDirectory);

    private static string LocalAppData() => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
}
