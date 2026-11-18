using System.Runtime.Versioning;

namespace Tandem.Platform.Mac.Tests;

/// <summary>Reads the real mounted volumes (never mounts or ejects anything); the change notifications are tested on a
/// temp directory standing in for <c>/Volumes</c>.</summary>
[SupportedOSPlatform("macos")]
public sealed class MacVolumeServiceTests : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly TempDir _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Volumes_include_root()
    {
        using var service = new MacVolumeService();

        var root = Assert.Single(service.GetVolumes(), v => v.MountPoint == "/");
        Assert.True(root.TotalBytes > 0);
        Assert.InRange(root.FreeBytes, 0, root.TotalBytes);
        Assert.False(string.IsNullOrEmpty(root.Name));
    }

    [Fact]
    public void Root_comes_first_then_volumes_by_name()
    {
        using var service = new MacVolumeService();

        var volumes = service.GetVolumes();

        Assert.Equal("/", volumes[0].MountPoint);
        Assert.Equal(volumes.Skip(1).OrderBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(v => v.MountPoint, StringComparer.Ordinal), volumes.Skip(1));
        Assert.All(volumes, v =>
        {
            Assert.True(Path.IsPathFullyQualified(v.MountPoint));
            Assert.True(v.MountPoint == "/" || !v.MountPoint.EndsWith('/'));
            Assert.False(string.IsNullOrEmpty(v.Name));
        });
    }

    [Fact]
    public void Hidden_system_volumes_are_skipped()
    {
        using var service = new MacVolumeService();

        Assert.DoesNotContain(service.GetVolumes(), v => v.MountPoint.StartsWith("/System/Volumes/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetVolumes_is_safe_from_several_threads()
    {
        using var service = new MacVolumeService();

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(service.GetVolumes)));

        Assert.All(results, r => Assert.Equal("/", r[0].MountPoint));
    }

    [Fact]
    public async Task A_burst_of_changes_raises_Changed_once()
    {
        using var service = new MacVolumeService(_temp.Path, Debounce);
        var count = 0;
        service.Changed += (_, _) => Interlocked.Increment(ref count);

        for (var i = 0; i < 3; i++) Directory.CreateDirectory(_temp.Combine($"Volume {i}"));

        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref count) > 0, Timeout));
        await Task.Delay(Debounce * 5, TestContext.Current.CancellationToken);
        Assert.Equal(1, Volatile.Read(ref count));
    }

    [Fact]
    public void A_throwing_handler_does_not_stop_other_handlers_or_later_events()
    {
        using var service = new MacVolumeService(_temp.Path, Debounce);
        var count = 0;
        service.Changed += (_, _) => throw new InvalidOperationException("handler failure");
        service.Changed += (_, _) => Interlocked.Increment(ref count);

        Directory.CreateDirectory(_temp.Combine("first"));
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref count) == 1, Timeout));

        Directory.Delete(_temp.Combine("first"));
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref count) == 2, Timeout));
    }

    [Fact]
    public async Task No_Changed_after_Dispose()
    {
        var service = new MacVolumeService(_temp.Path, Debounce);
        var count = 0;
        service.Changed += (_, _) => Interlocked.Increment(ref count);
        service.Dispose();

        Directory.CreateDirectory(_temp.Combine("after"));
        await Task.Delay(Debounce * 5, TestContext.Current.CancellationToken);

        Assert.Equal(0, Volatile.Read(ref count));
        service.Dispose();
    }

    [Fact]
    public async Task Dispose_waits_for_a_Changed_being_raised()
    {
        var service = new MacVolumeService(_temp.Path, Debounce);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var finished = false;
        service.Changed += (_, _) =>
        {
            entered.Set();
            release.Wait(Timeout);
            Volatile.Write(ref finished, true);
        };

        Directory.CreateDirectory(_temp.Combine("busy"));
        Assert.True(entered.Wait(Timeout, TestContext.Current.CancellationToken));
        var dispose = Task.Run(service.Dispose, TestContext.Current.CancellationToken);
        await Task.Delay(Debounce, TestContext.Current.CancellationToken);
        Assert.False(dispose.IsCompleted);

        release.Set();
        await dispose.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.True(Volatile.Read(ref finished));
    }

    [Fact]
    public void Dispose_from_a_Changed_handler_does_not_hang()
    {
        var service = new MacVolumeService(_temp.Path, Debounce);
        using var disposed = new ManualResetEventSlim();
        service.Changed += (_, _) =>
        {
            service.Dispose();
            disposed.Set();
        };

        Directory.CreateDirectory(_temp.Combine("self"));

        Assert.True(disposed.Wait(Timeout, TestContext.Current.CancellationToken));
    }
}
