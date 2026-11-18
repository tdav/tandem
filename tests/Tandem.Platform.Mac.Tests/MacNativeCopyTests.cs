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

    private static byte[] RandomBytes(int count)
    {
        var bytes = new byte[count];
        Random.Shared.NextBytes(bytes);
        return bytes;
    }

    /// <summary>Records every report synchronously, in order.</summary>
    private sealed class Recorder : IProgress<long>
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
        }
    }
}
