using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using Tandem.Core.Listing;
using Tandem.Core.Settings;
using Tandem.Core.Tests.Vfs;
using Tandem.Core.Vfs;

namespace Tandem.Core.Tests.Settings;

[SupportedOSPlatform("macos")]
public sealed class JsonStateStoreTests : IDisposable
{
    private const string Home = "/Users/someone";

    private readonly TempDir _temp = new();

    public void Dispose() => _temp.Dispose();

    private string StateDir => _temp.Combine("Tandem");
    private string StateFile => Path.Combine(StateDir, "state.json");
    private string BadFile => StateFile + ".bad";

    private JsonStateStore Store() => new(StateDir);

    private void WriteRaw(string content)
    {
        Directory.CreateDirectory(StateDir);
        File.WriteAllText(StateFile, content);
    }

    private static AppState Sample() => new(
        AppState.CurrentVersion,
        new PanelState(
        [
            new TabState(VfsPath.LocalProviderId, "/Users/someone/Documents", SortColumn.Size, SortDescending: true, Pinned: true),
            new TabState(VfsPath.LocalProviderId, "/tmp", SortColumn.Modified, SortDescending: false, Pinned: false),
        ], ActiveTab: 1),
        new PanelState([new TabState(VfsPath.LocalProviderId, "/Applications", SortColumn.Extension, false, false)], 0),
        LeftIsActive: false,
        new ViewState(Sidebar: true, VolumeBars: false, CommandLine: true, FKeyBar: false),
        new WindowState(10.5, 20, 1280, 800, Maximized: true, SplitRatio: 0.4),
        ["/Users/someone", "/Volumes/Data"],
        ShowHidden: true,
        Language: "ru");

    /// <summary>A valid saved state as a mutable JSON tree, for tests that damage one part of it.</summary>
    private JsonObject SampleJson()
    {
        Store().Save(Sample());
        return JsonNode.Parse(File.ReadAllText(StateFile))!.AsObject();
    }

    private static void AssertSameState(AppState expected, AppState actual)
    {
        Assert.Equal(expected.Version, actual.Version);
        AssertSamePanel(expected.Left, actual.Left);
        AssertSamePanel(expected.Right, actual.Right);
        Assert.Equal(expected.LeftIsActive, actual.LeftIsActive);
        Assert.Equal(expected.View, actual.View);
        Assert.Equal(expected.Window, actual.Window);
        Assert.Equal(expected.Favorites, actual.Favorites);
        Assert.Equal(expected.ShowHidden, actual.ShowHidden);
        Assert.Equal(expected.Language, actual.Language);
    }

    private static void AssertSamePanel(PanelState expected, PanelState actual)
    {
        Assert.Equal(expected.Tabs, actual.Tabs);
        Assert.Equal(expected.ActiveTab, actual.ActiveTab);
    }

    private void AssertDefaultAndBadCopy(AppState loaded, string originalContent)
    {
        AssertSameState(AppState.Default(Home), loaded);
        Assert.False(File.Exists(StateFile));
        Assert.Equal(originalContent, File.ReadAllText(BadFile));
    }

    [Fact]
    public void Default_has_one_home_tab_per_panel_sorted_by_name()
    {
        var state = AppState.Default(Home);

        Assert.Equal(AppState.CurrentVersion, state.Version);
        var expectedTab = new TabState(VfsPath.LocalProviderId, Home, SortColumn.Name, false, false);
        Assert.Equal([expectedTab], state.Left.Tabs);
        Assert.Equal([expectedTab], state.Right.Tabs);
        Assert.Equal(0, state.Left.ActiveTab);
        Assert.Equal(0, state.Right.ActiveTab);
        Assert.True(state.LeftIsActive);
        Assert.Equal(new ViewState(), state.View);
        Assert.Null(state.Window);
        Assert.Empty(state.Favorites);
        Assert.False(state.ShowHidden);
        Assert.Null(state.Language);
    }

    [Fact]
    public void Missing_file_gives_default()
    {
        AssertSameState(AppState.Default(Home), Store().Load(Home));
        Assert.False(Directory.Exists(StateDir));
    }

    [Fact]
    public void Save_then_load_roundtrips()
    {
        Store().Save(Sample());

        AssertSameState(Sample(), new JsonStateStore(StateDir).Load(Home));
    }

    [Fact]
    public void Save_writes_indented_json_with_enum_names()
    {
        Store().Save(Sample());

        var text = File.ReadAllText(StateFile);
        Assert.Contains("\n", text);
        Assert.Contains("\"Size\"", text);
        Assert.Contains("\"Extension\"", text);
    }

    [Fact]
    public void Corrupt_json_gives_default_and_keeps_bad_copy()
    {
        const string content = "{ \"Version\": 1, \"Left\": ";
        WriteRaw(content);

        AssertDefaultAndBadCopy(Store().Load(Home), content);
    }

    [Fact]
    public void Newer_version_gives_default_and_keeps_bad_copy()
    {
        var json = SampleJson();
        json["Version"] = AppState.CurrentVersion + 1;
        var content = json.ToJsonString();
        WriteRaw(content);

        AssertDefaultAndBadCopy(Store().Load(Home), content);
    }

    [Fact]
    public void Empty_tabs_list_is_treated_as_corrupt()
    {
        var json = SampleJson();
        json["Right"]!["Tabs"] = new JsonArray();
        var content = json.ToJsonString();
        WriteRaw(content);

        AssertDefaultAndBadCopy(Store().Load(Home), content);
    }

    [Fact]
    public void Unknown_fields_are_ignored()
    {
        var json = SampleJson();
        json["FutureSetting"] = new JsonObject { ["Nested"] = 42 };
        json["Left"]!["Tabs"]![0]!["Colour"] = "red";
        WriteRaw(json.ToJsonString());

        AssertSameState(Sample(), Store().Load(Home));
        Assert.False(File.Exists(BadFile));
    }

    [Fact]
    public void Save_creates_directory()
    {
        Assert.False(Directory.Exists(StateDir));

        Store().Save(Sample());

        Assert.True(File.Exists(StateFile));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("{}")]
    public void Degenerate_content_gives_default_and_keeps_bad_copy(string content)
    {
        WriteRaw(content);

        AssertDefaultAndBadCopy(Store().Load(Home), content);
    }

    public static TheoryData<string> Damages =>
    [
        "version-zero",
        "version-missing",
        "version-string",
        "left-missing",
        "right-null",
        "view-missing",
        "tabs-null",
        "tabs-missing",
        "tab-null",
        "active-tab-negative",
        "active-tab-past-end",
        "path-empty",
        "path-null",
        "provider-missing",
        "sort-column-unknown-name",
        "sort-column-number",
        "pinned-string",
        "favorites-not-array",
        "favorite-null",
        "window-wrong-type",
    ];

    [Theory]
    [MemberData(nameof(Damages))]
    public void Damaged_state_gives_default_and_keeps_bad_copy(string damage)
    {
        var json = SampleJson();
        var leftTab = json["Left"]!["Tabs"]![0]!.AsObject();
        switch (damage)
        {
            case "version-zero": json["Version"] = 0; break;
            case "version-missing": json.Remove("Version"); break;
            case "version-string": json["Version"] = "1"; break;
            case "left-missing": json.Remove("Left"); break;
            case "right-null": json["Right"] = null; break;
            case "view-missing": json.Remove("View"); break;
            case "tabs-null": json["Left"]!["Tabs"] = null; break;
            case "tabs-missing": json["Left"]!.AsObject().Remove("Tabs"); break;
            case "tab-null": json["Left"]!["Tabs"]![0] = null; break;
            case "active-tab-negative": json["Left"]!["ActiveTab"] = -1; break;
            case "active-tab-past-end": json["Left"]!["ActiveTab"] = 2; break;
            case "path-empty": leftTab["Path"] = ""; break;
            case "path-null": leftTab["Path"] = null; break;
            case "provider-missing": leftTab.Remove("ProviderId"); break;
            case "sort-column-unknown-name": leftTab["SortColumn"] = "Colour"; break;
            case "sort-column-number": leftTab["SortColumn"] = 1; break;
            case "pinned-string": leftTab["Pinned"] = "yes"; break;
            case "favorites-not-array": json["Favorites"] = "/Users"; break;
            case "favorite-null": json["Favorites"]![0] = null; break;
            case "window-wrong-type": json["Window"] = "big"; break;
            default: throw new ArgumentOutOfRangeException(nameof(damage), damage, null);
        }
        var content = json.ToJsonString();
        WriteRaw(content);

        AssertDefaultAndBadCopy(Store().Load(Home), content);
    }

    [Fact]
    public void Optional_parts_may_be_absent_or_null()
    {
        var json = SampleJson();
        json.Remove("Favorites");
        json["Window"] = null;
        json.Remove("Language");
        json.Remove("ShowHidden");
        json["View"] = new JsonObject();
        WriteRaw(json.ToJsonString());

        var loaded = Store().Load(Home);

        AssertSameState(Sample() with
        {
            Favorites = [], Window = null, Language = null, ShowHidden = false, View = new ViewState(),
        }, loaded);
        Assert.False(File.Exists(BadFile));
    }

    [Fact]
    public void Null_favorites_load_as_empty_list()
    {
        var json = SampleJson();
        json["Favorites"] = null;
        WriteRaw(json.ToJsonString());

        Assert.Empty(Store().Load(Home).Favorites);
        Assert.False(File.Exists(BadFile));
    }

    [Fact]
    public void Bad_copy_replaces_older_bad_copy()
    {
        Directory.CreateDirectory(StateDir);
        File.WriteAllText(BadFile, "old bad");
        WriteRaw("new bad");

        AssertDefaultAndBadCopy(Store().Load(Home), "new bad");
    }

    [Fact]
    public void Directory_in_place_of_file_gives_default()
    {
        Directory.CreateDirectory(StateFile);

        AssertSameState(AppState.Default(Home), Store().Load(Home));
        Assert.True(Directory.Exists(StateFile));
    }

    [Fact]
    public void Unreadable_file_gives_default()
    {
        Store().Save(Sample());
        File.SetUnixFileMode(StateFile, UnixFileMode.None);
        try
        {
            AssertSameState(AppState.Default(Home), Store().Load(Home));
        }
        finally
        {
            File.SetUnixFileMode(StateFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public void Save_replaces_previous_state_and_leaves_no_temp_file()
    {
        var store = Store();
        store.Save(AppState.Default(Home));
        store.Save(Sample());

        AssertSameState(Sample(), store.Load(Home));
        Assert.Equal(["state.json"], Directory.GetFiles(StateDir).Select(Path.GetFileName));
    }

    [Fact]
    public void Save_overwrites_leftover_temp_file()
    {
        Directory.CreateDirectory(StateDir);
        File.WriteAllText(Path.Combine(StateDir, ".state.json.tandem-part"), "half-written junk from a crash");

        Store().Save(Sample());

        AssertSameState(Sample(), Store().Load(Home));
        Assert.Equal(["state.json"], Directory.GetFiles(StateDir).Select(Path.GetFileName));
    }

    [Fact]
    public void Save_into_unwritable_directory_throws_io_exception_and_keeps_old_file()
    {
        var store = Store();
        store.Save(AppState.Default(Home));
        File.SetUnixFileMode(StateDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        Assert.ThrowsAny<IOException>(() => store.Save(Sample()));

        AssertSameState(AppState.Default(Home), store.Load(Home));
    }

    [Fact]
    public void Save_when_directory_path_is_a_file_throws_io_exception()
    {
        File.WriteAllText(StateDir, "not a directory");

        Assert.ThrowsAny<IOException>(() => Store().Save(Sample()));
    }

    [Fact]
    public async Task Concurrent_saves_from_one_store_leave_a_valid_file()
    {
        var store = Store();
        var states = Enumerable.Range(0, 8)
            .Select(i => Sample() with { Favorites = [$"/fav/{i}"] })
            .ToArray();

        await Task.WhenAll(states.Select(s => Task.Run(() =>
        {
            for (var n = 0; n < 20; n++) store.Save(s);
        }, TestContext.Current.CancellationToken)));

        var loaded = store.Load(Home);
        Assert.Contains(states, s => s.Favorites.SequenceEqual(loaded.Favorites));
        Assert.False(File.Exists(BadFile));
        Assert.Equal(["state.json"], Directory.GetFiles(StateDir).Select(Path.GetFileName));
    }
}
