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

        await _copy.CopyFileAsync(source, destination, new Recorder(), NoPause, TestContext.Current.CancellationToken);

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

        await _copy.CopyFileAsync(source, _temp.Combine(".big.bin.tandem-part"), progress, NoPause,
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
            _copy.CopyFileAsync(source, destination, new Recorder(), NoPause, new CancellationToken(canceled: true)));
        Assert.False(Path.Exists(destination));
    }

    [Fact]
    public async Task Copy_to_missing_directory_throws_FileNotFound()
    {
        var source = _temp.Combine("source.txt");
        await File.WriteAllTextAsync(source, "data", TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<FileNotFoundException>(() => _copy.CopyFileAsync(source,
            _temp.Combine("missing/.source.txt.tandem-part"), new Recorder(), NoPause, TestContext.Current.CancellationToken));
        Assert.Equal("No such file or directory", error.Message);
    }

    [Fact]
    public async Task Copy_into_a_read_only_directory_throws_UnauthorizedAccess()
    {
        var source = _temp.Combine("source.txt");
        await File.WriteAllTextAsync(source, "data", TestContext.Current.CancellationToken);
        var readOnly = Directory.CreateDirectory(_temp.Combine("read-only")).FullName;
        File.SetUnixFileMode(readOnly, UnixFileMode.UserRead | UnixFileMode.UserExecute); // restored by TempDir

        var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _copy.CopyFileAsync(source,
            Path.Combine(readOnly, ".source.txt.tandem-part"), new Recorder(), NoPause,
            TestContext.Current.CancellationToken));
        Assert.Equal("Permission denied", error.Message);
    }

    [Fact]
    public async Task Copy_replaces_a_stale_part_file()
    {
        var source = _temp.Combine("source.txt");
        await File.WriteAllTextAsync(source, "fresh", TestContext.Current.CancellationToken);
        var destination = _temp.Combine(".source.txt.tandem-part");
        await File.WriteAllTextAsync(destination, "stale leftover, longer", TestContext.Current.CancellationToken);

        await _copy.CopyFileAsync(source, destination, new Recorder(), NoPause, TestContext.Current.CancellationToken);

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

        await _copy.CopyFileAsync(source, destination, new Recorder(), NoPause, TestContext.Current.CancellationToken);

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

        await _copy.CopyFileAsync(link, destination, new Recorder(), NoPause, TestContext.Current.CancellationToken);

        Assert.Equal(target, new FileInfo(destination).LinkTarget);
    }

    [Fact]
    public async Task Copy_accesses_source_and_destination_through_the_broker()
    {
        var source = _temp.Combine("source.txt");
        await File.WriteAllTextAsync(source, "data", TestContext.Current.CancellationToken);
        var destination = _temp.Combine(".source.txt.tandem-part");
        var broker = new CountingBroker();

        await new MacNativeCopy(broker).CopyFileAsync(source, destination, new Recorder(), NoPause,
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

        await WithoutClone().CopyFileAsync(source, _temp.Combine(".big.bin.tandem-part"), progress, NoPause,
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
            WithoutClone().CopyFileAsync(source, _temp.Combine(".big.bin.tandem-part"), progress, NoPause, cts.Token));
        Assert.Single(progress.Values);
    }

    [Fact]
    public async Task Copy_paused_at_the_checkpoint_stops_until_resumed_then_completes()
    {
        var source = _temp.Combine("big.bin");
        var content = RandomBytes(8 * 1024 * 1024);
        await File.WriteAllBytesAsync(source, content, TestContext.Current.CancellationToken);
        var destination = _temp.Combine(".big.bin.tandem-part");
        var progress = new Recorder();
        var pause = new PauseGate();

        var copy = WithoutClone().CopyFileAsync(source, destination, progress, pause.CheckpointAsync,
            TestContext.Current.CancellationToken);
        await pause.Entered.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var reportsWhenPaused = progress.Values.Count;
        await Task.Delay(300, TestContext.Current.CancellationToken);

        Assert.False(copy.IsCompleted);
        Assert.Equal(reportsWhenPaused, progress.Values.Count); // no byte moved while paused
        Assert.True(progress.Values[^1] < content.Length);

        pause.Release();
        await copy.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(content.Length, progress.Values[^1]);
        Assert.Equal(content, await File.ReadAllBytesAsync(destination, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Copy_cancelled_while_paused_throws_OperationCanceled()
    {
        var source = _temp.Combine("big.bin");
        await File.WriteAllBytesAsync(source, RandomBytes(8 * 1024 * 1024), TestContext.Current.CancellationToken);
        using var cts = new CancellationTokenSource();
        var progress = new Recorder();
        var pause = new PauseGate();

        var copy = WithoutClone().CopyFileAsync(source, _temp.Combine(".big.bin.tandem-part"), progress,
            pause.CheckpointAsync, cts.Token);
        await pause.Entered.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var reportsWhenPaused = progress.Values.Count;
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            copy.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Equal(reportsWhenPaused, progress.Values.Count);
    }

    [Fact]
    public void Flags_are_read_from_lstat_as_chflags_sets_them()
    {
        var path = _temp.Combine("flagged.txt");
        File.WriteAllText(path, "x");
        RunChflags("uchg", path);

        Assert.Equal(FileFlags.Locked, FileFlags.Get(path) & (FileFlags.Locked | FileFlags.AppendOnly));
        Assert.Equal(0u, FileFlags.Get(_temp.Path) & FileFlags.Locked);
        // the mode field sits where the struct says, too
        Assert.True(Interop.LibC.lstat(path, out var stat) == 0);
        Assert.Equal((int)File.GetUnixFileMode(path), stat.Mode & 0xFFF);
    }

    [Fact]
    public async Task Copy_of_a_locked_file_leaves_the_part_file_unlocked_and_finishable()
    {
        var source = _temp.Combine("locked.txt");
        await File.WriteAllTextAsync(source, "locked content", TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(source, new DateTime(2021, 2, 3, 4, 5, 6, DateTimeKind.Utc));
        FileFlags.Add(source, FileFlags.Locked);
        var destination = _temp.Combine(".locked.txt.tandem-part");

        await _copy.CopyFileAsync(source, destination, new Recorder(), NoPause, TestContext.Current.CancellationToken);

        Assert.Equal(0u, FileFlags.Get(destination) & (FileFlags.Locked | FileFlags.AppendOnly));
        Assert.Equal("locked content", await File.ReadAllTextAsync(destination, TestContext.Current.CancellationToken));
        // What the engine does next works: set the time, rename into place.
        File.SetLastWriteTimeUtc(destination, new DateTime(2021, 2, 3, 4, 5, 6, DateTimeKind.Utc));
        File.Move(destination, _temp.Combine("locked copy.txt"));
        Assert.NotEqual(0u, FileFlags.Get(source) & FileFlags.Locked); // the source is untouched
    }

    [Fact]
    public async Task Copy_replaces_a_stale_locked_part_file()
    {
        var source = _temp.Combine("source.txt");
        await File.WriteAllTextAsync(source, "fresh", TestContext.Current.CancellationToken);
        var destination = _temp.Combine(".source.txt.tandem-part");
        await File.WriteAllTextAsync(destination, "stale leftover", TestContext.Current.CancellationToken);
        FileFlags.Add(destination, FileFlags.Locked | FileFlags.AppendOnly); // left by an older version

        await _copy.CopyFileAsync(source, destination, new Recorder(), NoPause, TestContext.Current.CancellationToken);

        Assert.Equal("fresh", await File.ReadAllTextAsync(destination, TestContext.Current.CancellationToken));
        Assert.Equal(0u, FileFlags.Get(destination) & (FileFlags.Locked | FileFlags.AppendOnly));
    }

    [Fact]
    public async Task CopyFlags_gives_the_placed_copy_the_sources_lock()
    {
        var source = _temp.Combine("locked.txt");
        await File.WriteAllTextAsync(source, "x", TestContext.Current.CancellationToken);
        FileFlags.Add(source, FileFlags.Locked);
        var destination = _temp.Combine("copy.txt");
        await File.WriteAllTextAsync(destination, "x", TestContext.Current.CancellationToken);
        FileFlags.Add(destination, 0x8000); // UF_HIDDEN, copied earlier with the rest of the metadata
        var plain = _temp.Combine("plain.txt");
        await File.WriteAllTextAsync(plain, "x", TestContext.Current.CancellationToken);
        var plainCopy = _temp.Combine("plain copy.txt");
        await File.WriteAllTextAsync(plainCopy, "x", TestContext.Current.CancellationToken);

        await _copy.CopyFlagsAsync(source, destination, TestContext.Current.CancellationToken);
        await _copy.CopyFlagsAsync(plain, plainCopy, TestContext.Current.CancellationToken);

        Assert.Equal(FileFlags.Locked | 0x8000u, FileFlags.Get(destination));
        Assert.Equal(0u, FileFlags.Get(plainCopy));
    }

    [Fact]
    public async Task CopyFlags_of_a_vanished_destination_throws_FileNotFound()
    {
        var source = _temp.Combine("a.txt");
        await File.WriteAllTextAsync(source, "x", TestContext.Current.CancellationToken);
        FileFlags.Add(source, FileFlags.Locked);

        await Assert.ThrowsAnyAsync<IOException>(() =>
            _copy.CopyFlagsAsync(source, _temp.Combine("gone.txt"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Directory_metadata_is_copied_without_the_contents()
    {
        var source = Directory.CreateDirectory(_temp.Combine("tagged")).FullName;
        File.WriteAllText(Path.Combine(source, "inside.txt"), "x");
        XAttr.Set(source, "user.tandem.test", "folder attribute");
        var metadata = new MacFileMetadataService(new UnrestrictedAccessBroker());
        metadata.SetTags(source, ["Blue", "Работа"]);
        File.SetUnixFileMode(source, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
        var destination = Directory.CreateDirectory(_temp.Combine("copy")).FullName;

        await _copy.CopyDirectoryMetadataAsync(source, destination, TestContext.Current.CancellationToken);

        Assert.Equal("folder attribute", XAttr.Get(destination, "user.tandem.test"));
        Assert.Equal(["Blue", "Работа"], metadata.GetTags(destination).Order(StringComparer.Ordinal));
        Assert.Equal(File.GetUnixFileMode(source), File.GetUnixFileMode(destination));
        Assert.Empty(Directory.EnumerateFileSystemEntries(destination));
    }

    [Fact]
    public async Task File_metadata_is_copied_without_the_data_times_or_lock()
    {
        var source = _temp.Combine("tagged.txt");
        File.WriteAllText(source, "original");
        File.SetLastWriteTimeUtc(source, new DateTime(2020, 5, 17, 10, 30, 0, DateTimeKind.Utc));
        XAttr.Set(source, "user.tandem.test", "file attribute");
        var metadata = new MacFileMetadataService(new UnrestrictedAccessBroker());
        metadata.SetTags(source, ["Red", "Работа"]);
        FileFlags.Add(source, 0x8000); // UF_HIDDEN
        var destination = _temp.Combine(".tagged.txt.tandem-part");
        File.WriteAllText(destination, "new text");
        var written = File.GetLastWriteTimeUtc(destination);
        FileFlags.Add(source, FileFlags.Locked);

        await _copy.CopyFileMetadataAsync(source, destination, TestContext.Current.CancellationToken);

        FileFlags.Unlock(source);
        Assert.Equal("new text", File.ReadAllText(destination));
        Assert.Equal("file attribute", XAttr.Get(destination, "user.tandem.test"));
        Assert.Equal(["Red", "Работа"], metadata.GetTags(destination).Order(StringComparer.Ordinal));
        Assert.Equal(0x8000u, FileFlags.Get(destination) & 0x8000u);
        Assert.Equal(0u, FileFlags.Get(destination) & FileFlags.Locked);
        Assert.Equal(written, File.GetLastWriteTimeUtc(destination));
    }

    [Fact]
    public async Task File_metadata_onto_a_missing_file_throws_IOException()
    {
        var source = _temp.Combine("a.txt");
        File.WriteAllText(source, "x");

        await Assert.ThrowsAnyAsync<IOException>(() => _copy.CopyFileMetadataAsync(source,
            _temp.Combine("missing/inner"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Directory_metadata_onto_a_missing_directory_throws_IOException()
    {
        var source = Directory.CreateDirectory(_temp.Combine("folder")).FullName;

        await Assert.ThrowsAnyAsync<IOException>(() => _copy.CopyDirectoryMetadataAsync(source,
            _temp.Combine("missing/inner"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Copy_surfaces_an_exception_thrown_by_progress()
    {
        var source = _temp.Combine("big.bin");
        await File.WriteAllBytesAsync(source, RandomBytes(8 * 1024 * 1024), TestContext.Current.CancellationToken);
        var progress = new Recorder(onReport: _ => throw new InvalidOperationException("progress failed"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => WithoutClone().CopyFileAsync(source,
            _temp.Combine(".big.bin.tandem-part"), progress, NoPause, TestContext.Current.CancellationToken));
        Assert.Equal("progress failed", error.Message);
        Assert.Single(progress.Values);
    }

    [Fact]
    public async Task Copy_onto_a_full_volume_throws_IOException_with_the_disk_full_HResult()
    {
        using var volume = TinyVolume.Create(_temp.Combine("tiny.dmg"), _temp.Combine("mnt"));
        var source = _temp.Combine("big.bin");
        await File.WriteAllBytesAsync(source, RandomBytes(8 * 1024 * 1024), TestContext.Current.CancellationToken);

        var copy = _copy.CopyFileAsync(source, Path.Combine(volume.MountPoint, ".big.bin.tandem-part"),
            new Recorder(), NoPause, TestContext.Current.CancellationToken);
        var finished = await Task.WhenAny(copy, Task.Delay(TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken));

        Assert.Same(copy, finished); // an error stage answered with "continue" would retry the write forever
        var error = await Assert.ThrowsAsync<IOException>(() => copy);
        Assert.Equal("No space left on device", error.Message);
        Assert.Equal(28, error.HResult); // ENOSPC, as .NET's own file APIs report it
    }

    private static readonly Func<CancellationToken, ValueTask> NoPause = _ => ValueTask.CompletedTask;

    private static void RunChflags(string flags, string path)
    {
        using var process = System.Diagnostics.Process.Start("/usr/bin/chflags", [flags, path]);
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    private static MacNativeCopy WithoutClone() =>
        new(new UnrestrictedAccessBroker(), MacNativeCopy.DefaultFlags & ~Interop.LibC.COPYFILE_CLONE);

    private static byte[] RandomBytes(int count)
    {
        var bytes = new byte[count];
        Random.Shared.NextBytes(bytes);
        return bytes;
    }

    /// <summary>A pause checkpoint that holds the copy at its first call until <see cref="Release"/>, as a paused
    /// operation does; cancelling the token ends the wait.</summary>
    private sealed class PauseGate
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

        public void Release() => _released.TrySetResult();

        public async ValueTask CheckpointAsync(CancellationToken ct)
        {
            _entered.TrySetResult();
            await _released.Task.WaitAsync(ct);
        }
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
