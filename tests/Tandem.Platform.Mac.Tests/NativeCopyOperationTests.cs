using System.Runtime.Versioning;
using Tandem.Core.Operations;
using Tandem.Core.Platform;
using Tandem.Core.Tests.Operations;
using Tandem.Core.Vfs;
using Tandem.Core.Vfs.Local;

namespace Tandem.Platform.Mac.Tests;

/// <summary>The production combination: copy and move operations over the local provider with the real
/// <see cref="MacNativeCopy"/>, in a temporary directory.</summary>
[SupportedOSPlatform("macos")]
public sealed class NativeCopyOperationTests : IDisposable
{
    private const int BigSize = 8 * 1024 * 1024;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private readonly TempDir _temp = new();
    private readonly ProviderRegistry _providers = new();
    private readonly ScriptedInteraction _interaction = new();
    private readonly MacFileMetadataService _metadata = new(new UnrestrictedAccessBroker());

    public NativeCopyOperationTests()
    {
        _providers.Register(new LocalFileSystemProvider(new UnrestrictedAccessBroker()));
        Directory.CreateDirectory(_temp.Combine("src"));
        Directory.CreateDirectory(_temp.Combine("dst"));
    }

    public void Dispose() => _temp.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The production copy.</summary>
    private static MacNativeCopy Native() => new(new UnrestrictedAccessBroker());

    /// <summary>Without cloning, so copyfile copies in blocks and its callback (progress, pause, cancel) runs even
    /// when the temp directory is on APFS.</summary>
    private static MacNativeCopy NativeInBlocks() =>
        new(new UnrestrictedAccessBroker(), MacNativeCopy.DefaultFlags & ~Interop.LibC.COPYFILE_CLONE);

    private CopyOperation Copy(string source, INativeCopy native, CopyOptions? options = null) =>
        new(_providers, [VfsPath.Local(_temp.Combine(source))], VfsPath.Local(_temp.Combine("dst")),
            options ?? new CopyOptions(), _interaction, native);

    private string[] Entries(string directory) =>
        Directory.EnumerateFileSystemEntries(_temp.Combine(directory), "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(_temp.Combine(directory), p))
            .Order(StringComparer.Ordinal).ToArray();

    /// <summary><paramref name="path"/> with its links resolved (/var is /private/var on macOS).</summary>
    private static string RealPath(string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var real = "/";
        foreach (var part in parts)
        {
            real = Path.Combine(real, part);
            if (new FileInfo(real).LinkTarget is { } target) real = Path.GetFullPath(target, Path.GetDirectoryName(real)!);
        }
        return real;
    }

    private static byte[] RandomBytes(int count)
    {
        var bytes = new byte[count];
        Random.Shared.NextBytes(bytes);
        return bytes;
    }

    [Fact]
    public async Task A_plain_file_keeps_content_time_mode_and_extended_attributes()
    {
        var content = RandomBytes(256 * 1024);
        var modified = new DateTime(2020, 5, 17, 10, 30, 0, DateTimeKind.Utc);
        const UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                                  UnixFileMode.GroupRead;
        var source = _temp.Combine("src/plain.bin");
        await File.WriteAllBytesAsync(source, content, Ct);
        XAttr.Set(source, "user.tandem.test", "kept");
        File.SetUnixFileMode(source, mode);
        File.SetLastWriteTimeUtc(source, modified);

        var result = await Copy("src/plain.bin", Native()).RunAsync().WaitAsync(Timeout, Ct);

        Assert.Equal(OperationState.Completed, result.State);
        Assert.Equal(["plain.bin"], Entries("dst"));
        var copy = _temp.Combine("dst/plain.bin");
        Assert.Equal(content, await File.ReadAllBytesAsync(copy, Ct));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(copy));
        Assert.Equal(mode, File.GetUnixFileMode(copy));
        Assert.Equal("kept", XAttr.Get(copy, "user.tandem.test"));
        Assert.Equal(content, await File.ReadAllBytesAsync(source, Ct));
    }

    [Fact]
    public async Task A_read_only_source_is_copied_and_stays_read_only()
    {
        var source = _temp.Combine("src/read-only.txt");
        await File.WriteAllTextAsync(source, "read only", Ct);
        File.SetUnixFileMode(source, UnixFileMode.UserRead | UnixFileMode.GroupRead);

        var result = await Copy("src/read-only.txt", Native()).RunAsync().WaitAsync(Timeout, Ct);

        Assert.Equal(OperationState.Completed, result.State);
        Assert.Empty(_interaction.Errors);
        Assert.Equal(["read-only.txt"], Entries("dst"));
        Assert.Equal("read only", await File.ReadAllTextAsync(_temp.Combine("dst/read-only.txt"), Ct));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.GroupRead,
            File.GetUnixFileMode(_temp.Combine("dst/read-only.txt")));
    }

    [Fact]
    public async Task Overwrite_replaces_the_old_file_and_leaves_no_part_or_backup()
    {
        await File.WriteAllTextAsync(_temp.Combine("src/a.txt"), "new content", Ct);
        await File.WriteAllTextAsync(_temp.Combine("dst/a.txt"), "old", Ct);
        _interaction.OnConflict(ConflictAction.Overwrite);

        var result = await Copy("src/a.txt", Native()).RunAsync().WaitAsync(Timeout, Ct);

        Assert.Equal(OperationState.Completed, result.State);
        Assert.Single(_interaction.Conflicts);
        Assert.Equal(["a.txt"], Entries("dst")); // no .a.txt.tandem-part, no .a.txt.tandem-old
        Assert.Equal("new content", await File.ReadAllTextAsync(_temp.Combine("dst/a.txt"), Ct));
    }

    [Fact]
    public async Task Cancel_inside_a_file_leaves_nothing_behind_and_the_source_intact()
    {
        var content = RandomBytes(BigSize);
        await File.WriteAllBytesAsync(_temp.Combine("src/big.bin"), content, Ct);
        CopyOperation? op = null;
        op = Copy("src/big.bin", new FirstBytesHook(NativeInBlocks(), () => op!.Cancel()));

        var result = await op.RunAsync().WaitAsync(Timeout, Ct);

        Assert.Equal(OperationState.Cancelled, result.State);
        Assert.Empty(Entries("dst"));
        Assert.Equal(content, await File.ReadAllBytesAsync(_temp.Combine("src/big.bin"), Ct));
    }

    [Fact]
    public async Task A_file_moved_to_another_volume_keeps_its_metadata_and_leaves_the_source()
    {
        using var volume = TinyVolume.Create(_temp.Combine("tiny.dmg"), _temp.Combine("mnt"));
        var mountPoint = RealPath(volume.MountPoint);
        var modified = new DateTime(2018, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        var source = _temp.Combine("src/a.txt");
        await File.WriteAllTextAsync(source, "moved", Ct);
        XAttr.Set(source, "user.tandem.test", "kept");
        File.SetLastWriteTimeUtc(source, modified);
        var op = new MoveOperation(_providers, [VfsPath.Local(source)], VfsPath.Local(mountPoint), new CopyOptions(),
            _interaction, Native());

        var result = await op.RunAsync().WaitAsync(Timeout, Ct);

        Assert.Equal(OperationState.Completed, result.State);
        Assert.False(File.Exists(source));
        var moved = Path.Combine(mountPoint, "a.txt");
        Assert.Equal("moved", await File.ReadAllTextAsync(moved, Ct));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(moved));
        Assert.Equal("kept", XAttr.Get(moved, "user.tandem.test"));
        Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(mountPoint).Select(Path.GetFileName),
            n => n!.StartsWith(".a.txt.tandem-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_Finder_locked_file_is_copied_and_the_copy_is_locked_too()
    {
        var modified = new DateTime(2019, 8, 7, 6, 5, 4, DateTimeKind.Utc);
        await File.WriteAllTextAsync(_temp.Combine("src/locked.txt"), "locked content", Ct);
        File.SetLastWriteTimeUtc(_temp.Combine("src/locked.txt"), modified);
        FileFlags.Add(_temp.Combine("src/locked.txt"), FileFlags.Locked);

        var result = await Copy("src/locked.txt", Native()).RunAsync().WaitAsync(Timeout, Ct);

        Assert.Equal(OperationState.Completed, result.State);
        Assert.Empty(_interaction.Errors);
        Assert.Empty(result.Skipped);
        Assert.Equal(["locked.txt"], Entries("dst"));
        Assert.Equal("locked content", await File.ReadAllTextAsync(_temp.Combine("dst/locked.txt"), Ct));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(_temp.Combine("dst/locked.txt")));
        Assert.NotEqual(0u, FileFlags.Get(_temp.Combine("dst/locked.txt")) & FileFlags.Locked);
    }

    [Fact]
    public async Task A_stale_locked_part_file_does_not_block_the_copy()
    {
        await File.WriteAllTextAsync(_temp.Combine("src/a.txt"), "fresh", Ct);
        await File.WriteAllTextAsync(_temp.Combine("dst/.a.txt.tandem-part"), "stale", Ct);
        FileFlags.Add(_temp.Combine("dst/.a.txt.tandem-part"), FileFlags.Locked);

        var result = await Copy("src/a.txt", Native()).RunAsync().WaitAsync(Timeout, Ct);

        Assert.Equal(OperationState.Completed, result.State);
        Assert.Empty(_interaction.Errors);
        Assert.Equal(["a.txt"], Entries("dst"));
        Assert.Equal("fresh", await File.ReadAllTextAsync(_temp.Combine("dst/a.txt"), Ct));
    }

    [Fact]
    public async Task A_folder_keeps_its_tags_and_extended_attributes()
    {
        var folder = Directory.CreateDirectory(_temp.Combine("src/tagged")).FullName;
        await File.WriteAllTextAsync(Path.Combine(folder, "inside.txt"), "inside", Ct);
        XAttr.Set(folder, "user.tandem.test", "folder attribute");
        _metadata.SetTags(folder, ["Green"]);

        var result = await Copy("src/tagged", Native()).RunAsync().WaitAsync(Timeout, Ct);

        Assert.Equal(OperationState.Completed, result.State);
        Assert.Equal(["tagged", "tagged/inside.txt"], Entries("dst"));
        Assert.Equal("folder attribute", XAttr.Get(_temp.Combine("dst/tagged"), "user.tandem.test"));
        Assert.Equal(["Green"], _metadata.GetTags(_temp.Combine("dst/tagged")));
        Assert.Equal("inside", await File.ReadAllTextAsync(_temp.Combine("dst/tagged/inside.txt"), Ct));
    }

    [Fact]
    public async Task A_folder_moved_to_another_volume_keeps_its_tags_and_extended_attributes()
    {
        // The image is mounted under the temp directory; the volume list names the mount by its real path.
        using var volume = TinyVolume.Create(_temp.Combine("tiny.dmg"), _temp.Combine("mnt"));
        var mountPoint = RealPath(volume.MountPoint);
        var folder = Directory.CreateDirectory(_temp.Combine("src/tagged")).FullName;
        await File.WriteAllTextAsync(Path.Combine(folder, "inside.txt"), "inside", Ct);
        XAttr.Set(folder, "user.tandem.test", "folder attribute");
        _metadata.SetTags(folder, ["Green"]);
        var op = new MoveOperation(_providers, [VfsPath.Local(folder)], VfsPath.Local(mountPoint), new CopyOptions(),
            _interaction, Native());

        var result = await op.RunAsync().WaitAsync(Timeout, Ct);

        Assert.Equal(OperationState.Completed, result.State);
        Assert.Empty(_interaction.Errors);
        Assert.False(Directory.Exists(folder)); // really moved, by copying: the source is gone
        var moved = Path.Combine(mountPoint, "tagged");
        Assert.Equal("inside", await File.ReadAllTextAsync(Path.Combine(moved, "inside.txt"), Ct));
        Assert.Equal("folder attribute", XAttr.Get(moved, "user.tandem.test"));
        Assert.Equal(["Green"], _metadata.GetTags(moved));
    }

    [Fact]
    public async Task Pause_inside_a_file_stops_the_bytes_and_resume_completes_the_copy()
    {
        var content = RandomBytes(BigSize);
        await File.WriteAllBytesAsync(_temp.Combine("src/big.bin"), content, Ct);
        CopyOperation? op = null;
        op = Copy("src/big.bin", new FirstBytesHook(NativeInBlocks(), () => op!.Pause()));
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        op.Changed += (_, _) =>
        {
            if (op.State == OperationState.Paused) paused.TrySetResult();
        };

        var run = op.RunAsync();
        await paused.Task.WaitAsync(Timeout, Ct);
        var bytesWhenPaused = op.Progress.BytesDone;
        await Task.Delay(300, Ct);

        Assert.Equal(OperationState.Paused, op.State);
        Assert.Equal(bytesWhenPaused, op.Progress.BytesDone);
        Assert.InRange(bytesWhenPaused, 1, BigSize - 1); // really inside the file
        Assert.Equal([".big.bin.tandem-part"], Entries("dst"));
        Assert.False(run.IsCompleted);

        op.Resume();
        var result = await run.WaitAsync(Timeout, Ct);

        Assert.Equal(OperationState.Completed, result.State);
        Assert.Equal(["big.bin"], Entries("dst"));
        Assert.Equal(content, await File.ReadAllBytesAsync(_temp.Combine("dst/big.bin"), Ct));
        Assert.Equal(op.Progress.BytesTotal, op.Progress.BytesDone);
    }

    [Fact]
    public async Task Cancel_while_paused_inside_a_file_ends_cancelled_without_leftovers()
    {
        var content = RandomBytes(BigSize);
        await File.WriteAllBytesAsync(_temp.Combine("src/big.bin"), content, Ct);
        CopyOperation? op = null;
        op = Copy("src/big.bin", new FirstBytesHook(NativeInBlocks(), () => op!.Pause()));
        op.Changed += (_, _) =>
        {
            if (op.State == OperationState.Paused) op.Cancel();
        };

        var result = await op.RunAsync().WaitAsync(Timeout, Ct);

        Assert.Equal(OperationState.Cancelled, result.State);
        Assert.Empty(Entries("dst"));
        Assert.Equal(content, await File.ReadAllBytesAsync(_temp.Combine("src/big.bin"), Ct));
    }

    /// <summary>The real copy, with <c>onFirstBytes</c> run (on copyfile's thread, synchronously) when it first
    /// reports copied bytes: a pause or cancel requested there reaches the copy inside the file, however fast the
    /// disk is.</summary>
    private sealed class FirstBytesHook(INativeCopy inner, Action onFirstBytes) : INativeCopy
    {
        public Task CopyFileAsync(string sourcePath, string destinationPath, IProgress<long> bytesCopied,
            Func<CancellationToken, ValueTask> pauseCheckpoint, CancellationToken ct) =>
            inner.CopyFileAsync(sourcePath, destinationPath, new Hook(bytesCopied, onFirstBytes), pauseCheckpoint, ct);

        public Task CopyFlagsAsync(string sourcePath, string destinationPath, CancellationToken ct) =>
            inner.CopyFlagsAsync(sourcePath, destinationPath, ct);

        public Task CopyDirectoryMetadataAsync(string sourcePath, string destinationPath, CancellationToken ct) =>
            inner.CopyDirectoryMetadataAsync(sourcePath, destinationPath, ct);

        private sealed class Hook(IProgress<long> inner, Action onFirstBytes) : IProgress<long>
        {
            private int _fired;

            public void Report(long value)
            {
                inner.Report(value);
                if (value > 0 && Interlocked.Exchange(ref _fired, 1) == 0) onFirstBytes();
            }
        }
    }
}
