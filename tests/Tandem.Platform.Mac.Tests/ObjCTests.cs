using System.Runtime.Versioning;
using Tandem.Platform.Mac.Interop;

namespace Tandem.Platform.Mac.Tests;

[SupportedOSPlatform("macos")]
public sealed class ObjCTests
{
    [Fact]
    public void NSString_round_trips_non_ASCII_text()
    {
        using var pool = new ObjC.AutoreleasePool();

        Assert.Equal("Папка/файл ✓.txt", ObjC.ToManagedString(ObjC.ToNSString("Папка/файл ✓.txt")));
    }

    [Fact]
    public void NSArray_round_trips_strings()
    {
        using var pool = new ObjC.AutoreleasePool();

        Assert.Equal(["a", "б", ""], ObjC.ToStringList(ObjC.ToNSArray(["a", "б", ""])));
    }

    [Fact]
    public void File_URL_keeps_the_path()
    {
        using var pool = new ObjC.AutoreleasePool();

        Assert.Equal("/tmp/some dir/файл", ObjC.PathOf(ObjC.FileUrl("/tmp/some dir/файл", isDirectory: false)));
    }
}
