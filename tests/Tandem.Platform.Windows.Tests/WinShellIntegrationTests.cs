using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using Tandem.Core.Platform;

namespace Tandem.Platform.Windows.Tests;

[SupportedOSPlatform("windows")]
public sealed class WinShellIntegrationTests
{
    private const string Item = @"C:\Users\me\file name.txt";
    private const string Folder = @"C:\Users\me";

    private readonly List<ProcessStartInfo> _started = [];

    private WinShellIntegration Shell(string? wt = null, Exception? failure = null) => new(info =>
    {
        if (failure is not null) throw failure;
        _started.Add(info);
    }, name => name == "wt.exe" ? wt : null);

    [Fact]
    public async Task OpenDefault_shell_executes_the_item()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");

        await Shell().OpenDefault(Item);

        var info = Assert.Single(_started);
        Assert.Equal(Item, info.FileName);
        Assert.True(info.UseShellExecute);
    }

    [Fact]
    public async Task OpenInTextEditor_starts_notepad_with_the_file()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");

        await Shell().OpenInTextEditor(Item);

        var info = Assert.Single(_started);
        Assert.Equal("notepad.exe", info.FileName);
        Assert.Equal([Item], info.ArgumentList);
    }

    [Fact]
    public async Task OpenWith_starts_the_application_with_the_item()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");

        await Shell().OpenWith(Item, new AppInfo("Editor", @"C:\Tools\editor.exe"));

        var info = Assert.Single(_started);
        Assert.Equal(@"C:\Tools\editor.exe", info.FileName);
        Assert.Equal([Item], info.ArgumentList);
    }

    [Fact]
    public async Task RevealInFinder_selects_the_item_in_explorer()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");

        await Shell().RevealInFinder(Item);

        var info = Assert.Single(_started);
        Assert.Equal("explorer.exe", info.FileName);
        Assert.Equal($"/select,\"{Item}\"", info.Arguments);
    }

    [Fact]
    public async Task OpenTerminal_uses_windows_terminal_when_it_is_on_the_path()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");

        await Shell(wt: @"C:\Users\me\AppData\Local\Microsoft\WindowsApps\wt.exe").OpenTerminal(Folder);

        var info = Assert.Single(_started);
        Assert.Equal(@"C:\Users\me\AppData\Local\Microsoft\WindowsApps\wt.exe", info.FileName);
        Assert.Equal(["-d", Folder], info.ArgumentList);
    }

    [Fact]
    public async Task OpenTerminal_falls_back_to_cmd_in_the_directory()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");

        await Shell().OpenTerminal(Folder);

        var info = Assert.Single(_started);
        Assert.Equal("cmd.exe", info.FileName);
        Assert.Equal(Folder, info.WorkingDirectory);
    }

    [Fact]
    public async Task Relative_paths_are_refused()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var shell = Shell();

        await Assert.ThrowsAsync<ArgumentException>(() => shell.OpenDefault("file.txt"));
        await Assert.ThrowsAsync<ArgumentException>(() => shell.OpenInTextEditor(@"dir\file.txt"));
        await Assert.ThrowsAsync<ArgumentException>(() => shell.RevealInFinder(@"C:file.txt"));
        await Assert.ThrowsAsync<ArgumentException>(() => shell.OpenTerminal("dir"));
        await Assert.ThrowsAsync<ArgumentException>(() => shell.OpenWith(Item, new AppInfo("x", "editor.exe")));
        Assert.Throws<ArgumentException>(() => shell.GetAppsFor("file.txt"));
        Assert.Empty(_started);
    }

    [Fact]
    public async Task A_launcher_that_cannot_start_is_an_IOException()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var shell = Shell(failure: new Win32Exception(2));

        await Assert.ThrowsAsync<IOException>(() => shell.OpenDefault(Item));
        await Assert.ThrowsAsync<IOException>(() => shell.OpenTerminal(Folder));
    }

    [Fact]
    public async Task The_real_shell_refuses_a_missing_item_with_an_IOException()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var missing = Path.Combine(Path.GetTempPath(), $"tandem-missing-{Guid.NewGuid():N}.txt");

        await Assert.ThrowsAsync<IOException>(() => new WinShellIntegration().OpenDefault(missing));
    }

    [Fact]
    public async Task There_are_no_apps_to_list_and_no_full_disk_access_to_grant()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var shell = Shell();

        Assert.Empty(shell.GetAppsFor(Item));
        await shell.OpenFullDiskAccessSettings();
        Assert.Empty(_started);
    }
}
