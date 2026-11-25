using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using Tandem.Core.Settings;
using Tandem.Core.Tests.Vfs;

namespace Tandem.Core.Tests.Settings;

[SupportedOSPlatform("macos")]
public sealed class JsonSettingsStoreTests : IDisposable
{
    private readonly TempDir _temp = new();

    public void Dispose() => _temp.Dispose();

    private string Dir => _temp.Combine("Tandem");
    private string SettingsFile => Path.Combine(Dir, "settings.json");
    private string BadFile => SettingsFile + ".bad";

    private JsonSettingsStore Store() => new(Dir);

    private void WriteRaw(string content)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(SettingsFile, content);
    }

    private static AppSettings Sample() => new(
        AppSettings.CurrentVersion,
        ThemeChoice.Dark,
        FontSize: 16,
        RowHeight: 26,
        LeftColumns:
        [
            new ColumnSetting(ColumnId.Size, true, 90),
            new ColumnSetting(ColumnId.Extension, false, 40),
            new ColumnSetting(ColumnId.Mode, true, 70),
            new ColumnSetting(ColumnId.Modified, true, 130),
        ],
        RightColumns:
        [
            new ColumnSetting(ColumnId.Extension, true, 60),
            new ColumnSetting(ColumnId.Size, false, 84),
            new ColumnSetting(ColumnId.Modified, true, 150),
            new ColumnSetting(ColumnId.Mode, false, 84),
        ],
        FileColors: [new FileColorRule("*.jpg;*.png", "Purple"), new FileColorRule("*.zip", "Red")],
        KeyBindings:
        [
            new KeyBindingOverride("file.copy", ["Ctrl+C", "Shift+F5"]),
            new KeyBindingOverride("view.quickView", []),
        ],
        Buttons: [new UserButton("Terminal", "/usr/bin/open", "-a Terminal %P")],
        ExternalEditor: "/Applications/TextEdit.app");

    /// <summary>A valid saved file as a mutable JSON tree, for tests that change one part of it.</summary>
    private JsonObject SampleJson()
    {
        Store().Save(Sample());
        return JsonNode.Parse(File.ReadAllText(SettingsFile))!.AsObject();
    }

    private static void AssertSame(AppSettings expected, AppSettings actual)
    {
        Assert.Equal(expected.Version, actual.Version);
        Assert.Equal(expected.Theme, actual.Theme);
        Assert.Equal(expected.FontSize, actual.FontSize);
        Assert.Equal(expected.RowHeight, actual.RowHeight);
        Assert.Equal(expected.LeftColumns, actual.LeftColumns);
        Assert.Equal(expected.RightColumns, actual.RightColumns);
        Assert.Equal(expected.FileColors, actual.FileColors);
        Assert.Equal(expected.KeyBindings.Count, actual.KeyBindings.Count);
        for (var i = 0; i < expected.KeyBindings.Count; i++)
        {
            Assert.Equal(expected.KeyBindings[i].CommandId, actual.KeyBindings[i].CommandId);
            Assert.Equal(expected.KeyBindings[i].Chords, actual.KeyBindings[i].Chords);
        }
        Assert.Equal(expected.Buttons, actual.Buttons);
        Assert.Equal(expected.ExternalEditor, actual.ExternalEditor);
    }

    private void AssertDefaultAndBadCopy(AppSettings loaded, string originalContent)
    {
        AssertSame(AppSettings.Default, loaded);
        Assert.False(File.Exists(SettingsFile));
        Assert.Equal(originalContent, File.ReadAllText(BadFile));
    }

    [Fact]
    public void Default_values()
    {
        var settings = AppSettings.Default;

        Assert.Equal(1, AppSettings.CurrentVersion);
        Assert.Equal(AppSettings.CurrentVersion, settings.Version);
        Assert.Equal(ThemeChoice.System, settings.Theme);
        Assert.Equal(14, settings.FontSize);
        Assert.Equal(22, settings.RowHeight);
        Assert.Equal(
            [
                new ColumnSetting(ColumnId.Extension, true, 56), new ColumnSetting(ColumnId.Size, true, 84),
                new ColumnSetting(ColumnId.Modified, true, 124), new ColumnSetting(ColumnId.Mode, true, 84),
            ],
            AppSettings.DefaultColumns);
        Assert.Equal(AppSettings.DefaultColumns, settings.LeftColumns);
        Assert.Equal(AppSettings.DefaultColumns, settings.RightColumns);
        Assert.Empty(settings.FileColors);
        Assert.Empty(settings.KeyBindings);
        Assert.Empty(settings.Buttons);
        Assert.Null(settings.ExternalEditor);
        Assert.Equal(["Red", "Orange", "Yellow", "Green", "Teal", "Blue", "Purple", "Gray"], AppSettings.ColorNames);
        AssertSame(settings, settings.Normalized());
    }

    [Fact]
    public void Missing_file_gives_defaults()
    {
        AssertSame(AppSettings.Default, Store().Load());
        Assert.False(Directory.Exists(Dir));
    }

    [Fact]
    public void Roundtrip_keeps_every_field()
    {
        Store().Save(Sample());

        AssertSame(Sample(), new JsonSettingsStore(Dir).Load());
        var text = File.ReadAllText(SettingsFile);
        Assert.Contains("\n", text);
        Assert.Contains("\"Dark\"", text);
        Assert.Contains("\"Modified\"", text);
        Assert.False(File.Exists(BadFile));
    }

    [Theory]
    [InlineData("{ \"Version\": 1, \"Theme\": ")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("{}")]
    [InlineData("{ \"Version\": 1, \"FontSize\": \"big\" }")]
    [InlineData("{ \"Version\": 1, \"LeftColumns\": [ { \"Column\": \"Size\", \"Visible\": \"yes\" } ] }")]
    [InlineData("{ \"Version\": 1, \"KeyBindings\": [ { \"CommandId\": \"file.copy\", \"Chords\": [ 5 ] } ] }")]
    public void Damaged_file_is_set_aside_as_bad_and_defaults_returned(string content)
    {
        WriteRaw(content);

        AssertDefaultAndBadCopy(Store().Load(), content);
    }

    [Theory]
    [InlineData(AppSettings.CurrentVersion + 1)]
    [InlineData(0)]
    [InlineData(-1)]
    public void Future_version_is_set_aside(int version)
    {
        var json = SampleJson();
        json["Version"] = version;
        var content = json.ToJsonString();
        WriteRaw(content);

        AssertDefaultAndBadCopy(Store().Load(), content);
    }

    [Theory]
    [InlineData("Theme", "Sepia")]
    [InlineData("Theme", 1)]
    [InlineData("Column", "Owner")]
    public void Unknown_theme_text_sets_the_file_aside(string field, object value)
    {
        var json = SampleJson();
        JsonNode node = value is string text ? JsonValue.Create(text) : JsonValue.Create((int)value);
        if (field == "Theme") json["Theme"] = node;
        else json["LeftColumns"]![0]!["Column"] = node;
        var content = json.ToJsonString();
        WriteRaw(content);

        AssertDefaultAndBadCopy(Store().Load(), content);
    }

    [Fact]
    public void Out_of_range_numbers_are_clamped()
    {
        var json = SampleJson();
        json["FontSize"] = 99;
        json["RowHeight"] = 5;
        json["LeftColumns"]![0]!["Width"] = 1000;
        json["LeftColumns"]![1]!["Width"] = 2;
        WriteRaw(json.ToJsonString());

        var loaded = Store().Load();

        Assert.Equal(AppSettings.MaxFontSize, loaded.FontSize);
        Assert.Equal(30, loaded.RowHeight); // 24 + 6
        Assert.Equal(AppSettings.MaxColumnWidth, loaded.LeftColumns[0].Width);
        Assert.Equal(AppSettings.MinColumnWidth, loaded.LeftColumns[1].Width);
        Assert.False(File.Exists(BadFile));
    }

    [Theory]
    [InlineData(5, 100, 10, 48)]  // the smallest font; the tallest row
    [InlineData(12, 17, 12, 18)]  // a row at least six points taller than the font
    [InlineData(12, 30, 12, 30)]  // in range: unchanged
    public void Font_size_and_row_height_are_kept_in_their_limits(double font, double row, double expectedFont,
        double expectedRow)
    {
        var loaded = (AppSettings.Default with { FontSize = font, RowHeight = row }).Normalized();

        Assert.Equal(expectedFont, loaded.FontSize);
        Assert.Equal(expectedRow, loaded.RowHeight);
    }

    [Fact]
    public void Numbers_left_out_of_the_file_get_their_defaults()
    {
        var json = SampleJson();
        json.Remove("FontSize");
        json.Remove("RowHeight");
        json["LeftColumns"]!.AsArray()[0]!.AsObject().Remove("Width");   // Size
        json["RightColumns"]!.AsArray()[2]!.AsObject().Remove("Width");  // Modified
        WriteRaw(json.ToJsonString());

        var loaded = Store().Load();

        Assert.Equal(AppSettings.DefaultFontSize, loaded.FontSize);
        Assert.Equal(AppSettings.DefaultRowHeight, loaded.RowHeight);
        Assert.Equal(new ColumnSetting(ColumnId.Size, true, 84), loaded.LeftColumns[0]);
        Assert.Equal(new ColumnSetting(ColumnId.Modified, true, 124), loaded.RightColumns[2]);
        Assert.False(File.Exists(BadFile));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    [InlineData(double.NaN)]
    public void Zero_negative_or_NaN_numbers_get_their_defaults(double value)
    {
        var settings = AppSettings.Default with
        {
            FontSize = value,
            RowHeight = value,
            LeftColumns = [new ColumnSetting(ColumnId.Extension, false, value)],
        };

        var loaded = settings.Normalized();

        Assert.Equal(AppSettings.DefaultFontSize, loaded.FontSize);
        Assert.Equal(AppSettings.DefaultRowHeight, loaded.RowHeight);
        Assert.Equal(new ColumnSetting(ColumnId.Extension, false, 56), loaded.LeftColumns[0]);
    }

    [Fact]
    public void Key_override_without_chords_is_dropped_but_an_empty_list_unbinds()
    {
        var json = SampleJson();
        json["KeyBindings"] = new JsonArray(
            new JsonObject { ["CommandId"] = "file.copy" },
            new JsonObject { ["CommandId"] = "file.move", ["Chords"] = null },
            new JsonObject { ["CommandId"] = "view.quickView", ["Chords"] = new JsonArray() });
        WriteRaw(json.ToJsonString());

        var loaded = Store().Load();

        var only = Assert.Single(loaded.KeyBindings);
        Assert.Equal("view.quickView", only.CommandId);
        Assert.Empty(only.Chords);
        Assert.False(File.Exists(BadFile));
    }

    [Fact]
    public void Missing_and_duplicate_columns_are_repaired()
    {
        var json = SampleJson();
        json["LeftColumns"] = new JsonArray(
            new JsonObject { ["Column"] = "Modified", ["Visible"] = false, ["Width"] = 100 },
            new JsonObject { ["Column"] = "Size", ["Visible"] = true, ["Width"] = 70 },
            new JsonObject { ["Column"] = "Modified", ["Visible"] = true, ["Width"] = 200 },
            null);
        json.Remove("RightColumns");
        WriteRaw(json.ToJsonString());

        var loaded = Store().Load();

        Assert.Equal(
            [
                new ColumnSetting(ColumnId.Modified, false, 100), new ColumnSetting(ColumnId.Size, true, 70),
                new ColumnSetting(ColumnId.Extension, true, 56), new ColumnSetting(ColumnId.Mode, true, 84),
            ],
            loaded.LeftColumns);
        Assert.Equal(AppSettings.DefaultColumns, loaded.RightColumns);
        Assert.False(File.Exists(BadFile));
    }

    [Fact]
    public void Bad_colour_rules_and_buttons_are_dropped()
    {
        var json = SampleJson();
        json["FileColors"] = new JsonArray(
            new JsonObject { ["Mask"] = "*.txt", ["Color"] = "Blue" },
            new JsonObject { ["Mask"] = "", ["Color"] = "Red" },
            new JsonObject { ["Mask"] = "  ", ["Color"] = "Red" },
            new JsonObject { ["Mask"] = "*.md", ["Color"] = "Magenta" },
            new JsonObject { ["Mask"] = "*.md" },
            null);
        json["Buttons"] = new JsonArray(
            new JsonObject { ["Title"] = "Edit", ["Program"] = "/usr/bin/vi", ["Arguments"] = "%P" },
            new JsonObject { ["Title"] = "", ["Program"] = "/usr/bin/vi", ["Arguments"] = "" },
            new JsonObject { ["Title"] = "Nothing", ["Program"] = " ", ["Arguments"] = "" },
            new JsonObject { ["Title"] = "No arguments", ["Program"] = "/bin/ls" },
            null);
        WriteRaw(json.ToJsonString());

        var loaded = Store().Load();

        Assert.Equal([new FileColorRule("*.txt", "Blue")], loaded.FileColors);
        Assert.Equal([new UserButton("Edit", "/usr/bin/vi", "%P"), new UserButton("No arguments", "/bin/ls", "")],
            loaded.Buttons);
        Assert.False(File.Exists(BadFile));
    }

    [Fact]
    public void Missing_lists_become_empty_and_a_blank_editor_null()
    {
        var json = SampleJson();
        json["FileColors"] = null;
        json.Remove("Buttons");
        json["KeyBindings"] = new JsonArray(
            new JsonObject { ["CommandId"] = "file.copy", ["Chords"] = null },
            new JsonObject { ["CommandId"] = "", ["Chords"] = new JsonArray("Ctrl+X") },
            new JsonObject { ["CommandId"] = "file.copy", ["Chords"] = new JsonArray("F5") },
            new JsonObject { ["CommandId"] = "file.move", ["Chords"] = new JsonArray("F6", " ", null) },
            null);
        json["ExternalEditor"] = "   ";
        WriteRaw(json.ToJsonString());

        var loaded = Store().Load();

        Assert.Empty(loaded.FileColors);
        Assert.Empty(loaded.Buttons);
        // The file.copy entry without chords is dropped, so the later one with F5 is the first that counts.
        Assert.Equal(["file.copy", "file.move"], loaded.KeyBindings.Select(b => b.CommandId));
        Assert.Equal(["F5"], loaded.KeyBindings[0].Chords);
        Assert.Equal(["F6"], loaded.KeyBindings[1].Chords);
        Assert.Null(loaded.ExternalEditor);
        Assert.False(File.Exists(BadFile));
    }

    [Fact]
    public void Every_cut_short_file_loads_without_throwing()
    {
        var content = SampleJson().ToJsonString();

        for (var length = 0; length < content.Length; length++)
        {
            WriteRaw(content[..length]);
            var loaded = Store().Load();
            Assert.Equal(AppSettings.CurrentVersion, loaded.Version);
        }
    }

    [Fact]
    public void Unknown_fields_are_ignored()
    {
        var json = SampleJson();
        json["FutureSetting"] = new JsonObject { ["Nested"] = 42 };
        WriteRaw(json.ToJsonString());

        AssertSame(Sample(), Store().Load());
        Assert.False(File.Exists(BadFile));
    }

    [Fact]
    public void Unreadable_file_gives_defaults_and_is_left_alone()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix permissions; Windows has none to take away.");
        Store().Save(Sample());
        File.SetUnixFileMode(SettingsFile, UnixFileMode.None);
        try
        {
            AssertSame(AppSettings.Default, Store().Load());
            Assert.True(File.Exists(SettingsFile));
            Assert.False(File.Exists(BadFile));
        }
        finally
        {
            File.SetUnixFileMode(SettingsFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public void Save_does_not_touch_state_json()
    {
        var stateStore = new JsonStateStore(Dir);
        stateStore.Save(AppState.Default("/Users/someone"));
        var stateBytes = File.ReadAllBytes(Path.Combine(Dir, "state.json"));

        Store().Save(Sample());

        Assert.Equal(stateBytes, File.ReadAllBytes(Path.Combine(Dir, "state.json")));
        Assert.Equal(["settings.json", "state.json"], Directory.GetFiles(Dir).Select(Path.GetFileName).Order());
        Assert.Equal(AppState.Default("/Users/someone").View, stateStore.Load("/Users/someone").View);
    }

    [Fact]
    public void Save_replaces_previous_file_and_leaves_no_temp_file()
    {
        var store = Store();
        Directory.CreateDirectory(Dir);
        File.WriteAllText(Path.Combine(Dir, ".settings.json.tandem-part"), "half-written junk from a crash");
        store.Save(AppSettings.Default);
        store.Save(Sample());

        AssertSame(Sample(), store.Load());
        Assert.Equal(["settings.json"], Directory.GetFiles(Dir).Select(Path.GetFileName));
    }

    [Fact]
    public void Failed_save_keeps_previous_file()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix permissions; Windows has none to take away.");
        var store = Store();
        store.Save(Sample());
        var before = File.ReadAllText(SettingsFile);
        File.SetUnixFileMode(Dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        Assert.ThrowsAny<IOException>(() => store.Save(AppSettings.Default));

        Assert.Equal(before, File.ReadAllText(SettingsFile));
        AssertSame(Sample(), store.Load());
    }

    [Fact]
    public void Save_when_directory_path_is_a_file_throws_io_exception()
    {
        File.WriteAllText(Dir, "not a directory");

        Assert.ThrowsAny<IOException>(() => Store().Save(Sample()));
    }
}
