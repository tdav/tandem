using System.Runtime.Versioning;

namespace Tandem.Platform.Windows.Tests;

/// <summary>These tests really use the Recycle Bin (of the CI runner).</summary>
[SupportedOSPlatform("windows")]
public sealed class WinTrashTests : IDisposable
{
    private readonly TempDir _temp = new();
    private readonly WinTrash _trash = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task A_file_is_moved_away()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var file = _temp.Combine($"tandem-trash-test-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(file, "to the bin", Ct);

        await _trash.MoveToTrashAsync(file, Ct);

        Assert.False(Path.Exists(file));
    }

    [Fact]
    public async Task A_directory_is_moved_away_whole()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var directory = _temp.Combine($"tandem-trash-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(directory, "sub"));
        await File.WriteAllTextAsync(Path.Combine(directory, "sub", "inside.txt"), "x", Ct);

        await _trash.MoveToTrashAsync(directory, Ct);

        Assert.False(Path.Exists(directory));
    }

    [Fact]
    public async Task A_name_with_spaces_and_unicode_is_moved_away()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var file = _temp.Combine($"файл с пробелами {Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(file, "x", Ct);

        await _trash.MoveToTrashAsync(file, Ct);

        Assert.False(Path.Exists(file));
    }

    [Fact]
    public async Task A_missing_item_is_an_IOException()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");

        await Assert.ThrowsAnyAsync<IOException>(() => _trash.MoveToTrashAsync(_temp.Combine("missing"), Ct));
    }

    [Fact]
    public async Task A_cancelled_token_moves_nothing()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var file = _temp.Combine("kept.txt");
        await File.WriteAllTextAsync(file, "x", Ct);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _trash.MoveToTrashAsync(file, new CancellationToken(canceled: true)));

        Assert.True(File.Exists(file));
    }
}
