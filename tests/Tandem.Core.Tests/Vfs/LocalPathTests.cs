using Tandem.Core.Vfs;
using Tandem.Core.Vfs.Local;

namespace Tandem.Core.Tests.Vfs;

public class LocalPathTests
{
    private const LocalPathStyle Win = LocalPathStyle.Windows;
    private const LocalPathStyle Unix = LocalPathStyle.Unix;

    [Theory]
    [InlineData(@"C:\Users\me", "/C:/Users/me")]
    [InlineData(@"c:\Users\me\", "/C:/Users/me/")]
    [InlineData("c:/Users/me", "/C:/Users/me")]
    [InlineData(@"C:\", "/C:/")]
    [InlineData("D:", "/D:")]
    [InlineData(@"\\srv\share\dir", "/UNC/srv/share/dir")]
    [InlineData(@"\\?\C:\long\path", "/C:/long/path")]
    [InlineData(@"\\?\UNC\srv\share", "/UNC/srv/share")]
    [InlineData("/C:/already", "/C:/already")]
    [InlineData("/c:/lower", "/C:/lower")]
    [InlineData("/", "/")]
    public void FromOs_maps_windows_paths(string os, string expected) =>
        Assert.Equal(expected, LocalPath.FromOs(os, Win));

    [Theory]
    [InlineData("C:foo")]
    [InlineData(@"Users\me")]
    [InlineData("")]
    public void FromOs_rejects_relative_windows_paths(string os) =>
        Assert.Throws<ArgumentException>(() => LocalPath.FromOs(os, Win));

    [Theory]
    [InlineData("/C:", @"C:\")]
    [InlineData("/C:/Users/me", @"C:\Users\me")]
    [InlineData("/UNC/srv/share", @"\\srv\share")]
    [InlineData("/UNC/srv/share/a b", @"\\srv\share\a b")]
    public void ToOs_maps_to_windows_paths(string vfs, string expected) =>
        Assert.Equal(expected, LocalPath.ToOs(vfs, Win));

    [Theory]
    [InlineData("/")]
    [InlineData("/UNC")]
    [InlineData("/UNC/srv")]
    [InlineData("/notadrive/x")]
    public void ToOs_refuses_paths_without_a_windows_counterpart(string vfs) =>
        Assert.Throws<VfsException>(() => LocalPath.ToOs(vfs, Win));

    [Theory]
    [InlineData("/Users/me")]
    [InlineData("/")]
    [InlineData(@"/odd\name")]
    public void Unix_style_is_the_identity(string path)
    {
        Assert.Equal(path, LocalPath.FromOs(path, Unix));
        Assert.Equal(path, LocalPath.ToOs(path, Unix));
    }

    [Fact]
    public void Unix_style_leaves_windows_looking_strings_alone() =>
        Assert.Equal(@"C:\x", LocalPath.FromOs(@"C:\x", Unix));

    [Theory]
    [InlineData("/C:", true)]
    [InlineData("/z:", true)]
    [InlineData("/C:/x", false)]
    [InlineData("/CC:", false)]
    [InlineData("/", false)]
    public void IsDriveRoot(string vfs, bool expected) => Assert.Equal(expected, LocalPath.IsDriveRoot(vfs));

    [Theory]
    [InlineData(@"C:\Users\me")]
    [InlineData(@"\\srv\share\dir\file.txt")]
    public void Round_trips(string os) =>
        Assert.Equal(os, LocalPath.ToOs(VfsPath.Local(LocalPath.FromOs(os, Win)).Path, Win));

    [Fact]
    public void Current_matches_the_running_os() =>
        Assert.Equal(OperatingSystem.IsWindows() ? Win : Unix, LocalPath.Current);
}

public class LocalPathTypedTests
{
    private const LocalPathStyle Win = LocalPathStyle.Windows;
    private const LocalPathStyle Unix = LocalPathStyle.Unix;

    [Theory]
    [InlineData("~", @"C:\Users\me")]
    [InlineData(@"~\Docs", @"C:\Users\me\Docs")]
    [InlineData("~/Docs", @"C:\Users\me\Docs")]
    [InlineData(@"D:\x", @"D:\x")]
    [InlineData("d:", "d:")]
    [InlineData(@"\\srv\share", @"\\srv\share")]
    [InlineData("/C:/x", "/C:/x")]
    [InlineData("/", "/")]
    public void ExpandTyped_windows_absolute(string text, string expected) =>
        Assert.Equal(expected, LocalPath.ExpandTyped(text, @"C:\Users\me", Win));

    [Theory]
    [InlineData("sub")]
    [InlineData(@"sub\dir")]
    [InlineData("C:rel")]
    [InlineData("~other")]
    public void ExpandTyped_windows_relative_is_null(string text) =>
        Assert.Null(LocalPath.ExpandTyped(text, @"C:\Users\me", Win));

    [Theory]
    [InlineData("~", "/Users/me")]
    [InlineData("~/Docs", "/Users/me/Docs")]
    [InlineData("/x", "/x")]
    public void ExpandTyped_unix(string text, string expected) =>
        Assert.Equal(expected, LocalPath.ExpandTyped(text, "/Users/me/", Unix));

    [Theory]
    [InlineData(@"~\Docs")]
    [InlineData(@"C:\x")]
    [InlineData("sub")]
    public void ExpandTyped_unix_relative_is_null(string text) =>
        Assert.Null(LocalPath.ExpandTyped(text, "/Users/me", Unix));

    [Theory]
    [InlineData(@"a\b", Win, "a/b")]
    [InlineData(@"a\b", Unix, @"a\b")]
    public void RelativeToVfs(string text, LocalPathStyle style, string expected) =>
        Assert.Equal(expected, LocalPath.RelativeToVfs(text, style));

    [Theory]
    [InlineData("x/", Win, true)]
    [InlineData(@"x\", Win, true)]
    [InlineData(@"x\", Unix, false)]
    [InlineData("x/", Unix, true)]
    public void EndsWithSeparator(string text, LocalPathStyle style, bool expected) =>
        Assert.Equal(expected, LocalPath.EndsWithSeparator(text, style));

    [Theory]
    [InlineData("/", Win, "/")]
    [InlineData("/C:", Win, @"C:\")]
    [InlineData("/C:/Users", Win, @"C:\Users")]
    [InlineData("/Users", Unix, "/Users")]
    public void Display(string vfs, LocalPathStyle style, string expected) =>
        Assert.Equal(expected, LocalPath.Display(vfs, style));
}

public class WindowsNameTests
{
    [Theory]
    [InlineData("report.txt", true)]
    [InlineData("a b", true)]
    [InlineData(".gitignore", true)]
    [InlineData("a:b", false)]
    [InlineData(@"a\b", false)]
    [InlineData("a/b", false)]
    [InlineData("what?", false)]
    [InlineData("star*", false)]
    [InlineData("<x>", false)]
    [InlineData("pipe|", false)]
    [InlineData("quote\"", false)]
    [InlineData("tab\t", false)]
    [InlineData("CON", false)]
    [InlineData("nul.txt", false)]
    [InlineData("COM1", false)]
    [InlineData("lpt9.log", false)]
    [InlineData("CONSOLE", true)]
    [InlineData("trailing.", false)]
    [InlineData("trailing ", false)]
    public void IsWindowsName(string name, bool expected) => Assert.Equal(expected, LocalPath.IsWindowsName(name));
}

public class LinkTargetTests
{
    [Theory]
    [InlineData("/C:/a", @"C:\target", LocalPathStyle.Windows, @"C:\target")]
    [InlineData("/C:/a", @"..\x", LocalPathStyle.Windows, "/C:/a/../x")]
    [InlineData("/C:/a", @"\\srv\share", LocalPathStyle.Windows, @"\\srv\share")]
    [InlineData("/a", "/t", LocalPathStyle.Unix, "/t")]
    [InlineData("/a", "../x", LocalPathStyle.Unix, "/a/../x")]
    [InlineData("/", "x", LocalPathStyle.Unix, "//x")]
    public void JoinLinkTarget(string parent, string target, LocalPathStyle style, string expected) =>
        Assert.Equal(expected, LocalPath.JoinLinkTarget(parent, target, style));
}

public class LocalPathSafetyTests
{
    [Theory]
    [InlineData("/", LocalPathStyle.Windows, true)]
    [InlineData("/C:", LocalPathStyle.Windows, true)]
    [InlineData("/UNC/srv", LocalPathStyle.Windows, true)]
    [InlineData("/UNC/srv/share", LocalPathStyle.Windows, true)]
    [InlineData("/UNC/srv/share/x", LocalPathStyle.Windows, false)]
    [InlineData("/C:/x", LocalPathStyle.Windows, false)]
    [InlineData("/", LocalPathStyle.Unix, true)]
    [InlineData("/C:", LocalPathStyle.Unix, false)]
    public void IsVolumeRoot(string vfs, LocalPathStyle style, bool expected) =>
        Assert.Equal(expected, LocalPath.IsVolumeRoot(vfs, style));

    [Theory]
    [InlineData(@"/C:/Downloads/..\..\Startup\x.bat")]
    [InlineData("/C:/Downloads/a:b")]
    [InlineData(@"/UNC/srv/share/a\b")]
    [InlineData(@"/UNC/s\rv/share/x")]
    public void A_name_holding_a_separator_or_colon_has_no_windows_path(string vfs)
    {
        Assert.Null(LocalPath.TryToOs(vfs, LocalPathStyle.Windows));
        Assert.Throws<VfsException>(() => LocalPath.ToOs(vfs, LocalPathStyle.Windows));
    }

    [Fact]
    public void A_name_holding_a_backslash_is_fine_on_unix() =>
        Assert.Equal(@"/a\b", LocalPath.ToOs(@"/a\b", LocalPathStyle.Unix));
}
