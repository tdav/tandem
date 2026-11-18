using System.Runtime.Versioning;
using Tandem.Core.Vfs.Local;

namespace Tandem.Platform.Mac.Tests;

/// <summary>Tags and owners of items inside a per-test temp directory only.</summary>
[SupportedOSPlatform("macos")]
public sealed class MacFileMetadataServiceTests : IDisposable
{
    private readonly TempDir _temp = new();
    private readonly MacFileMetadataService _service = new(new UnrestrictedAccessBroker());

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Tags_roundtrip()
    {
        var file = NewFile("tagged.txt");

        _service.SetTags(file, ["Red", "Работа"]);

        Assert.Equal(["Red", "Работа"], _service.GetTags(file).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Tags_roundtrip_on_a_directory()
    {
        var directory = Directory.CreateDirectory(_temp.Combine("folder")).FullName;

        _service.SetTags(directory, ["Blue"]);

        Assert.Equal(["Blue"], _service.GetTags(directory));
    }

    [Fact]
    public void Untagged_file_has_no_tags()
    {
        Assert.Empty(_service.GetTags(NewFile("plain.txt")));
    }

    [Fact]
    public void SetTags_empty_clears()
    {
        var file = NewFile("cleared.txt");
        _service.SetTags(file, ["Green"]);

        _service.SetTags(file, []);

        Assert.Empty(_service.GetTags(file));
    }

    [Fact]
    public void Tags_on_missing_file_throw_IOException()
    {
        var missing = _temp.Combine("missing.txt");

        Assert.Throws<IOException>(() => _service.GetTags(missing));
        Assert.Throws<IOException>(() => _service.SetTags(missing, ["Red"]));
        Assert.False(File.Exists(missing));
    }

    [Fact]
    public void Empty_tag_name_is_rejected()
    {
        var file = NewFile("bad-tag.txt");

        Assert.Throws<ArgumentException>(() => _service.SetTags(file, ["Red", ""]));
        Assert.Empty(_service.GetTags(file));
    }

    [Fact]
    public void Owner_of_temp_file_is_current_user()
    {
        var owner = _service.GetOwner(NewFile("owned.txt"));

        Assert.Equal(Environment.UserName, owner.User);
        Assert.False(string.IsNullOrEmpty(owner.Group));
    }

    [Fact]
    public void Owner_of_missing_file_throws_IOException()
    {
        Assert.Throws<IOException>(() => _service.GetOwner(_temp.Combine("missing.txt")));
    }

    [Fact]
    public void Non_ASCII_names_are_found()
    {
        var file = NewFile("Отчёт é.txt");

        _service.SetTags(file, ["Orange"]);

        Assert.Equal(["Orange"], _service.GetTags(file));
        Assert.Equal(Environment.UserName, _service.GetOwner(file).User);
    }

    [Fact]
    public void Relative_paths_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => _service.GetTags("file.txt"));
        Assert.Throws<ArgumentException>(() => _service.SetTags("file.txt", []));
        Assert.Throws<ArgumentException>(() => _service.GetOwner("file.txt"));
    }

    [Fact]
    public void Every_call_accesses_the_item_through_the_broker()
    {
        var file = NewFile("brokered.txt");
        var broker = new CountingBroker();
        var service = new MacFileMetadataService(broker);

        service.SetTags(file, ["Red"]);
        service.GetTags(file);
        service.GetOwner(file);

        Assert.Equal([file, file, file], broker.Accessed);
        Assert.Equal(0, broker.OpenScopes);
    }

    private string NewFile(string name)
    {
        var path = _temp.Combine(name);
        File.WriteAllText(path, "x");
        return path;
    }
}
