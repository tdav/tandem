using System.Text;
using Avalonia.Headless.XUnit;
using Tandem.App.Tests.Views;
using Tandem.App.ViewModels.Editing;
using Tandem.Core.Operations;
using Tandem.Core.Viewing;
using Tandem.Core.Vfs;
using Tandem.Core.Vfs.InMemory;

namespace Tandem.App.Tests.ViewModels;

/// <summary>The built-in editor's model over in-memory providers: opening with the detected encoding, refusals, and
/// saving through the provider's batch and the safe replace (part file, then rename).</summary>
[Collection(LanguageCollection.Name)]
public class EditorViewModelTests : LanguageTestBase
{
    private const string Path = "/dir/a.txt";
    private const string Part = "/dir/.a.txt" + SafeReplace.PartSuffix;

    private static readonly VfsPath File = new("mem", Path);

    private static readonly Encoding Cp1251 = TextEncodings.Windows1251.Encoding;

    private static (ProviderRegistry Providers, InMemoryFileSystem Fs) Setup(byte[] content)
    {
        var fs = new InMemoryFileSystem("mem");
        fs.AddFile(Path, content);
        var providers = new ProviderRegistry();
        providers.Register(fs);
        return (providers, fs);
    }

    private static byte[] Bytes(InMemoryFileSystem fs, string path)
    {
        using var stream = fs.OpenReadAsync(new VfsPath(fs.Id, path), CancellationToken.None).Result;
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private static Task<EditorViewModel> OpenAsync(ProviderRegistry providers, VfsPath? file = null) =>
        EditorViewModel.OpenAsync(providers, file ?? File, CancellationToken.None);

    private static byte[] Concat(byte[] head, byte[] tail) => [.. head, .. tail];

    [AvaloniaFact]
    public async Task Opens_utf8_and_windows1251_text_with_detected_encoding()
    {
        var (providers, fs) = Setup(Encoding.UTF8.GetBytes("Привет, мир"));
        fs.AddFile("/dir/b.txt", Cp1251.GetBytes("Привет, мир! Это текст в кодировке Windows-1251."));

        var utf8 = await OpenAsync(providers);
        var cp1251 = await OpenAsync(providers, new VfsPath("mem", "/dir/b.txt"));

        Assert.Equal("Привет, мир", utf8.Document.Text);
        Assert.Equal("utf-8", utf8.Encoding.Id);
        Assert.False(utf8.HasBom);
        Assert.False(utf8.IsModified);
        Assert.False(utf8.IsReadOnly);
        Assert.Null(utf8.Error);
        Assert.Equal(Path, utf8.Title);
        Assert.Equal(File, utf8.File);
        Assert.Equal("Привет, мир! Это текст в кодировке Windows-1251.", cp1251.Document.Text);
        Assert.Equal("windows-1251", cp1251.Encoding.Id);
    }

    public static TheoryData<string, byte[]> Files() => new()
    {
        { "utf-8 with BOM", Concat([0xEF, 0xBB, 0xBF], Encoding.UTF8.GetBytes("Привет\nмир\n")) },
        { "utf-8", Encoding.UTF8.GetBytes("Привет\nмир\n") },
        { "utf-16le with BOM", Concat([0xFF, 0xFE], Encoding.Unicode.GetBytes("Привет\r\nмир")) },
        { "windows-1251", Cp1251.GetBytes("Привет, мир! Это текст в кодировке Windows-1251.\r\n") },
        { "crlf", Encoding.ASCII.GetBytes("one\r\ntwo\r\nthree\r\n") },
        { "lf", Encoding.ASCII.GetBytes("one\ntwo\nthree") },
    };

    [AvaloniaTheory]
    [MemberData(nameof(Files))]
    public async Task Unmodified_save_writes_the_same_bytes(string kind, byte[] content)
    {
        var (providers, fs) = Setup(content);
        var editor = await OpenAsync(providers);

        Assert.True(await editor.SaveAsync(), kind);

        Assert.Equal(content, Bytes(fs, Path));
        Assert.Equal([Path], fs.AllPaths.Where(p => p.StartsWith("/dir/", StringComparison.Ordinal)));
    }

    [AvaloniaFact]
    public async Task Save_keeps_encoding_and_bom()
    {
        var (providers, fs) = Setup(Cp1251.GetBytes("Привет, мир! Это текст в кодировке Windows-1251."));
        fs.AddFile("/dir/bom.txt", Concat([0xEF, 0xBB, 0xBF], Encoding.UTF8.GetBytes("с меткой")));
        fs.AddFile("/dir/plain.txt", Encoding.UTF8.GetBytes("без метки €"));

        var cp1251 = await OpenAsync(providers);
        var bom = await OpenAsync(providers, new VfsPath("mem", "/dir/bom.txt"));
        var plain = await OpenAsync(providers, new VfsPath("mem", "/dir/plain.txt"));
        foreach (var editor in new[] { cp1251, bom, plain })
        {
            editor.Document.Insert(0, "Ж");
            Assert.True(await editor.SaveAsync());
        }

        Assert.Equal(Cp1251.GetBytes("ЖПривет, мир! Это текст в кодировке Windows-1251."), Bytes(fs, Path));
        Assert.True(bom.HasBom);
        Assert.Equal(Concat([0xEF, 0xBB, 0xBF], Encoding.UTF8.GetBytes("Жс меткой")), Bytes(fs, "/dir/bom.txt"));
        Assert.False(plain.HasBom);
        Assert.Equal(Encoding.UTF8.GetBytes("Жбез метки €"), Bytes(fs, "/dir/plain.txt"));
    }

    [AvaloniaFact]
    public async Task Save_keeps_crlf_line_endings()
    {
        var (providers, fs) = Setup(Encoding.ASCII.GetBytes("one\r\ntwo\r\n"));
        var editor = await OpenAsync(providers);

        editor.Document.Insert(editor.Document.Lines[1].Offset, "2: ");
        Assert.True(await editor.SaveAsync());

        Assert.Equal("one\r\n2: two\r\n", fs.ReadAllText(Path));
    }

    [AvaloniaFact]
    public async Task Save_keeps_unix_mode()
    {
        var (providers, fs) = Setup(Encoding.ASCII.GetBytes("#!/bin/sh\n"));
        const UnixFileMode executable = (UnixFileMode)0b111_101_101; // 0o755
        await fs.SetModeAsync(File, executable, CancellationToken.None);
        var editor = await OpenAsync(providers);

        editor.Document.Insert(editor.Document.TextLength, "echo hi\n");
        Assert.True(await editor.SaveAsync());

        Assert.Equal("#!/bin/sh\necho hi\n", fs.ReadAllText(Path));
        Assert.Equal(executable, (await fs.GetEntryAsync(File, CancellationToken.None))!.Mode);
    }

    [AvaloniaFact]
    public async Task Failed_save_keeps_original_and_reports_error()
    {
        var (providers, fs) = Setup(Encoding.ASCII.GetBytes("original"));
        fs.FailWriteAfter(Part, 2, () => new IOException("disk full"));
        var editor = await OpenAsync(providers);
        editor.Document.Insert(0, "changed ");

        Assert.False(await editor.SaveAsync());

        Assert.Equal("original", fs.ReadAllText(Path));
        Assert.False(fs.Exists(Part));
        Assert.True(editor.IsModified);
        Assert.Equal("Could not save “a.txt”: an item could not be read or written.", editor.Error);
    }

    /// <summary>Each step of a save that can fail: writing the part file, giving it the mode, setting the original
    /// aside, and moving the part into place.</summary>
    public static TheoryData<string> FailingSteps() => ["OpenWrite", "SetMode", "Move original", "Move part"];

    [AvaloniaTheory]
    [MemberData(nameof(FailingSteps))]
    public async Task Save_failing_at_any_step_keeps_the_original(string step)
    {
        var (providers, fs) = Setup(Encoding.ASCII.GetBytes("original"));
        await fs.SetModeAsync(File, (UnixFileMode)0b110_100_000, CancellationToken.None); // 0o640
        fs.FailOn((op, path) => step switch
        {
            "OpenWrite" => op == "OpenWrite" && path.Path == Part,
            "SetMode" => op == "SetMode" && path.Path == Part,
            "Move original" => op == "Move" && path.Path == Path,
            _ => op == "Move" && path.Path == Part,
        }, () => new IOException("injected"));
        var editor = await OpenAsync(providers);
        editor.Document.Insert(0, "changed ");

        Assert.False(await editor.SaveAsync());

        Assert.Equal("original", fs.ReadAllText(Path));
        Assert.Equal([Path], fs.AllPaths.Where(p => p.StartsWith("/dir/", StringComparison.Ordinal)));
        Assert.True(editor.IsModified);
        Assert.NotNull(editor.Error);
    }

    [AvaloniaFact]
    public async Task Successful_save_clears_the_error_and_the_modified_mark()
    {
        var (providers, fs) = Setup(Encoding.ASCII.GetBytes("original"));
        fs.FailWriteAfter(Part, 0, () => new IOException("disk full"));
        var editor = await OpenAsync(providers);
        var changed = new List<string?>();
        editor.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        editor.Document.Insert(0, "new ");
        Assert.True(editor.IsModified);
        Assert.Equal(Path + " *", editor.Title);
        Assert.Contains(nameof(EditorViewModel.IsModified), changed);
        Assert.Contains(nameof(EditorViewModel.Title), changed);
        Assert.False(await editor.SaveAsync());
        Assert.NotNull(editor.Error);

        fs.FailWriteAfter(Part, long.MaxValue, () => new IOException("never"));
        Assert.True(await editor.SaveAsync());

        Assert.Null(editor.Error);
        Assert.False(editor.IsModified);
        Assert.Equal(Path, editor.Title);
        Assert.Equal("new original", fs.ReadAllText(Path));
    }

    [AvaloniaFact]
    public async Task File_over_32_MiB_is_refused()
    {
        var (providers, _) = Setup(new byte[EditorViewModel.MaxFileBytes + 1]);

        var refused = await Assert.ThrowsAsync<EditorRefusedException>(() => OpenAsync(providers));

        Assert.Equal(EditorRefusal.TooLarge, refused.Reason);
        Assert.Equal(32 * 1024 * 1024, EditorViewModel.MaxFileBytes);
    }

    [AvaloniaFact]
    public async Task Binary_file_is_refused()
    {
        var (providers, _) = Setup([0x7F, 0x45, 0x4C, 0x46, 0x02, 0x01, 0x01, 0x00, 0x00, 0x00]);

        var refused = await Assert.ThrowsAsync<EditorRefusedException>(() => OpenAsync(providers));

        Assert.Equal(EditorRefusal.Binary, refused.Reason);
    }

    [AvaloniaFact]
    public async Task Missing_file_fails_to_open_with_not_found()
    {
        var (providers, _) = Setup([]);

        await Assert.ThrowsAsync<VfsNotFoundException>(() => OpenAsync(providers, new VfsPath("mem", "/dir/gone")));
    }

    [AvaloniaFact]
    public async Task Provider_without_write_opens_read_only()
    {
        var fs = new InMemoryFileSystem("mem");
        fs.AddFile(Path, "text");
        var providers = new ProviderRegistry();
        providers.Register(new CapabilityProvider(fs, ProviderCapabilities.None, ProviderCapabilities.Write));

        var editor = await OpenAsync(providers);

        Assert.True(editor.IsReadOnly);
        Assert.Equal("text", editor.Document.Text);
        Assert.False(await editor.SaveAsync());
        Assert.NotNull(editor.Error);
        Assert.Equal([Path], fs.AllPaths.Where(p => p.StartsWith("/dir/", StringComparison.Ordinal)));
    }

    [AvaloniaFact]
    public async Task Save_inside_batching_provider_commits_once()
    {
        var fs = new InMemoryFileSystem("arc");
        fs.AddFile(Path, "inside");
        var batching = new JournalingBatchProvider(fs);
        var providers = new ProviderRegistry();
        providers.Register(batching);
        var file = new VfsPath("arc", Path);
        var editor = await OpenAsync(providers, file);

        editor.Document.Insert(0, "edited ");
        Assert.True(await editor.SaveAsync());

        Assert.Equal("edited inside", fs.ReadAllText(Path));
        Assert.Equal(1, batching.Journal.Count(e => e == "Begin"));
        Assert.Equal(1, batching.Journal.Count(e => e == "Commit"));
        var (begin, commit) = (batching.Journal.IndexOf("Begin"), batching.Journal.IndexOf("Commit"));
        var changes = batching.Journal.Select((e, i) => (e, i)).Where(x => x.e.StartsWith("Write") ||
            x.e.StartsWith("Move")).ToList();
        Assert.NotEmpty(changes);
        Assert.All(changes, x => Assert.InRange(x.i, begin + 1, commit - 1));
        Assert.Equal("Dispose", batching.Journal[^1]);
    }

    [AvaloniaFact]
    public async Task Hosted_file_shows_its_display_path_and_saves_into_the_provider()
    {
        var local = new InMemoryFileSystem(VfsPath.LocalProviderId);
        local.AddFile("/home/data.pack", "packed");
        var hosted = new FakeHosted(local);
        hosted.Files.AddFile("/docs/a.txt", "hosted text");
        var providers = new ProviderRegistry();
        providers.Register(local);
        providers.Register(hosted);
        var pack = (await local.GetEntryAsync(VfsPath.Local("/home/data.pack"), CancellationToken.None))!;
        await hosted.TryMountAsync(pack, byContent: false, CancellationToken.None);

        var editor = await OpenAsync(providers, new VfsPath(FakeHosted.ProviderId, "/docs/a.txt"));
        editor.Document.Insert(0, "more ");
        Assert.True(await editor.SaveAsync());

        Assert.Equal("/home/data.pack/docs/a.txt", editor.Title);
        Assert.Equal("more hosted text", hosted.Files.ReadAllText("/docs/a.txt"));
        Assert.Equal(["/docs", "/docs/a.txt"], hosted.Files.AllPaths);
    }

    /// <summary>A provider that takes batches over an in-memory file system and journals the batch's steps and the
    /// changes made (writes and moves), all passed straight through.</summary>
    private sealed class JournalingBatchProvider(InMemoryFileSystem inner) : IFileSystemProvider, IBatchProvider
    {
        public List<string> Journal { get; } = [];

        public IBatchProvider? BatchHost => null;

        public Task<IProviderBatch> BeginBatchAsync(CancellationToken ct)
        {
            Add("Begin");
            return Task.FromResult<IProviderBatch>(new Batch(this));
        }

        private void Add(string entry)
        {
            lock (Journal) Journal.Add(entry);
        }

        private sealed class Batch(JournalingBatchProvider owner) : IProviderBatch
        {
            public Task PrepareReadAsync(IReadOnlyList<VfsPath> files, CancellationToken ct) => Task.CompletedTask;

            public Task CommitAsync(CancellationToken ct)
            {
                owner.Add("Commit");
                return Task.CompletedTask;
            }

            public ValueTask DisposeAsync()
            {
                owner.Add("Dispose");
                return ValueTask.CompletedTask;
            }
        }

        public string Id => inner.Id;
        public ProviderCapabilities Capabilities => inner.Capabilities;
        public IAsyncEnumerable<FileEntry> EnumerateAsync(VfsPath directory, CancellationToken ct) =>
            inner.EnumerateAsync(directory, ct);
        public Task<FileEntry?> GetEntryAsync(VfsPath path, CancellationToken ct) => inner.GetEntryAsync(path, ct);
        public Task<Stream> OpenReadAsync(VfsPath path, CancellationToken ct) => inner.OpenReadAsync(path, ct);
        public Task<Stream> OpenWriteAsync(VfsPath path, CancellationToken ct)
        {
            Add("Write " + path.Path);
            return inner.OpenWriteAsync(path, ct);
        }
        public Task CreateDirectoryAsync(VfsPath path, CancellationToken ct) => inner.CreateDirectoryAsync(path, ct);
        public Task DeleteAsync(VfsPath path, bool recursive, CancellationToken ct) =>
            inner.DeleteAsync(path, recursive, ct);
        public Task MoveAsync(VfsPath source, VfsPath destination, CancellationToken ct)
        {
            Add($"Move {source.Path} {destination.Path}");
            return inner.MoveAsync(source, destination, ct);
        }
        public Task CreateSymlinkAsync(VfsPath path, string target, CancellationToken ct) =>
            inner.CreateSymlinkAsync(path, target, ct);
        public Task SetModifiedAsync(VfsPath path, DateTimeOffset modified, CancellationToken ct) =>
            inner.SetModifiedAsync(path, modified, ct);
        public Task SetModeAsync(VfsPath path, UnixFileMode mode, CancellationToken ct) =>
            inner.SetModeAsync(path, mode, ct);
        public Task<bool> IsSameEntryAsync(VfsPath a, VfsPath b, CancellationToken ct) =>
            inner.IsSameEntryAsync(a, b, ct);
        public Task<string> GetVolumeKeyAsync(VfsPath path, CancellationToken ct) => inner.GetVolumeKeyAsync(path, ct);
        public IDisposable? Watch(VfsPath directory, Action changed) => inner.Watch(directory, changed);
    }
}
