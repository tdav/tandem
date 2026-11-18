using System.Runtime.Versioning;
using Tandem.Core.Vfs.Local;

namespace Tandem.Platform.Mac.Tests;

/// <summary>
/// These tests really use the user's Trash. Each one removes from it only the exact item it put there, found by the
/// location the trash returned, and only after checking that location is directly inside <c>~/.Trash</c>.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacTrashTests : IDisposable
{
    private static readonly string UserTrash =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".Trash");

    private readonly TempDir _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task Trash_removes_from_original_and_places_in_trash()
    {
        var file = _temp.Combine($"tandem-trash-test-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(file, "to the trash", TestContext.Current.CancellationToken);
        var trash = new MacTrash(new UnrestrictedAccessBroker());

        var location = await trash.MoveToTrashAndGetLocationAsync(file, TestContext.Current.CancellationToken);
        try
        {
            Assert.False(File.Exists(file));
            AssertInUserTrash(location);
            Assert.Equal("to the trash", await File.ReadAllTextAsync(location, TestContext.Current.CancellationToken));
        }
        finally
        {
            DeleteFromTrash(location);
        }
    }

    [Fact]
    public async Task Trash_missing_file_throws_IOException()
    {
        var trash = new MacTrash(new UnrestrictedAccessBroker());

        await Assert.ThrowsAsync<IOException>(() =>
            trash.MoveToTrashAsync(_temp.Combine("missing.txt"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Trash_moves_a_symlink_itself_not_its_target()
    {
        var target = Directory.CreateDirectory(_temp.Combine("target")).FullName;
        await File.WriteAllTextAsync(Path.Combine(target, "inside.txt"), "stays", TestContext.Current.CancellationToken);
        var link = _temp.Combine($"tandem-trash-link-{Guid.NewGuid():N}");
        File.CreateSymbolicLink(link, target);
        var trash = new MacTrash(new UnrestrictedAccessBroker());

        var location = await trash.MoveToTrashAndGetLocationAsync(link, TestContext.Current.CancellationToken);
        try
        {
            Assert.False(Path.Exists(link));
            AssertInUserTrash(location);
            Assert.Equal(target, new FileInfo(location).LinkTarget);
            Assert.Equal("stays", await File.ReadAllTextAsync(Path.Combine(target, "inside.txt"),
                TestContext.Current.CancellationToken));
        }
        finally
        {
            DeleteFromTrash(location);
        }
    }

    [Fact]
    public async Task Trash_accesses_the_item_through_the_broker()
    {
        var file = _temp.Combine($"tandem-trash-broker-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(file, "x", TestContext.Current.CancellationToken);
        var broker = new CountingBroker();
        var trash = new MacTrash(broker);

        var location = await trash.MoveToTrashAndGetLocationAsync(file, TestContext.Current.CancellationToken);
        try
        {
            Assert.Equal([file], broker.Accessed);
            Assert.Equal(0, broker.OpenScopes);
        }
        finally
        {
            DeleteFromTrash(location);
        }
    }

    private static void AssertInUserTrash(string location)
    {
        Assert.Equal(location, Path.GetFullPath(location));
        Assert.Equal(UserTrash, Path.GetDirectoryName(location));
    }

    /// <summary>Deletes the one item this test put in the Trash; refuses any path not directly inside ~/.Trash.</summary>
    private static void DeleteFromTrash(string location)
    {
        if (location != Path.GetFullPath(location) || Path.GetDirectoryName(location) != UserTrash ||
            Path.GetFileName(location).Length == 0)
            throw new InvalidOperationException($"Refusing to delete '{location}': it is not an item in {UserTrash}.");

        if (new FileInfo(location).LinkTarget is null && Directory.Exists(location))
            Directory.Delete(location, recursive: true);
        else
            File.Delete(location);
    }
}
