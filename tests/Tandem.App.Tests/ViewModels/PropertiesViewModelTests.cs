using Tandem.App.Resources;
using Tandem.App.Services;
using Tandem.App.Tests.Views;
using Tandem.App.ViewModels;
using Tandem.Core.Vfs;
using Tandem.Core.Vfs.InMemory;

namespace Tandem.App.Tests.ViewModels;

/// <summary>The properties dialog's model over an in-memory "local disk" and in-memory tags: no real file's tags or
/// permissions are touched.</summary>
[Collection(LanguageCollection.Name)]
public class PropertiesViewModelTests : LanguageTestBase
{
    private const UnixFileMode Rwx = (UnixFileMode)0x1FF; // 0777

    /// <summary>Records mode changes and entry reads; can fail them or drop capabilities.</summary>
    private sealed class RecordingProvider(IFileSystemProvider inner,
        ProviderCapabilities removed = ProviderCapabilities.None) : IFileSystemProvider
    {
        public List<(VfsPath Path, UnixFileMode Mode)> SetModeCalls { get; } = [];
        public List<VfsPath> EntryReads { get; } = [];
        public Exception? SetModeFailure { get; set; }

        public string Id => inner.Id;
        public ProviderCapabilities Capabilities => inner.Capabilities & ~removed;
        public IAsyncEnumerable<FileEntry> EnumerateAsync(VfsPath directory, CancellationToken ct) =>
            inner.EnumerateAsync(directory, ct);
        public Task<FileEntry?> GetEntryAsync(VfsPath path, CancellationToken ct)
        {
            lock (EntryReads) EntryReads.Add(path);
            return inner.GetEntryAsync(path, ct);
        }
        public Task<Stream> OpenReadAsync(VfsPath path, CancellationToken ct) => inner.OpenReadAsync(path, ct);
        public Task<Stream> OpenWriteAsync(VfsPath path, CancellationToken ct) => inner.OpenWriteAsync(path, ct);
        public Task CreateDirectoryAsync(VfsPath path, CancellationToken ct) => inner.CreateDirectoryAsync(path, ct);
        public Task DeleteAsync(VfsPath path, bool recursive, CancellationToken ct) =>
            inner.DeleteAsync(path, recursive, ct);
        public Task MoveAsync(VfsPath source, VfsPath destination, CancellationToken ct) =>
            inner.MoveAsync(source, destination, ct);
        public Task CreateSymlinkAsync(VfsPath path, string target, CancellationToken ct) =>
            inner.CreateSymlinkAsync(path, target, ct);
        public Task SetModifiedAsync(VfsPath path, DateTimeOffset modified, CancellationToken ct) =>
            inner.SetModifiedAsync(path, modified, ct);
        public Task SetModeAsync(VfsPath path, UnixFileMode mode, CancellationToken ct)
        {
            if (SetModeFailure is { } failure) return Task.FromException(failure);
            lock (SetModeCalls) SetModeCalls.Add((path, mode));
            return inner.SetModeAsync(path, mode, ct);
        }
        public Task<bool> IsSameEntryAsync(VfsPath a, VfsPath b, CancellationToken ct) =>
            inner.IsSameEntryAsync(a, b, ct);
        public Task<string> GetVolumeKeyAsync(VfsPath path, CancellationToken ct) => inner.GetVolumeKeyAsync(path, ct);
        public IDisposable? Watch(VfsPath directory, Action changed) => inner.Watch(directory, changed);
    }

    private static InMemoryFileSystem FileSystem()
    {
        var fs = new InMemoryFileSystem(VfsPath.LocalProviderId);
        fs.AddFile("/home/notes.txt", "notes");
        fs.AddDirectory("/home/docs");
        return fs;
    }

    private static async Task<FileEntry> EntryAsync(IFileSystemProvider fs, string path, UnixFileMode? mode = null)
    {
        if (mode is { } m)
        {
            await fs.SetModeAsync(VfsPath.Local(path), m, CancellationToken.None);
            if (fs is RecordingProvider recording) recording.SetModeCalls.Clear(); // arranging is not a change
        }
        return (await fs.GetEntryAsync(VfsPath.Local(path), CancellationToken.None))!;
    }

    private static async Task<PropertiesViewModel> CreateAsync(FileEntry entry, IFileSystemProvider provider,
        FakeMetadata? metadata)
    {
        var vm = new PropertiesViewModel(entry, provider, metadata);
        await vm.LoadAsync();
        return vm;
    }

    private static bool[] Flags(PropertiesViewModel vm) =>
    [
        vm.OwnerRead, vm.OwnerWrite, vm.OwnerExecute, vm.GroupRead, vm.GroupWrite, vm.GroupExecute, vm.OtherRead,
        vm.OtherWrite, vm.OtherExecute,
    ];

    [Fact]
    public async Task Shows_name_size_date_owner_and_tags()
    {
        var fs = FileSystem();
        var metadata = new FakeMetadata { Owner = new("davron", "staff") };
        metadata.Tags["/home/notes.txt"] = ["Red", "Work"];
        var entry = await EntryAsync(fs, "/home/notes.txt", (UnixFileMode)0x1A4); // 0644

        var vm = await CreateAsync(entry, fs, metadata);

        Assert.Equal("notes.txt", vm.Name);
        Assert.Equal(SizeFormatter.Format(5), vm.SizeText);
        Assert.Equal(entry.Modified.ToLocalTime().ToString("g", Loc.Culture), vm.ModifiedText);
        Assert.Equal("davron:staff", vm.OwnerText);
        Assert.Equal("Red, Work", vm.TagsText);
        Assert.Equal("644", vm.Octal);
        Assert.True(vm.CanEditMode);
        Assert.True(vm.CanEditTags);
        Assert.Null(vm.Error);
    }

    [Fact]
    public async Task A_folder_says_folder_for_its_size()
    {
        var fs = FileSystem();
        var vm = await CreateAsync(await EntryAsync(fs, "/home/docs"), fs, new FakeMetadata());

        Assert.Equal(Loc.Get("Panel_Folder"), vm.SizeText);
    }

    [Fact]
    public async Task Octal_and_checkboxes_stay_in_sync()
    {
        var fs = FileSystem();
        var vm = await CreateAsync(await EntryAsync(fs, "/home/notes.txt"), fs, new FakeMetadata());

        vm.Octal = "640";
        Assert.Equal([true, true, false, true, false, false, false, false, false], Flags(vm));

        vm.GroupRead = false;
        Assert.Equal("600", vm.Octal);

        vm.OtherExecute = true;
        Assert.Equal("601", vm.Octal);
    }

    [Theory]
    [InlineData("9z")]
    [InlineData("64")]
    [InlineData("7777")]
    [InlineData("8 0")]
    [InlineData("")]
    [InlineData("-12")]
    public async Task Invalid_octal_is_ignored(string text)
    {
        var fs = new RecordingProvider(FileSystem());
        var vm = await CreateAsync(await EntryAsync(fs, "/home/notes.txt", (UnixFileMode)0x1A4), fs,
            new FakeMetadata());
        var before = Flags(vm);

        vm.Octal = text;

        Assert.Equal(before, Flags(vm));
        Assert.False(await vm.ApplyAsync()); // the text in the field is not applied
        Assert.Equal(Loc.Get("Properties_InvalidOctal"), vm.Error);
        Assert.Empty(fs.SetModeCalls);
    }

    [Fact]
    public async Task Apply_sets_mode_and_tags()
    {
        var fs = new RecordingProvider(FileSystem());
        var metadata = new FakeMetadata();
        var vm = await CreateAsync(await EntryAsync(fs, "/home/notes.txt", (UnixFileMode)0x1A4), fs, metadata);

        vm.Octal = "755";
        vm.TagsText = "Red, Work";

        Assert.True(await vm.ApplyAsync());

        Assert.Equal([(VfsPath.Local("/home/notes.txt"), (UnixFileMode)0x1ED)], fs.SetModeCalls);
        var (taggedPath, tags) = Assert.Single(metadata.SetCalls);
        Assert.Equal("/home/notes.txt", taggedPath);
        Assert.Equal(["Red", "Work"], tags);
        Assert.Null(vm.Error);
    }

    [Fact]
    public async Task Apply_changes_only_what_changed()
    {
        var fs = new RecordingProvider(FileSystem());
        var metadata = new FakeMetadata();
        metadata.Tags["/home/notes.txt"] = ["Red"];
        var vm = await CreateAsync(await EntryAsync(fs, "/home/notes.txt", (UnixFileMode)0x1A4), fs, metadata);

        Assert.True(await vm.ApplyAsync()); // nothing changed
        vm.TagsText = " Red ,"; // the same tags, typed differently
        vm.OwnerWrite = false;
        vm.OwnerWrite = true; // and back
        Assert.True(await vm.ApplyAsync());
        Assert.Empty(fs.SetModeCalls);
        Assert.Empty(metadata.SetCalls);

        vm.TagsText = "Red, Blue";
        Assert.True(await vm.ApplyAsync());
        Assert.Empty(fs.SetModeCalls);
        Assert.Single(metadata.SetCalls);

        vm.Octal = "600";
        Assert.True(await vm.ApplyAsync());
        Assert.Single(fs.SetModeCalls);
        Assert.Single(metadata.SetCalls); // the tags applied before are not applied again
        Assert.True(await vm.ApplyAsync());
        Assert.Single(fs.SetModeCalls);
    }

    [Fact]
    public async Task Special_bits_are_kept_when_applying()
    {
        var fs = new RecordingProvider(FileSystem());
        const UnixFileMode setUid = UnixFileMode.SetUser | UnixFileMode.StickyBit;
        var vm = await CreateAsync(await EntryAsync(fs, "/home/notes.txt", setUid | (UnixFileMode)0x1ED), fs,
            new FakeMetadata());
        Assert.Equal("755", vm.Octal);

        vm.Octal = "700";
        Assert.True(await vm.ApplyAsync());

        Assert.Equal(setUid | (UnixFileMode)0x1C0, Assert.Single(fs.SetModeCalls).Mode);
    }

    public static TheoryData<string, string[]> TagTexts => new()
    {
        { "", [] },
        { "  ", [] },
        { "Red", ["Red"] },
        { "Red,Work", ["Red", "Work"] },
        { " Red , Work ", ["Red", "Work"] },
        { "Red,,Work,", ["Red", "Work"] },
        { ", ,", [] },
        { "Red, red, RED", ["Red"] },
        { "work, Red, Work", ["work", "Red"] },
        { "Важное, важное", ["Важное"] },
        { "two words, x", ["two words", "x"] },
    };

    [Theory]
    [MemberData(nameof(TagTexts))]
    public void Tags_are_split_trimmed_and_deduplicated(string text, string[] expected) =>
        Assert.Equal(expected, PropertiesViewModel.ParseTags(text));

    [Fact]
    public async Task Apply_failure_sets_error_and_returns_false()
    {
        var fs = new RecordingProvider(FileSystem()) { SetModeFailure = new VfsAccessDeniedException("denied") };
        var vm = await CreateAsync(await EntryAsync(fs, "/home/notes.txt"), fs, new FakeMetadata());
        vm.Octal = "700";

        Assert.False(await vm.ApplyAsync());

        Assert.Equal(Loc.Format("Properties_ApplyFailed", Loc.Get("OperationFailed_AccessDenied")), vm.Error);
    }

    [Fact]
    public async Task A_tags_failure_sets_error_and_returns_false()
    {
        var fs = FileSystem();
        var metadata = new FakeMetadata();
        var vm = await CreateAsync(await EntryAsync(fs, "/home/notes.txt"), fs, metadata);
        metadata.SetFailure = new IOException("xattr failed");
        vm.TagsText = "Red";

        Assert.False(await vm.ApplyAsync());

        Assert.Equal(Loc.Format("Properties_ApplyFailed", Loc.Get("OperationFailed_IO")), vm.Error);
    }

    [Fact]
    public async Task Mode_not_editable_without_capability()
    {
        var fs = new RecordingProvider(FileSystem(), removed: ProviderCapabilities.UnixPermissions);
        var vm = await CreateAsync(await EntryAsync(fs, "/home/notes.txt"), fs, new FakeMetadata());

        Assert.False(vm.CanEditMode);
        vm.Octal = "700";
        Assert.True(await vm.ApplyAsync());
        Assert.Empty(fs.SetModeCalls);
    }

    [Fact]
    public async Task An_entry_without_a_mode_shows_none_and_cannot_edit_it()
    {
        var fs = FileSystem();
        var entry = (await EntryAsync(fs, "/home/notes.txt")) with { Mode = null };

        var vm = await CreateAsync(entry, fs, new FakeMetadata());

        Assert.False(vm.CanEditMode);
        Assert.Equal("", vm.Octal);
    }

    [Fact]
    public async Task Without_metadata_tags_and_owner_are_unavailable()
    {
        var fs = FileSystem();
        var vm = await CreateAsync(await EntryAsync(fs, "/home/notes.txt"), fs, metadata: null);

        Assert.False(vm.CanEditTags);
        Assert.Equal(Loc.Get("Properties_Unavailable"), vm.OwnerText);
        Assert.Equal(Loc.Get("Properties_Unavailable"), vm.TagsText);
        Assert.True(await vm.ApplyAsync());
    }

    [Fact]
    public async Task Unreadable_metadata_shows_unavailable_and_is_not_written()
    {
        var fs = FileSystem();
        var metadata = new FakeMetadata { Failure = new IOException("gone") };
        var vm = await CreateAsync(await EntryAsync(fs, "/home/notes.txt"), fs, metadata);
        metadata.Failure = null;

        Assert.False(vm.CanEditTags);
        Assert.Equal(Loc.Get("Properties_Unavailable"), vm.OwnerText);
        vm.TagsText = "Red";
        Assert.True(await vm.ApplyAsync());
        Assert.Empty(metadata.SetCalls);
    }

    [Fact]
    public async Task A_non_local_item_has_no_tags_or_owner()
    {
        var fs = new InMemoryFileSystem("remote");
        fs.AddFile("/a.txt");
        var entry = (await fs.GetEntryAsync(new VfsPath("remote", "/a.txt"), CancellationToken.None))!;
        var metadata = new FakeMetadata();

        var vm = await CreateAsync(entry, fs, metadata);

        Assert.False(vm.CanEditTags);
        Assert.Equal(Loc.Get("Properties_Unavailable"), vm.OwnerText);
        Assert.Empty(metadata.OwnerQueries);
    }

    [Fact]
    public async Task A_symlink_shows_its_own_info_and_never_follows_the_link()
    {
        var inner = FileSystem();
        await inner.CreateSymlinkAsync(VfsPath.Local("/home/link"), "/home/notes.txt", CancellationToken.None);
        var fs = new RecordingProvider(inner);
        var link = (await inner.GetEntryAsync(VfsPath.Local("/home/link"), CancellationToken.None))!;
        Assert.Equal(EntryKind.Symlink, link.Kind);
        var metadata = new FakeMetadata();
        metadata.Tags["/home/link"] = ["Link tag"];
        metadata.Tags["/home/notes.txt"] = ["Target tag"];

        var vm = await CreateAsync(link, fs, metadata);

        Assert.Equal("link", vm.Name);
        Assert.Equal("/home/notes.txt", vm.LinkTarget);
        Assert.Equal("Link tag", vm.TagsText);
        Assert.Equal(["/home/link"], metadata.OwnerQueries);
        Assert.Equal(Octal(link.Mode!.Value), vm.Octal); // the link's own mode as reported
        // Changing a link's mode would change its target's on macOS: not offered.
        Assert.False(vm.CanEditMode);
        vm.TagsText = "New";
        Assert.True(await vm.ApplyAsync());
        var (taggedPath, tags) = Assert.Single(metadata.SetCalls);
        Assert.Equal("/home/link", taggedPath);
        Assert.Equal(["New"], tags);
        Assert.Equal(["Target tag"], metadata.Tags["/home/notes.txt"]);
        Assert.Empty(fs.SetModeCalls);
        Assert.Empty(fs.EntryReads); // the target is never looked up
    }

    private static string Octal(UnixFileMode mode) => Convert.ToString((int)mode & 0x1FF, 8).PadLeft(3, '0');

    [Fact]
    public async Task Flags_cover_all_nine_bits()
    {
        var fs = FileSystem();
        var vm = await CreateAsync(await EntryAsync(fs, "/home/notes.txt", Rwx), fs, new FakeMetadata());

        Assert.Equal("777", vm.Octal);
        Assert.All(Flags(vm), Assert.True);
        vm.Octal = "000";
        Assert.All(Flags(vm), Assert.False);
    }
}
