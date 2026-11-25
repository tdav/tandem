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
