using System.Runtime.Versioning;
using System.Text;

namespace Tandem.Platform.Mac.Tests;

/// <summary>The quarantine mark of items inside a per-test temp directory only.</summary>
[SupportedOSPlatform("macos")]
public sealed class MacQuarantineTests : IDisposable
{
    private const string Attribute = "com.apple.quarantine";
    private const string Value = "0083;6560a1b2;Tandem tests;";

    private readonly TempDir _temp = new();
    private readonly CountingBroker _broker = new();
    private readonly MacQuarantine _quarantine;

    public MacQuarantineTests() => _quarantine = new MacQuarantine(_broker);

    public void Dispose() => _temp.Dispose();

    private string NewFile(string name)
    {
        var path = _temp.Combine(name);
        File.WriteAllText(path, "x");
        return path;
    }

    [Fact]
    public void Mark_is_read_as_the_system_wrote_it()
    {
        var file = NewFile("downloaded.zip");
        XAttr.Set(file, Attribute, Value);

        Assert.Equal(Value, Encoding.UTF8.GetString(_quarantine.Get(file)!));
        Assert.Contains(file, _broker.Accessed);
        Assert.Equal(0, _broker.OpenScopes);
    }

    [Fact]
    public void Item_without_a_mark_has_none()
    {
        Assert.Null(_quarantine.Get(NewFile("plain.txt")));
    }

    [Fact]
    public void Mark_written_to_a_file_and_a_folder_is_the_attribute_itself()
    {
        var file = NewFile("unpacked.txt");
        var folder = Directory.CreateDirectory(_temp.Combine("unpacked")).FullName;

        _quarantine.Set(file, Encoding.UTF8.GetBytes(Value));
        _quarantine.Set(folder, Encoding.UTF8.GetBytes(Value));

        Assert.Equal(Value, XAttr.Get(file, Attribute));
        Assert.Equal(Value, XAttr.Get(folder, Attribute));
        Assert.Equal(0, _broker.OpenScopes);
    }

    [Fact]
    public void Missing_item_and_relative_path_are_errors()
    {
        Assert.ThrowsAny<IOException>(() => _quarantine.Get(_temp.Combine("missing")));
        Assert.ThrowsAny<IOException>(() => _quarantine.Set(_temp.Combine("missing"), [1]));
        Assert.Throws<ArgumentException>(() => _quarantine.Get("relative.txt"));
    }
}
