using System.Runtime.Versioning;
using Avalonia.Headless.XUnit;
using Tandem.App.Services;
using Tandem.App.Views;
using Tandem.Core.Vfs;
using Tandem.Core.Vfs.Local;

namespace Tandem.App.Tests.Integration;

/// <summary>The real Windows services start the app on a real home folder and show the main window. Runs only on
/// Windows (the CI runner); skipped elsewhere.</summary>
public sealed class WindowsStartTests
{
    [AvaloniaFact]
    public async Task The_app_starts_on_the_home_folder_and_lists_drives_at_the_root()
    {
        if (OperatingSystem.IsWindows()) await RunAsync();
        else Assert.Skip("Windows only");
    }

    [SupportedOSPlatform("windows")]
    private static async Task RunAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var home = Directory.CreateTempSubdirectory("tandem-win-start-");
        try
        {
            File.WriteAllText(Path.Combine(home.FullName, "hello.txt"), "hi");
            using var services = Composition.CreateWindowsServices(
                new ErrorLog(Path.Combine(home.FullName, "test.log")), home.FullName);
            var state = Path.Combine(home.FullName, "state");
            using var main = await Composition.StartAsync(services, state, home.FullName);

            var panel = main.Workspace.ActivePanel;
            Assert.Equal(VfsPath.Local(home.FullName), panel.CurrentPath);
            Assert.StartsWith("/" + home.FullName[0] + ":/", panel.CurrentPath.Path);
            Assert.Equal(home.FullName.TrimEnd('\\'), services.Providers.DisplayPath(panel.CurrentPath));

            var local = services.Providers.For(VfsPath.Local("/"));
            var drives = new List<string>();
            await foreach (var entry in local.EnumerateAsync(VfsPath.Local("/"), ct)) drives.Add(entry.Name);
            Assert.Contains(Path.GetPathRoot(Environment.SystemDirectory)!.TrimEnd('\\'), drives);

            var files = new List<string>();
            await foreach (var entry in local.EnumerateAsync(panel.CurrentPath, ct)) files.Add(entry.Name);
            Assert.Contains("hello.txt", files);

            Assert.NotEmpty(services.Volumes.GetVolumes());
            var window = new MainWindow(main, screens: null);
            window.Show();
            window.Close();
        }
        finally
        {
            home.Delete(recursive: true);
        }
    }
}
