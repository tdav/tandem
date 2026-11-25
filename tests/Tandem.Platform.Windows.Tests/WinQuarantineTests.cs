using System.Runtime.Versioning;

namespace Tandem.Platform.Windows.Tests;

[SupportedOSPlatform("windows")]
public sealed class WinQuarantineTests : IDisposable
{
    private readonly TempDir _temp = new();
    private readonly WinQuarantine _quarantine = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void A_mark_round_trips_through_the_zone_identifier_stream()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var file = _temp.Combine("download.exe");
        File.WriteAllText(file, "content");
        var mark = "[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=https://example.com/\r\n"u8.ToArray();

        _quarantine.Set(file, mark);

        Assert.Equal(mark, _quarantine.Get(file));
        Assert.Equal(mark, File.ReadAllBytes(file + ":Zone.Identifier"));
        Assert.Equal("content", File.ReadAllText(file));
    }

    [Fact]
    public void A_shorter_mark_replaces_a_longer_one()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var file = _temp.Combine("file.txt");
        File.WriteAllText(file, "content");

        _quarantine.Set(file, "[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=https://example.com/long\r\n"u8.ToArray());
        _quarantine.Set(file, "[ZoneTransfer]\r\nZoneId=2\r\n"u8.ToArray());

        Assert.Equal("[ZoneTransfer]\r\nZoneId=2\r\n"u8.ToArray(), _quarantine.Get(file));
    }

    [Fact]
    public void An_unmarked_item_has_no_mark()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var file = _temp.Combine("file.txt");
        File.WriteAllText(file, "content");

        Assert.Null(_quarantine.Get(file));
        Assert.Null(_quarantine.Get(_temp.Path));
    }

    [Fact]
    public void A_missing_item_is_FileNotFound()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");

        Assert.Throws<FileNotFoundException>(() => _quarantine.Get(_temp.Combine("missing")));
        Assert.Throws<FileNotFoundException>(() => _quarantine.Set(_temp.Combine("missing"), [1]));
        Assert.False(Path.Exists(_temp.Combine("missing")));
    }

    [Fact]
    public void A_relative_path_is_refused()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");

        Assert.Throws<ArgumentException>(() => _quarantine.Get("file.txt"));
        Assert.Throws<ArgumentException>(() => _quarantine.Set("file.txt", [1]));
    }
}
