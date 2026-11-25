using Tandem.Core.Platform.Portable;

namespace Tandem.Core.Tests.Platform;

public sealed class PortableNativeCopyTests : IDisposable
{
    private const int Block = 1024 * 1024;

    private readonly string _root =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "tandem-tests", Guid.NewGuid().ToString("N"))).FullName;

    private readonly PortableNativeCopy _copy = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Func<CancellationToken, ValueTask> NoPause = _ => ValueTask.CompletedTask;

    public void Dispose()
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories))
        {
            try
            {
                File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A dangling link: nothing to unlock.
            }
        }
        Directory.Delete(_root, recursive: true);
    }

    private string Combine(string relative) => Path.Combine(_root, relative);

    [Fact]
    public async Task Copies_the_content_in_blocks_reporting_running_totals()
    {
        var source = Combine("big.bin");
        var content = RandomBytes(3 * Block + Block / 2);
        await File.WriteAllBytesAsync(source, content, Ct);
        var progress = new Recorder();

        await _copy.CopyFileAsync(source, Combine(".big.bin.tandem-part"), progress, NoPause, Ct);

        Assert.Equal(content, await File.ReadAllBytesAsync(Combine(".big.bin.tandem-part"), Ct));
        Assert.Equal([Block, 2L * Block, 3L * Block, content.Length], progress.Values);
    }

    [Fact]
    public async Task An_empty_file_is_copied_and_reported_as_zero_bytes()
    {
        var source = Combine("empty");
        await File.WriteAllBytesAsync(source, [], Ct);
        var progress = new Recorder();

        await _copy.CopyFileAsync(source, Combine(".empty.tandem-part"), progress, NoPause, Ct);

        Assert.Equal(0, new FileInfo(Combine(".empty.tandem-part")).Length);
        Assert.Equal([0L], progress.Values);
    }

    [Fact]
    public async Task Copies_the_times_and_on_unix_the_mode()
    {
        var source = Combine("source.txt");
        await File.WriteAllTextAsync(source, "data", Ct);
        var created = new DateTime(2019, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var written = new DateTime(2020, 5, 17, 10, 30, 0, DateTimeKind.Utc);
        File.SetCreationTimeUtc(source, created);
        File.SetLastWriteTimeUtc(source, written);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(source, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        var destination = Combine(".source.txt.tandem-part");

        await _copy.CopyFileAsync(source, destination, new Recorder(), NoPause, Ct);

        Assert.Equal(written, File.GetLastWriteTimeUtc(destination));
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
            Assert.Equal(created, File.GetCreationTimeUtc(destination));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead,
                File.GetUnixFileMode(destination));
    }

    [Fact]
    public async Task On_windows_the_attributes_are_copied_without_read_only_until_CopyFlags()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var source = Combine("source.txt");
        await File.WriteAllTextAsync(source, "data", Ct);
        File.SetAttributes(source, FileAttributes.Hidden | FileAttributes.ReadOnly | FileAttributes.Archive);
        var destination = Combine(".source.txt.tandem-part");

        await _copy.CopyFileAsync(source, destination, new Recorder(), NoPause, Ct);

        var attributes = File.GetAttributes(destination);
        Assert.True(attributes.HasFlag(FileAttributes.Hidden));
        Assert.False(attributes.HasFlag(FileAttributes.ReadOnly));

        var placed = Combine("placed.txt");
        File.Move(destination, placed);
        await _copy.CopyFlagsAsync(source, placed, Ct);

        Assert.True(File.GetAttributes(placed).HasFlag(FileAttributes.ReadOnly));
        Assert.True(File.GetAttributes(placed).HasFlag(FileAttributes.Hidden));
    }

    [Fact]
    public async Task On_unix_CopyFlags_changes_nothing()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Unix only");
            return;
        }
        var source = Combine("source.txt");
        var destination = Combine("destination.txt");
        await File.WriteAllTextAsync(source, "s", Ct);
        await File.WriteAllTextAsync(destination, "d", Ct);
        File.SetUnixFileMode(source, UnixFileMode.UserRead);
        File.SetUnixFileMode(destination, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        await _copy.CopyFlagsAsync(source, destination, Ct);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(destination));
        Assert.Equal("d", await File.ReadAllTextAsync(destination, Ct));
    }

    [Fact]
    public async Task A_leftover_file_at_the_destination_is_replaced_even_when_read_only()
    {
        var source = Combine("source.txt");
        await File.WriteAllTextAsync(source, "new", Ct);
        var destination = Combine(".source.txt.tandem-part");
        await File.WriteAllTextAsync(destination, "stale leftover, longer than the source", Ct);
        if (OperatingSystem.IsWindows()) File.SetAttributes(destination, FileAttributes.ReadOnly);
        else File.SetUnixFileMode(destination, UnixFileMode.UserRead);

        await _copy.CopyFileAsync(source, destination, new Recorder(), NoPause, Ct);

        Assert.Equal("new", await File.ReadAllTextAsync(destination, Ct));
    }

    [Fact]
    public async Task A_link_at_the_destination_is_replaced_itself_and_its_target_is_untouched()
    {
        var source = Combine("source.txt");
        await File.WriteAllTextAsync(source, "new", Ct);
        var target = Combine("target.txt");
        await File.WriteAllTextAsync(target, "target", Ct);
        Directory.CreateDirectory(Combine("target-dir"));
        var fileLink = Combine(".a.tandem-part");
        var directoryLink = Combine(".b.tandem-part");
        try
        {
            File.CreateSymbolicLink(fileLink, target);
            Directory.CreateSymbolicLink(directoryLink, Combine("target-dir"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("Symbolic links cannot be created here (Windows without the privilege).");
        }

        await _copy.CopyFileAsync(source, fileLink, new Recorder(), NoPause, Ct);
        await _copy.CopyFileAsync(source, directoryLink, new Recorder(), NoPause, Ct);

        Assert.Null(new FileInfo(fileLink).LinkTarget);
        Assert.Equal("new", await File.ReadAllTextAsync(fileLink, Ct));
        Assert.Null(new FileInfo(directoryLink).LinkTarget);
        Assert.Equal("new", await File.ReadAllTextAsync(directoryLink, Ct));
        Assert.Equal("target", await File.ReadAllTextAsync(target, Ct));
        Assert.True(Directory.Exists(Combine("target-dir")));
    }

    [Fact]
    public async Task A_directory_at_the_destination_is_an_IOException_and_is_kept()
    {
        var source = Combine("source.txt");
        await File.WriteAllTextAsync(source, "new", Ct);
        var destination = Directory.CreateDirectory(Combine(".source.txt.tandem-part")).FullName;
        await File.WriteAllTextAsync(Path.Combine(destination, "inside"), "kept", Ct);

        await Assert.ThrowsAnyAsync<IOException>(
            () => _copy.CopyFileAsync(source, destination, new Recorder(), NoPause, Ct));

        Assert.Equal("kept", await File.ReadAllTextAsync(Path.Combine(destination, "inside"), Ct));
    }

    [Fact]
    public async Task A_missing_source_is_FileNotFound_and_creates_nothing()
    {
        var destination = Combine(".missing.tandem-part");

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => _copy.CopyFileAsync(Combine("missing"), destination, new Recorder(), NoPause, Ct));

        Assert.False(Path.Exists(destination));
    }

    [Fact]
    public async Task The_checkpoint_between_blocks_holds_the_copy_while_paused()
    {
        var source = Combine("big.bin");
        await File.WriteAllBytesAsync(source, RandomBytes(3 * Block), Ct);
        var destination = Combine(".big.bin.tandem-part");
        var reachedPause = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var checkpoints = 0;
        var progress = new Recorder();

        var copy = _copy.CopyFileAsync(source, destination, progress, async token =>
        {
            if (Interlocked.Increment(ref checkpoints) != 1) return;
            reachedPause.SetResult();
            await resume.Task.WaitAsync(token);
        }, Ct);

        await reachedPause.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await Task.Delay(100, Ct);
        Assert.False(copy.IsCompleted);
        Assert.Equal([(long)Block], progress.Values);

        resume.SetResult();
        await copy.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.True(checkpoints >= 2);
        Assert.Equal(3L * Block, new FileInfo(destination).Length);
    }

    [Fact]
    public async Task Cancelling_during_the_copy_throws_OperationCanceled_and_removes_the_part()
    {
        var source = Combine("big.bin");
        await File.WriteAllBytesAsync(source, RandomBytes(3 * Block), Ct);
        var destination = Combine(".big.bin.tandem-part");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _copy.CopyFileAsync(source, destination,
            new Recorder(), async token =>
            {
                await cts.CancelAsync();
                token.ThrowIfCancellationRequested();
            }, cts.Token));

        Assert.False(Path.Exists(destination));
    }

    [Fact]
    public async Task A_cancelled_token_copies_nothing()
    {
        var source = Combine("source.txt");
        await File.WriteAllTextAsync(source, "data", Ct);
        var destination = Combine(".source.txt.tandem-part");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _copy.CopyFileAsync(source, destination, new Recorder(), NoPause, new CancellationToken(canceled: true)));

        Assert.False(Path.Exists(destination));
    }

    [Fact]
    public async Task A_failure_during_the_copy_removes_the_part()
    {
        var source = Combine("big.bin");
        await File.WriteAllBytesAsync(source, RandomBytes(2 * Block), Ct);
        var destination = Combine(".big.bin.tandem-part");

        await Assert.ThrowsAsync<InvalidOperationException>(() => _copy.CopyFileAsync(source, destination,
            new Recorder(), _ => throw new InvalidOperationException("boom"), Ct));

        Assert.False(Path.Exists(destination));
    }

    [Fact]
    public async Task CopyDirectoryMetadata_copies_times_and_mode_or_attributes()
    {
        var source = Directory.CreateDirectory(Combine("source")).FullName;
        var destination = Directory.CreateDirectory(Combine("destination")).FullName;
        await File.WriteAllTextAsync(Path.Combine(destination, "inside"), "kept", Ct);
        var created = new DateTime(2018, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        var written = new DateTime(2021, 6, 7, 8, 9, 10, DateTimeKind.Utc);
        if (OperatingSystem.IsWindows())
            File.SetAttributes(source, FileAttributes.Directory | FileAttributes.Hidden);
        else
            File.SetUnixFileMode(source, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                                         UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
        Directory.SetCreationTimeUtc(source, created);
        Directory.SetLastWriteTimeUtc(source, written);

        await _copy.CopyDirectoryMetadataAsync(source, destination, Ct);

        Assert.Equal(written, Directory.GetLastWriteTimeUtc(destination));
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
            Assert.Equal(created, Directory.GetCreationTimeUtc(destination));
        if (OperatingSystem.IsWindows())
            Assert.True(File.GetAttributes(destination).HasFlag(FileAttributes.Hidden));
        else
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                         UnixFileMode.GroupRead | UnixFileMode.GroupExecute, File.GetUnixFileMode(destination));
        Assert.Equal(["inside"], Directory.GetFileSystemEntries(destination).Select(Path.GetFileName));
    }

    [Fact]
    public async Task CopyFileMetadata_leaves_the_data_times_and_mode_alone()
    {
        var source = Combine("source.txt");
        var destination = Combine(".source.txt.tandem-part");
        await File.WriteAllTextAsync(source, "old", Ct);
        await File.WriteAllTextAsync(destination, "new content", Ct);
        File.SetLastWriteTimeUtc(source, new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var written = new DateTime(2022, 2, 2, 2, 2, 2, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(destination, written);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(source, UnixFileMode.UserRead);
            File.SetUnixFileMode(destination, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        await _copy.CopyFileMetadataAsync(source, destination, Ct);

        Assert.Equal("new content", await File.ReadAllTextAsync(destination, Ct));
        Assert.Equal(written, File.GetLastWriteTimeUtc(destination));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(destination));
    }

    [Fact]
    public async Task On_windows_CopyFileMetadata_carries_attributes_but_read_only_and_the_zone_stream()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var source = Combine("source.txt");
        var destination = Combine(".source.txt.tandem-part");
        await File.WriteAllTextAsync(source, "old", Ct);
        await File.WriteAllTextAsync(destination, "new", Ct);
        var zone = "[ZoneTransfer]\r\nZoneId=3\r\n"u8.ToArray();
        await File.WriteAllBytesAsync(source + ":Zone.Identifier", zone, Ct);
        File.SetAttributes(source, FileAttributes.Hidden | FileAttributes.ReadOnly);

        await _copy.CopyFileMetadataAsync(source, destination, Ct);

        var attributes = File.GetAttributes(destination);
        Assert.True(attributes.HasFlag(FileAttributes.Hidden));
        Assert.False(attributes.HasFlag(FileAttributes.ReadOnly));
        Assert.Equal(zone, await File.ReadAllBytesAsync(destination + ":Zone.Identifier", Ct));
        Assert.Equal("new", await File.ReadAllTextAsync(destination, Ct));
    }

    private static byte[] RandomBytes(int count)
    {
        var bytes = new byte[count];
        Random.Shared.NextBytes(bytes);
        return bytes;
    }

    /// <summary>Records reported values synchronously (unlike <see cref="Progress{T}"/>, which posts).</summary>
    private sealed class Recorder : IProgress<long>
    {
        private readonly List<long> _values = [];

        public IReadOnlyList<long> Values
        {
            get { lock (_values) return _values.ToArray(); }
        }

        public void Report(long value)
        {
            lock (_values) _values.Add(value);
        }
    }
}
