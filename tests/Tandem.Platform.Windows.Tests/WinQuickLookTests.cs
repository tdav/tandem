using System.Runtime.Versioning;

namespace Tandem.Platform.Windows.Tests;

[SupportedOSPlatform("windows")]
public sealed class WinQuickLookTests
{
    [Fact]
    public void Toggle_never_opens_a_preview()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var quickLook = new WinQuickLook();

        quickLook.Toggle([@"C:\file.txt"]);
        quickLook.Toggle([]);
        quickLook.Close();

        Assert.False(quickLook.IsOpen);
    }

    [Fact]
    public void A_relative_path_is_refused()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");

        Assert.Throws<ArgumentException>(() => new WinQuickLook().Toggle(["file.txt"]));
    }
}
