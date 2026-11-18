using System.Runtime.Versioning;
using Tandem.Core.Vfs.Local;

namespace Tandem.Platform.Mac.Tests;

[SupportedOSPlatform("macos")]
public sealed class MacNativeCopyTests : IDisposable
{
    private readonly TempDir _temp = new();
    private readonly MacNativeCopy _copy = new(new UnrestrictedAccessBroker());

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task Copy_preserves_content_mtime_mode_and_xattr()
    {
        var source = _temp.Combine("source.bin");
        var content = RandomBytes(64 * 1024);
        await File.WriteAllBytesAsync(source, content, TestContext.Current.CancellationToken);
        var mtime = new DateTime(2020, 5, 17, 10, 30, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(source, mtime);
        File.SetUnixFileMode(source, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        XAttr.Set(source, "user.tandem.test", "kept");
        var destination = _temp.Combine(".source.bin.tandem-part");

        await _copy.CopyFileAsync(source, destination, new Recorder(), TestContext.Current.CancellationToken);

        Assert.Equal(content, await File.ReadAllBytesAsync(destination, TestContext.Current.CancellationToken));
        Assert.Equal(mtime, File.GetLastWriteTimeUtc(destination));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead,
            File.GetUnixFileMode(destination));
        Assert.Equal("kept", XAttr.Get(destination, "user.tandem.test"));
    }

    [Fact]
    public async Task Copy_reports_final_byte_count()
    {
        var source = _temp.Combine("big.bin");
        await File.WriteAllBytesAsync(source, RandomBytes(8 * 1024 * 1024), TestContext.Current.CancellationToken);
        var progress = new Recorder();

        await _copy.CopyFileAsync(source, _temp.Combine(".big.bin.tandem-part"), progress,
            TestContext.Current.CancellationToken);

        Assert.NotEmpty(progress.Values);
        Assert.Equal(8L * 1024 * 1024, progress.Values[^1]);
    }

    [Fact]
    public async Task Copy_cancel_throws_OperationCanceled()
    {
        var source = _temp.Combine("source.txt");
        await File.WriteAllTextAsync(source, "data", TestContext.Current.CancellationToken);
        var destination = _temp.Combine(".source.txt.tandem-part");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _copy.CopyFileAsync(source, destination, new Recorder(), new CancellationToken(canceled: true)));
        Assert.False(Path.Exists(destination));
    }

    [Fact]
    public async Task Copy_to_missing_directory_throws_IOException()
    {
        var source = _temp.Combine("source.txt");
        await File.WriteAllTextAsync(source, "data", TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<IOException>(() => _copy.CopyFileAsync(source,
            _temp.Combine("missing/.source.txt.tandem-part"), new Recorder(), TestContext.Current.CancellationToken));
        Assert.Equal("No such file or directory", error.Message);
    }

    [Fact]
    public async Task Copy_replaces_a_stale_part_file()
    {
        var source = _temp.Combine("source.txt");
        await File.WriteAllTextAsync(source, "fresh", TestContext.Current.CancellationToken);
        var destination = _temp.Combine(".source.txt.tandem-part");
        await File.WriteAllTextAsync(destination, "stale leftover, longer", TestContext.Current.CancellationToken);

        await _copy.CopyFileAsync(source, destination, new Recorder(), TestContext.Current.CancellationToken);

        Assert.Equal("fresh", await File.ReadAllTextAsync(destination, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Copy_replaces_a_stale_part_symlink_without_writing_through_it()
    {
        var source = _temp.Combine("source.txt");
        await File.WriteAllTextAsync(source, "fresh", TestContext.Current.CancellationToken);
        var elsewhere = _temp.Combine("elsewhere.txt");
        await File.WriteAllTextAsync(elsewhere, "untouched", TestContext.Current.CancellationToken);
        var destination = _temp.Combine(".source.txt.tandem-part");
        File.CreateSymbolicLink(destination, elsewhere);

        await _copy.CopyFileAsync(source, destination, new Recorder(), TestContext.Current.CancellationToken);

        Assert.Null(new FileInfo(destination).LinkTarget);
        Assert.Equal("fresh", await File.ReadAllTextAsync(destination, TestContext.Current.CancellationToken));
        Assert.Equal("untouched", await File.ReadAllTextAsync(elsewhere, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Copy_of_a_symlink_copies_the_link_itself()
    {
        var target = _temp.Combine("target.txt");
        await File.WriteAllTextAsync(target, "target", TestContext.Current.CancellationToken);
        var link = _temp.Combine("link");
        File.CreateSymbolicLink(link, target);
        var destination = _temp.Combine(".link.tandem-part");

        await _copy.CopyFileAsync(link, destination, new Recorder(), TestContext.Current.CancellationToken);

        Assert.Equal(target, new FileInfo(destination).LinkTarget);
    }

    [Fact]
    public async Task Copy_accesses_source_and_destination_through_the_broker()
    {
        var source = _temp.Combine("source.txt");
        await File.WriteAllTextAsync(source, "data", TestContext.Current.CancellationToken);
        var destination = _temp.Combine(".source.txt.tandem-part");
        var broker = new CountingBroker();

        await new MacNativeCopy(broker).CopyFileAsync(source, destination, new Recorder(),
            TestContext.Current.CancellationToken);

        Assert.Equal([source, destination], broker.Accessed);
        Assert.Equal(0, broker.OpenScopes);
    }

    [Fact]
    public async Task Copy_without_clone_reports_increasing_totals_ending_at_the_file_size()
    {
        var source = _temp.Combine("big.bin");
        await File.WriteAllBytesAsync(source, RandomBytes(8 * 1024 * 1024), TestContext.Current.CancellationToken);
        var progress = new Recorder();

        await WithoutClone().CopyFileAsync(source, _temp.Combine(".big.bin.tandem-part"), progress,
            TestContext.Current.CancellationToken);

        var values = progress.Values;
        Assert.True(values.Count > 2, $"expected several reports from the callback, got {values.Count}");
        Assert.All(values.Zip(values.Skip(1)), pair => Assert.True(pair.Second >= pair.First));
        Assert.True(values[0] < 8L * 1024 * 1024);
        Assert.Equal(8L * 1024 * 1024, values[^1]);
    }

    [Fact]
    public async Task Copy_cancelled_mid_copy_throws_OperationCanceled()
    {
        var source = _temp.Combine("big.bin");
        await File.WriteAllBytesAsync(source, RandomBytes(8 * 1024 * 1024), TestContext.Current.CancellationToken);
        using var cts = new CancellationTokenSource();
        var progress = new Recorder(onReport: _ => cts.Cancel());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            WithoutClone().CopyFileAsync(source, _temp.Combine(".big.bin.tandem-part"), progress, cts.Token));
        Assert.Single(progress.Values);
    }

    [Fact]
    public async Task Copy_surfaces_an_exception_thrown_by_progress()
    {
        var source = _temp.Combine("big.bin");
        await File.WriteAllBytesAsync(source, RandomBytes(8 * 1024 * 1024), TestContext.Current.CancellationToken);
        var progress = new Recorder(onReport: _ => throw new InvalidOperationException("progress failed"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => WithoutClone().CopyFileAsync(source,
            _temp.Combine(".big.bin.tandem-part"), progress, TestContext.Current.CancellationToken));
        Assert.Equal("progress failed", error.Message);
        Assert.Single(progress.Values);
    }

    [Fact]
    public async Task Copy_onto_a_full_volume_throws_IOException_with_the_system_message()
    {
        using var volume = TinyVolume.Create(_temp.Combine("tiny.dmg"), _temp.Combine("mnt"));
        var source = _temp.Combine("big.bin");
        await File.WriteAllBytesAsync(source, RandomBytes(8 * 1024 * 1024), TestContext.Current.CancellationToken);

        var copy = _copy.CopyFileAsync(source, Path.Combine(volume.MountPoint, ".big.bin.tandem-part"),
            new Recorder(), TestContext.Current.CancellationToken);
        var finished = await Task.WhenAny(copy, Task.Delay(TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken));

        Assert.Same(copy, finished); // an error stage answered with "continue" would retry the write forever
        var error = await Assert.ThrowsAsync<IOException>(() => copy);
        Assert.Equal("No space left on device", error.Message);
    }

    private static MacNativeCopy WithoutClone() =>
        new(new UnrestrictedAccessBroker(), MacNativeCopy.DefaultFlags & ~Interop.LibC.COPYFILE_CLONE);

    private static byte[] RandomBytes(int count)
    {
        var bytes = new byte[count];
        Random.Shared.NextBytes(bytes);
        return bytes;
    }

    /// <summary>Records every report synchronously, in order, then runs <c>onReport</c>.</summary>
    private sealed class Recorder(Action<long>? onReport = null) : IProgress<long>
    {
        private readonly Lock _lock = new();
        private readonly List<long> _values = [];

        public IReadOnlyList<long> Values
        {
            get { lock (_lock) return [.. _values]; }
        }

        public void Report(long value)
        {
            lock (_lock) _values.Add(value);
            onReport?.Invoke(value);
        }
    }
}
