using System.Runtime.Versioning;
using Tandem.Core.Platform;
using Tandem.Platform.Mac.Interop;

namespace Tandem.Platform.Mac;

/// <summary>
/// Launches through <c>/usr/bin/open</c> (arguments passed as a list, never through a shell) and lists applications
/// with <c>-[NSWorkspace URLsForApplicationsToOpenURL:]</c>. A launch's task faults when <c>open</c> exits with a
/// failure, with what it wrote as the message.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacShellIntegration(IProcessRunner runner) : IShellIntegration
{
    private const string OpenTool = "/usr/bin/open";
    private const string FullDiskAccessPane = "x-apple.systempreferences:com.apple.preference.security?Privacy_AllFiles";

    private readonly IProcessRunner _runner = runner ?? throw new ArgumentNullException(nameof(runner));

    public Task OpenDefault(string path)
    {
        ProcessLaunch.RequireAbsolute(path, nameof(path));
        return Open(path);
    }

    public Task OpenInTextEditor(string path)
    {
        ProcessLaunch.RequireAbsolute(path, nameof(path));
        return Open("-t", path);
    }

    public Task OpenWith(string path, AppInfo app)
    {
        ProcessLaunch.RequireAbsolute(path, nameof(path));
        ArgumentNullException.ThrowIfNull(app);
        ProcessLaunch.RequireAbsolute(app.BundlePath, nameof(app));
        return Open("-a", app.BundlePath, path);
    }

    public Task RevealInFinder(string path)
    {
        ProcessLaunch.RequireAbsolute(path, nameof(path));
        return Open("-R", path);
    }

    public Task OpenTerminal(string directory)
    {
        ProcessLaunch.RequireAbsolute(directory, nameof(directory));
        return Open("-a", "Terminal", directory);
    }

    public Task OpenFullDiskAccessSettings() => Open(FullDiskAccessPane);

    public IReadOnlyList<AppInfo> GetAppsFor(string path)
    {
        ProcessLaunch.RequireAbsolute(path, nameof(path));
        if (!Path.Exists(path)) return [];

        List<string> bundles;
        using (new ObjC.AutoreleasePool())
        {
            ObjC.LoadAppKit();
            var workspace = ObjC.Send(ObjC.Class("NSWorkspace"), ObjC.Selector("sharedWorkspace"));
            var url = ObjC.FileUrl(path, Directory.Exists(path));
            bundles = ObjC.ToPathList(ObjC.Send(workspace, ObjC.Selector("URLsForApplicationsToOpenURL:"), url));
        }

        return
        [
            .. bundles
                .Where(b => b.Length > 0)
                .Select(b => b.TrimEnd('/'))
                .Distinct(StringComparer.Ordinal)
                .Select(b => new AppInfo(AppName(b), b))
                .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(a => a.BundlePath, StringComparer.Ordinal),
        ];
    }

    /// <summary>The bundle's file name without <c>.app</c>.</summary>
    private static string AppName(string bundlePath)
    {
        var name = Path.GetFileName(bundlePath);
        return name.EndsWith(".app", StringComparison.OrdinalIgnoreCase) ? name[..^".app".Length] : name;
    }

    /// <summary>Starts <c>open</c> (an <see cref="IOException"/> at once if it cannot start) and returns its verdict:
    /// it exits with a failure and a line on standard error when there is no application for the item, the item is
    /// gone, or the application is broken.</summary>
    private Task Open(params string[] arguments) =>
        ProcessLaunch.ExitedAsync(ProcessLaunch.Start(_runner, new ProcessSpec(OpenTool, arguments)), OpenTool);
}
