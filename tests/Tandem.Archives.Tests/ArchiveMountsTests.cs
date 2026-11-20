using System.IO.Compression;
using System.Runtime.Versioning;
using System.Text;
using Tandem.Core.Operations;
using Tandem.Core.Tests.Operations;
using Tandem.Core.Tests.Vfs;
using Tandem.Core.Vfs;

namespace Tandem.Archives.Tests;

/// <summary>Opening archive files as folders: by name and by content, once per archive, inside other archives, and
/// with passwords asked at mount (encrypted listing) or at the first read (encrypted entries).</summary>
[SupportedOSPlatform("macos")]
public sealed class ArchiveMountsTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static readonly DateTimeOffset HostTime = new(2025, 3, 4, 5, 6, 7, TimeSpan.Zero);
    private static readonly byte[] DataBin = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();

    private readonly CountingHostProvider _host = new();
    private readonly ProviderRegistry _providers = new();
    private readonly TempDir _tempDir = new();
    private readonly TempFileStore _temp;
    private readonly ScriptedPasswordPrompt _prompt = new();
    private readonly ArchiveMounts _mounts;

    public ArchiveMountsTests()
    {
        _temp = new TempFileStore(_tempDir.Combine("store"));
        _providers.Register(_host);
        _mounts = new ArchiveMounts(_providers, _temp, _prompt);
    }

    public void Dispose()
    {
        _mounts.Dispose();
        _temp.Dispose();
        _tempDir.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static VfsPath Mem(string path) => new("mem", path);

    private VfsPath AddFixture(string fixture, string? hostPath = null)
    {
        var path = hostPath ?? "/arc/" + fixture;
        _host.Files.AddFile(path, Fixture.Bytes(fixture), HostTime);
        return Mem(path);
    }

    private async Task<FileEntry> EntryAsync(VfsPath path) =>
        (await _providers.For(path).GetEntryAsync(path, Ct)) ?? throw new FileNotFoundException(path.Path);

    private async Task<List<FileEntry>> ListAsync(VfsPath directory)
    {
        var entries = new List<FileEntry>();
        await foreach (var entry in _providers.For(directory).EnumerateAsync(directory, Ct)) entries.Add(entry);
        return entries;
    }

    private async Task<byte[]> ReadAsync(VfsPath path)
    {
        await using var stream = await _providers.For(path).OpenReadAsync(path, Ct);
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy, Ct);
        return copy.ToArray();
    }

    private static VfsPath At(VfsPath root, string relative) => root with { Path = "/" + relative };

    private static void AssertArchiveRoot(VfsPath? root)
    {
        Assert.NotNull(root);
        Assert.StartsWith("archive:", root.Value.ProviderId, StringComparison.Ordinal);
        Assert.True(root.Value.IsRoot);
    }

    /// <summary>Mounts that open archives through <paramref name="open"/> instead of the provider itself: an
    /// archive whose listing is encrypted, with no such fixture at hand.</summary>
    private ArchiveMounts MountsOpeningWith(ArchiveMounts.ArchiveOpener open) =>
        new(_providers, _temp, _prompt, open);

    /// <summary>An opener that behaves like a header-encrypted archive with password <paramref name="right"/>:
    /// without a password it asks for one, with another one it fails as <paramref name="wrong"/> says, with the
    /// right one it opens <c>sample.zip</c> (placed at the archive's path) and records the providers it made.
    /// </summary>
    private sealed class EncryptedListing(ArchiveMountsTests test, string right, Func<VfsPath, Exception>? wrong = null)
    {
        public List<string?> Passwords { get; } = [];
        public List<ArchiveProvider> Opened { get; } = [];

        /// <summary>Every open waits for it.</summary>
        public TaskCompletionSource? Gate { get; init; }

        /// <summary>Opens with a password wait for it.</summary>
        public TaskCompletionSource? PasswordGate { get; init; }

        /// <summary>Set when the first open has begun.</summary>
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Set when the first open with a password has begun.</summary>
        public TaskCompletionSource EnteredWithPassword { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ArchiveProvider> OpenAsync(string id, VfsPath archive, string? password,
            CancellationToken ct)
        {
            lock (Passwords) Passwords.Add(password);
            Entered.TrySetResult();
            if (password is not null) EnteredWithPassword.TrySetResult();
            if (Gate is not null) await Gate.Task.WaitAsync(ct);
            if (password is not null && PasswordGate is not null) await PasswordGate.Task.WaitAsync(ct);
            if (password is null) throw new ArchivePasswordRequiredException(archive, wrongPassword: false);
            if (password != right)
                throw wrong?.Invoke(archive) ?? new ArchivePasswordRequiredException(archive, wrongPassword: true);
            var provider = await ArchiveProvider.OpenAsync(id, test._providers, archive, test._temp, password,
                test._prompt, ct);
            lock (Opened) Opened.Add(provider);
            return provider;
        }
    }

    // ---- Mounting by name and by content ----------------------------------------------------------------

    [Fact]
    public async Task Zip_by_name_mounts_and_lists_its_root()
    {
        var archive = AddFixture("sample.zip");

        var root = await _mounts.TryMountAsync(await EntryAsync(archive), byContent: false, Ct);

        AssertArchiveRoot(root);
        Assert.Equal(["a.txt", "docs", "empty", "привет.txt"],
            (await ListAsync(root!.Value)).Select(e => e.Name).Order(StringComparer.Ordinal));
        Assert.Equal(new HostLocation(Mem("/arc"), "sample.zip"), _providers.UpOf(root.Value));
        Assert.Empty(_prompt.Questions);
    }

    [Fact]
    public async Task Same_archive_is_mounted_once()
    {
        var archive = AddFixture("sample.zip");

        var first = await _mounts.MountAsync(archive, Ct);
        var again = await _mounts.TryMountAsync(await EntryAsync(archive), byContent: false, Ct);
        var byContent = await _mounts.TryMountAsync(await EntryAsync(archive), byContent: true, Ct);

        Assert.Equal(first, again);
        Assert.Equal(first, byContent);
        Assert.Equal(1, _host.ReadsOf("/arc/sample.zip"));
        Assert.Equal("a"u8.ToArray(), await ReadAsync(first.Combine("a.txt")));
    }

    [Fact]
    public async Task Racing_mounts_of_one_archive_open_and_register_it_once()
    {
        var archive = AddFixture("sample.zip");
        var opener = new EncryptedListing(this, right: "pw") { Gate = new TaskCompletionSource() };
        _prompt.Answer("pw");
        using var mounts = MountsOpeningWith(opener.OpenAsync);

        var racing = Enumerable.Range(0, 8).Select(_ => Task.Run(() => mounts.MountAsync(archive, Ct), Ct)).ToList();
        await Task.Delay(50, Ct);
        opener.Gate.SetResult();
        var roots = await Task.WhenAll(racing).WaitAsync(Timeout, Ct);

        Assert.Single(roots.Distinct());
        Assert.Equal([null, "pw"], opener.Passwords);
        Assert.Single(opener.Opened);
        Assert.Same(opener.Opened[0], _providers.Get(roots[0].ProviderId));
    }

    [Fact]
    public async Task Non_archive_name_is_not_mounted_by_name()
    {
        _host.Files.AddFile("/arc/a.txt", "a"u8.ToArray(), HostTime);
        AddFixture("sample.zip", "/arc/a.docx");
        _host.Files.AddDirectory("/arc/folder.zip");

        Assert.Null(await _mounts.TryMountAsync(await EntryAsync(Mem("/arc/a.txt")), byContent: false, Ct));
        Assert.Null(await _mounts.TryMountAsync(await EntryAsync(Mem("/arc/a.docx")), byContent: false, Ct));
        Assert.Null(await _mounts.TryMountAsync(await EntryAsync(Mem("/arc/folder.zip")), byContent: false, Ct));
        Assert.Null(await _mounts.TryMountAsync(await EntryAsync(Mem("/arc/folder.zip")), byContent: true, Ct));
        Assert.Equal(0, _host.ReadsOf("/arc/a.docx"));
        Assert.True(_mounts.IsArchiveName("x.TAR.GZ"));
        Assert.False(_mounts.IsArchiveName("x.docx"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Zip_named_docx_mounts_by_content_and_text_file_does_not(bool unseekableHost)
    {
        AddFixture("sample.zip", "/arc/report.docx");
        AddFixture("sample.tar.gz", "/arc/data.bin");
        _host.Files.AddFile("/arc/notes.txt", "just text"u8.ToArray(), HostTime);
        AddFixture("broken.zip", "/arc/broken.dat");
        _host.Unseekable = unseekableHost;

        var docx = await _mounts.TryMountAsync(await EntryAsync(Mem("/arc/report.docx")), byContent: true, Ct);
        var tarGz = await _mounts.TryMountAsync(await EntryAsync(Mem("/arc/data.bin")), byContent: true, Ct);
        var text = await _mounts.TryMountAsync(await EntryAsync(Mem("/arc/notes.txt")), byContent: true, Ct);

        AssertArchiveRoot(docx);
        Assert.Contains("docs", (await ListAsync(docx!.Value)).Select(e => e.Name));
        AssertArchiveRoot(tarGz);
        Assert.Equal("hello\n"u8.ToArray(), await ReadAsync(At(tarGz!.Value, "docs/readme.txt")));
        Assert.Null(text);
        // A zip signature over damaged content is recognised; mounting it then reports the damage.
        await Assert.ThrowsAsync<ArchiveDamagedException>(async () =>
            await _mounts.TryMountAsync(await EntryAsync(Mem("/arc/broken.dat")), byContent: true, Ct));
    }

    [Fact]
    public async Task Damaged_archive_by_name_throws_damaged()
    {
        var archive = AddFixture("broken.zip", "/arc/b.zip");
        _host.Files.AddFile("/arc/t.zip", "not a zip"u8.ToArray(), HostTime);

        await Assert.ThrowsAsync<ArchiveDamagedException>(async () =>
            await _mounts.TryMountAsync(await EntryAsync(archive), byContent: false, Ct));
        await Assert.ThrowsAsync<ArchiveDamagedException>(() => _mounts.MountAsync(Mem("/arc/t.zip"), Ct));
        await Assert.ThrowsAsync<VfsNotFoundException>(() => _mounts.MountAsync(Mem("/arc/none.zip"), Ct));

        // Nothing stays mounted: the repaired file mounts afresh.
        Assert.False(_providers.TryGet("archive:1", out _));
        Assert.False(_providers.TryGet("archive:2", out _));
        _host.Files.AddFile("/arc/b.zip", Fixture.Bytes("sample.zip"), HostTime.AddMinutes(1));
        var root = await _mounts.MountAsync(archive, Ct);
        Assert.Equal(4, (await ListAsync(root)).Count);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_temp.Directory));
    }

    // ---- Passwords at mount (encrypted listing) ---------------------------------------------------------

    [Fact]
    public async Task Encrypted_7z_asks_for_password_at_mount()
    {
        var bytes = Fixture.Bytes("encrypted.7z"); // skipped while the external fixture is absent
        _host.Files.AddFile("/arc/encrypted.7z", bytes, HostTime);
        _prompt.Answer("wrong", Fixture.External7zPassword);

        var root = await _mounts.MountAsync(Mem("/arc/encrypted.7z"), Ct);

        Assert.Equal([(Mem("/arc/encrypted.7z"), false), (Mem("/arc/encrypted.7z"), true)], _prompt.Questions);
        var files = new List<FileEntry>();
        var pending = new Stack<VfsPath>([root]);
        while (pending.TryPop(out var folder))
            foreach (var entry in await ListAsync(folder))
                if (entry.Kind == EntryKind.Directory) pending.Push(entry.Path);
                else files.Add(entry);
        Assert.NotEmpty(files);
        foreach (var file in files) Assert.Equal(file.Size, (await ReadAsync(file.Path)).LongLength);
        Assert.Equal(2, _prompt.Questions.Count); // the password found at mount serves the reads
    }

    [Fact]
    public async Task Right_password_at_mount_opens_the_listing_with_it()
    {
        var archive = AddFixture("sample.zip", "/arc/locked.7z");
        var opener = new EncryptedListing(this, right: "pw");
        _prompt.Answer("pw");
        using var mounts = MountsOpeningWith(opener.OpenAsync);

        var root = await mounts.MountAsync(archive, Ct);

        Assert.Equal([(archive, false)], _prompt.Questions);
        Assert.Equal([null, "pw"], opener.Passwords);
        var provider = Assert.IsType<ArchiveProvider>(_providers.Get(root.ProviderId));
        Assert.Same(opener.Opened.Single(), provider);
        Assert.Equal("pw", provider.Password);
        Assert.Equal(4, (await ListAsync(root)).Count);
        Assert.Equal(root, await mounts.MountAsync(archive, Ct)); // asked once
        Assert.Single(_prompt.Questions);
    }

    [Fact]
    public async Task Wrong_password_is_asked_again_and_fails_after_three()
    {
        var archive = AddFixture("sample.zip", "/arc/locked.7z");
        var opener = new EncryptedListing(this, right: "pw");
        _prompt.Answer("x", "y", "z");
        using var mounts = MountsOpeningWith(opener.OpenAsync);

        var error = await Assert.ThrowsAsync<ArchivePasswordRequiredException>(() => mounts.MountAsync(archive, Ct));

        Assert.Equal(3, ArchiveMounts.MaxPasswordAttempts);
        Assert.Equal([false, true, true], _prompt.PreviousWasWrong);
        Assert.Equal([null, "x", "y", "z"], opener.Passwords);
        Assert.True(error.WrongPassword);
        Assert.Equal(archive, error.Archive);
        Assert.Empty(opener.Opened);
        Assert.False(_providers.TryGet("archive:1", out _));
    }

    /// <summary>A 7z with an encrypted header fails to decode, not to decrypt, under a wrong password (SharpCompress
    /// reports a CRC mismatch): once the archive has asked for a password, damage under a password counts as a
    /// wrong one.</summary>
    [Fact]
    public async Task Damage_under_a_password_after_asking_counts_as_a_wrong_password()
    {
        var archive = AddFixture("sample.zip", "/arc/locked.7z");
        var opener = new EncryptedListing(this, right: "pw",
            wrong: a => new ArchiveDamagedException($"'{a.Path}' is damaged: CRC mismatch."));
        _prompt.Answer("x", "pw");
        using var mounts = MountsOpeningWith(opener.OpenAsync);

        var root = await mounts.MountAsync(archive, Ct);

        Assert.Equal([false, true], _prompt.PreviousWasWrong);
        Assert.Equal(4, (await ListAsync(root)).Count);
    }

    [Fact]
    public async Task Declined_prompt_cancels_mount_and_fails_a_read_as_item_error()
    {
        // At mount: the listing needs the password, the user declines.
        var locked = AddFixture("sample.zip", "/arc/locked.7z");
        var opener = new EncryptedListing(this, right: "pw");
        _prompt.Answer((string?)null);
        using (var mounts = MountsOpeningWith(opener.OpenAsync))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => mounts.MountAsync(locked, Ct));
        Assert.Equal([null], opener.Passwords);
        Assert.False(_providers.TryGet("archive:1", out _));

        // At a read: the listing is open, the entry needs the password; declining fails that read only.
        var archive = AddFixture("encrypted.zip");
        var root = await _mounts.MountAsync(archive, Ct);
        _prompt.Answer((string?)null, (string?)null);
        var declined = await Assert.ThrowsAsync<ArchivePasswordRequiredException>(() =>
            ReadAsync(root.Combine("a.txt")));
        Assert.Equal(archive, declined.Archive);

        _host.Files.AddDirectory("/dst");
        var interaction = new ScriptedInteraction().OnError(ErrorAction.Skip);
        var op = new CopyOperation(_providers, [root.Combine("a.txt")], Mem("/dst"), new CopyOptions(), interaction);
        var result = await op.RunAsync().WaitAsync(Timeout, Ct);

        Assert.Equal(OperationState.Completed, result.State);
        Assert.IsType<ArchivePasswordRequiredException>(Assert.Single(interaction.Errors).Error);
        Assert.Null(await _host.GetEntryAsync(Mem("/dst/a.txt"), Ct));
        Assert.Equal([false, false, false], _prompt.PreviousWasWrong); // mount, read, copy: each declined
    }

    [Fact]
    public async Task Declined_read_password_is_not_asked_again_in_the_same_operation()
    {
        var archive = AddFixture("encrypted.zip");
        var root = await _mounts.MountAsync(archive, Ct);
        _host.Files.AddDirectory("/dst");
        _prompt.Answer((string?)null);
        var interaction = new ScriptedInteraction()
            .OnError(ErrorAction.Skip).OnError(ErrorAction.Skip).OnError(ErrorAction.Skip);
        var sources = new[] { root.Combine("a.txt"), At(root, "docs/readme.txt"), root.Combine("привет.txt") };
        var op = new CopyOperation(_providers, sources, Mem("/dst"), new CopyOptions(), interaction);

        var result = await op.RunAsync().WaitAsync(Timeout, Ct);

        Assert.Equal(OperationState.Completed, result.State);
        Assert.Single(_prompt.Questions); // one decline covers the operation
        Assert.Equal(3, interaction.Errors.Count);
        Assert.All(interaction.Errors, e => Assert.IsType<ArchivePasswordRequiredException>(e.Error));
        Assert.Equal(["/dst"], _host.Files.AllPaths.Where(p => p.StartsWith("/dst", StringComparison.Ordinal)));

        // Outside that operation a read asks again.
        _prompt.Answer("secret");
        Assert.Equal("a"u8.ToArray(), await ReadAsync(root.Combine("a.txt")));
        Assert.Equal(2, _prompt.Questions.Count);
    }

    // ---- Passwords at the first read (encrypted entries) ------------------------------------------------

    [Fact]
    public async Task Encrypted_zip_lists_freely_and_asks_at_first_read()
    {
        var archive = AddFixture("encrypted.zip");

        var root = await _mounts.MountAsync(archive, Ct);
        Assert.Equal(["a.txt", "docs", "привет.txt"],
            (await ListAsync(root)).Select(e => e.Name).Order(StringComparer.Ordinal));
        Assert.Empty(_prompt.Questions);

        _prompt.Answer("secret");
        Assert.Equal("a"u8.ToArray(), await ReadAsync(root.Combine("a.txt")));
        Assert.Equal([(archive, false)], _prompt.Questions);
    }

    [Fact]
    public async Task Wrong_password_at_a_read_is_asked_again_and_fails_after_three()
    {
        var root = await _mounts.MountAsync(AddFixture("encrypted.zip"), Ct);
        _prompt.Answer("x", "y", "z");

        var error = await Assert.ThrowsAsync<ArchivePasswordRequiredException>(() =>
            ReadAsync(root.Combine("a.txt")));

        Assert.True(error.WrongPassword);
        Assert.Equal([false, true, true], _prompt.PreviousWasWrong);
        _prompt.Answer("u", "secret"); // the next read asks afresh, by the same rules
        Assert.Equal("hello\n"u8.ToArray(), await ReadAsync(At(root, "docs/readme.txt")));
        Assert.Equal([false, true, true, false, true], _prompt.PreviousWasWrong);
    }

    [Fact]
    public async Task Right_password_is_asked_once_per_session()
    {
        var archive = AddFixture("encrypted.zip");
        var root = await _mounts.MountAsync(archive, Ct);
        _prompt.Answer("secret");

        var reads = Enumerable.Range(0, 12)
            .Select(i => (i % 3) switch { 0 => "a.txt", 1 => "docs/readme.txt", _ => "docs/sub/data.bin" })
            .Select(name => Task.Run(() => ReadAsync(At(root, name)), Ct)).ToList();
        await Task.WhenAll(reads).WaitAsync(Timeout, Ct);
        Assert.Equal(DataBin, await ReadAsync(At(root, "docs/sub/data.bin")));

        Assert.Equal(root, await _mounts.MountAsync(archive, Ct));
        Assert.Equal(Encoding.UTF8.GetBytes("привет\n"), await ReadAsync(root.Combine("привет.txt")));
        Assert.Single(_prompt.Questions);
    }

    [Fact]
    public async Task Copy_out_of_encrypted_zip_through_the_engine()
    {
        var root = await _mounts.MountAsync(AddFixture("encrypted.zip"), Ct);
        _host.Files.AddDirectory("/dst");
        _prompt.Answer("secret");

        var sources = (await ListAsync(root)).Select(e => e.Path).ToList();
        var op = new CopyOperation(_providers, sources, Mem("/dst"), new CopyOptions(), new ScriptedInteraction());
        var result = await op.RunAsync().WaitAsync(Timeout, Ct);

        Assert.Equal(OperationState.Completed, result.State);
        Assert.Single(_prompt.Questions);
        Assert.Equal("a", _host.Files.ReadAllText("/dst/a.txt"));
        Assert.Equal("hello\n", _host.Files.ReadAllText("/dst/docs/readme.txt"));
        Assert.Equal(DataBin, await ReadAsync(Mem("/dst/docs/sub/data.bin")));
        Assert.Equal("привет\n", _host.Files.ReadAllText("/dst/привет.txt"));
    }

    // ---- Nesting ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Archive_inside_archive_lists_and_reads()
    {
        _host.Files.AddFile("/arc/outer.zip",
            Zip(("inner/sample.zip", Fixture.Bytes("sample.zip")), ("top.txt", "top"u8.ToArray())), HostTime);
        var outer = await _mounts.MountAsync(Mem("/arc/outer.zip"), Ct);
        var innerFile = await EntryAsync(At(outer, "inner/sample.zip"));

        var inner = await _mounts.TryMountAsync(innerFile, byContent: false, Ct);

        AssertArchiveRoot(inner);
        Assert.NotEqual(outer.ProviderId, inner!.Value.ProviderId);
        Assert.Equal(["a.txt", "docs", "empty", "привет.txt"],
            (await ListAsync(inner.Value)).Select(e => e.Name).Order(StringComparer.Ordinal));
        Assert.Equal("hello\n"u8.ToArray(), await ReadAsync(At(inner.Value, "docs/readme.txt")));
        Assert.Equal(DataBin, await ReadAsync(At(inner.Value, "docs/sub/data.bin")));
        Assert.Equal(new HostLocation(outer.Combine("inner"), "sample.zip"), _providers.UpOf(inner.Value));
        Assert.Equal("/arc/outer.zip/inner/sample.zip/docs", _providers.DisplayPath(inner.Value.Combine("docs")));
        Assert.Equal(Mem("/arc"), _providers.PersistentLocationOf(inner.Value.Combine("docs")));
        Assert.Equal(inner, await _mounts.TryMountAsync(innerFile, byContent: true, Ct));
    }

    // ---- Lifetime ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Dispose_closes_every_mounted_archive()
    {
        var mounts = new ArchiveMounts(_providers, _temp, _prompt);
        var zip = await mounts.MountAsync(AddFixture("sample.zip"), Ct);
        var tar = await mounts.MountAsync(AddFixture("sample.tar"), Ct);

        mounts.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => ListAsync(zip));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => ListAsync(tar));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => mounts.MountAsync(Mem("/arc/sample.zip"), Ct));
        mounts.Dispose(); // twice is fine
    }

    [Fact]
    public async Task Mount_that_finishes_after_dispose_is_closed_and_not_registered()
    {
        var archive = AddFixture("sample.zip", "/arc/locked.7z");
        var opener = new EncryptedListing(this, right: "pw") { PasswordGate = new TaskCompletionSource() };
        _prompt.Answer("pw");
        var mounts = MountsOpeningWith(opener.OpenAsync);

        var pending = Task.Run(() => mounts.MountAsync(archive, Ct), Ct);
        await opener.EnteredWithPassword.Task.WaitAsync(Timeout, Ct);
        mounts.Dispose();
        opener.PasswordGate.SetResult();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => pending.WaitAsync(Timeout, Ct));
        var opened = Assert.Single(opener.Opened);
        Assert.False(_providers.TryGet(opened.Id, out _));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => opened.GetEntryAsync(new VfsPath(opened.Id, "/"), Ct));
    }

    [Fact]
    public async Task Mount_disposed_while_opening_stops_before_asking_for_the_password()
    {
        var archive = AddFixture("sample.zip", "/arc/locked.7z");
        var opener = new EncryptedListing(this, right: "pw") { Gate = new TaskCompletionSource() };
        var mounts = MountsOpeningWith(opener.OpenAsync);

        var pending = Task.Run(() => mounts.MountAsync(archive, Ct), Ct);
        await opener.Entered.Task.WaitAsync(Timeout, Ct);
        mounts.Dispose();
        opener.Gate.SetResult();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => pending.WaitAsync(Timeout, Ct));
        Assert.Empty(_prompt.Questions);
        Assert.Equal([null], opener.Passwords);
        Assert.Empty(opener.Opened);
    }

    [Fact]
    public async Task Cancelled_mount_leaves_nothing_registered()
    {
        var archive = AddFixture("sample.zip");
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _mounts.MountAsync(archive, cancelled.Token));

        Assert.False(_providers.TryGet("archive:1", out _));
        AssertArchiveRoot(await _mounts.MountAsync(archive, Ct));
    }

    private static byte[] Zip(params (string Name, byte[] Content)[] files)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in files)
            {
                using var stream = zip.CreateEntry(name, CompressionLevel.NoCompression).Open();
                stream.Write(content);
            }
        }
        return buffer.ToArray();
    }
}
