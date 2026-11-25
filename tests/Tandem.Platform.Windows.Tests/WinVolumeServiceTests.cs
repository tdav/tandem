using System.Runtime.Versioning;

namespace Tandem.Platform.Windows.Tests;

[SupportedOSPlatform("windows")]
public sealed class WinVolumeServiceTests
{
    [Fact]
    public void The_system_drive_comes_first()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        using var service = new WinVolumeService();

        var volumes = service.GetVolumes();

        Assert.NotEmpty(volumes);
        var system = Path.GetPathRoot(Environment.SystemDirectory)!;
        Assert.Equal(system, volumes[0].MountPoint, ignoreCase: true);
        Assert.Contains(system.TrimEnd('\\').ToUpperInvariant(), volumes[0].Name);
        Assert.True(volumes[0].TotalBytes > 0);
        Assert.False(volumes[0].IsRemovable);
        Assert.All(volumes, v => Assert.Matches(@"^[A-Z]:\\$", v.MountPoint));
        Assert.Equal(volumes.Skip(1).Select(v => v.MountPoint).Order(StringComparer.OrdinalIgnoreCase),
            volumes.Skip(1).Select(v => v.MountPoint));
    }

    [Theory]
    [InlineData("", "C:")]
    [InlineData("Windows", "Windows (C:)")]
    public void A_drive_is_named_by_its_label_and_letter(string label, string expected)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");

        Assert.Equal(expected, WinVolumeService.DisplayName(label, @"C:\"));
    }

    [Fact]
    public async Task Changed_is_raised_when_the_ready_drives_change_and_a_throwing_handler_stops_nothing()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        IReadOnlyList<(string, bool)> drives = [(@"C:\", true)];
        using var service = new WinVolumeService(() => Volatile.Read(ref drives), TimeSpan.FromMilliseconds(20));
        var raised = 0;
        service.Changed += (_, _) => throw new InvalidOperationException("a broken handler");
        service.Changed += (_, _) => Interlocked.Increment(ref raised);

        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.Equal(0, Volatile.Read(ref raised)); // nothing changed

        Volatile.Write(ref drives, [(@"C:\", true), (@"D:\", false)]);
        await WaitUntil(() => Volatile.Read(ref raised) == 1);
        Volatile.Write(ref drives, [(@"C:\", true), (@"D:\", true)]); // media inserted
        await WaitUntil(() => Volatile.Read(ref raised) == 2);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.Equal(2, Volatile.Read(ref raised));
    }

    [Fact]
    public async Task Dispose_stops_the_poll()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        IReadOnlyList<(string, bool)> drives = [(@"C:\", true)];
        var service = new WinVolumeService(() => Volatile.Read(ref drives), TimeSpan.FromMilliseconds(20));
        var raised = 0;
        service.Changed += (_, _) => Interlocked.Increment(ref raised);

        service.Dispose();
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Volatile.Write(ref drives, [(@"D:\", true)]);
        await Task.Delay(200, TestContext.Current.CancellationToken);

        Assert.Equal(0, Volatile.Read(ref raised));
        service.Dispose(); // twice is fine
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out.");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }
}
