using System.Diagnostics;
using System.Runtime.Versioning;
using Tandem.Core.Vfs;
using Tandem.Core.Vfs.Local;

namespace Tandem.Platform.Windows.Tests;

/// <summary>Core's <c>WindowsEntryIdentity</c> (internal), through the local provider that uses it by default on
/// Windows.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsEntryIdentityTests : IDisposable
{
    private readonly TempDir _temp = new();
    private readonly LocalFileSystemProvider _fs = new(new UnrestrictedAccessBroker());

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    private VfsPath At(string relative) => VfsPath.Local(_temp.Combine(relative));

    [Fact]
    public async Task Two_spellings_of_one_file_are_the_same_entry()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        File.WriteAllText(_temp.Combine("a.txt"), "a");

        Assert.True(await _fs.IsSameEntryAsync(At("a.txt"), At("A.TXT"), Ct));
    }

    [Fact]
    public async Task A_hard_link_is_the_same_entry_as_its_file()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        File.WriteAllText(_temp.Combine("a.txt"), "a");
        HardLink(_temp.Combine("a.txt"), _temp.Combine("b.txt"));

        Assert.True(await _fs.IsSameEntryAsync(At("a.txt"), At("b.txt"), Ct));
    }

    [Fact]
    public async Task Two_files_are_different_entries()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        File.WriteAllText(_temp.Combine("a.txt"), "a");
        File.WriteAllText(_temp.Combine("b.txt"), "b");

        Assert.False(await _fs.IsSameEntryAsync(At("a.txt"), At("b.txt"), Ct));
    }

    [Fact]
    public async Task Two_spellings_of_one_directory_are_the_same_entry()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        Directory.CreateDirectory(_temp.Combine("dir"));

        Assert.True(await _fs.IsSameEntryAsync(At("dir"), At("DIR"), Ct));
    }

    [Fact]
    public async Task Nothing_there_is_not_the_same_entry()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        File.WriteAllText(_temp.Combine("a.txt"), "a");

        Assert.False(await _fs.IsSameEntryAsync(At("a.txt"), At("missing"), Ct));
        Assert.False(await _fs.IsSameEntryAsync(At("missing-dir/missing"), At("missing-dir/missing"), Ct));
    }

    private static void HardLink(string existing, string link)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            ArgumentList = { "/c", "mklink", "/H", link, existing },
            UseShellExecute = false,
            RedirectStandardOutput = true,
        })!;
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }
}
