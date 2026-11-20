using System.Diagnostics;
using System.Runtime.Versioning;

namespace Tandem.Platform.Mac.Tests;

/// <summary>A 2 MB disk image (HFS+ unless another file system is named), attached (hidden from the Finder) at a mount
/// point inside the test's temp directory and detached again on dispose — a volume that fills up quickly, or one with
/// rules the temp volume lacks (letter case, say). The image file lives in the test's temp directory.</summary>
[SupportedOSPlatform("macos")]
public sealed class TinyVolume : IDisposable
{
    private TinyVolume(string mountPoint) => MountPoint = mountPoint;

    public string MountPoint { get; }

    public static TinyVolume Create(string imagePath, string mountPoint, string fileSystem = "HFS+")
    {
        Directory.CreateDirectory(mountPoint);
        HdiUtil("create", "-size", "2m", "-fs", fileSystem, "-volname", "TandemTestTiny", "-layout", "NONE", imagePath);
        HdiUtil("attach", imagePath, "-nobrowse", "-noautoopen", "-mountpoint", mountPoint);
        return new TinyVolume(mountPoint);
    }

    public void Dispose() => HdiUtil("detach", MountPoint, "-force");

    private static void HdiUtil(params string[] arguments)
    {
        var start = new ProcessStartInfo("/usr/bin/hdiutil", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(start) ?? throw new InvalidOperationException("hdiutil did not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"hdiutil {string.Join(' ', arguments)} failed ({process.ExitCode}): {error}{output.Result}");
    }
}
