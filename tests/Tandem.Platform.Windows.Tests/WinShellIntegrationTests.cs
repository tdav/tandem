using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using Tandem.Core.Platform;
using Tandem.Core.Settings;

namespace Tandem.Platform.Windows.Tests;

[SupportedOSPlatform("windows")]
public sealed class WinShellIntegrationTests
{
    private const string Item = @"C:\Users\me\file name.txt";
    private const string Folder = @"C:\Users\me";

    private readonly List<ProcessStartInfo> _started = [];

    private WinShellIntegration Shell(string? wt = null, Exception? failure = null, string? pwsh = null) => new(info =>
    {
        if (failure is not null) throw failure;
        _started.Add(info);
    }, name => name switch { "wt.exe" => wt, "pwsh.exe" => pwsh, _ => null });

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

    private const string WindowsTerminalPath = @"C:\Users\me\AppData\Local\Microsoft\WindowsApps\wt.exe";

    [Fact]
    public async Task OpenTerminal_uses_windows_terminal_when_chosen_and_on_the_path()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");

        await Shell(wt: WindowsTerminalPath).OpenTerminal(Folder, TerminalChoice.WindowsTerminal);

        var info = Assert.Single(_started);
        Assert.Equal(WindowsTerminalPath, info.FileName);
        Assert.Equal(["-d", Folder], info.ArgumentList);
    }

    [Fact]
    public async Task OpenTerminal_default_is_windows_powershell_in_the_directory()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");

        // Windows Terminal being installed does not matter unless it is chosen.
        await Shell(wt: WindowsTerminalPath).OpenTerminal(Folder, TerminalChoice.Default);

        var info = Assert.Single(_started);
        Assert.Equal("powershell.exe", info.FileName);
        Assert.Equal(Folder, info.WorkingDirectory);
        Assert.True(info.UseShellExecute);
    }

    [Fact]
    public async Task OpenTerminal_default_prefers_powershell_7_when_installed()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");

        await Shell(pwsh: @"C:\Program Files\PowerShell\7\pwsh.exe").OpenTerminal(Folder, TerminalChoice.Default);

        Assert.Equal(@"C:\Program Files\PowerShell\7\pwsh.exe", Assert.Single(_started).FileName);
    }

    [Fact]
    public async Task OpenTerminal_windows_terminal_chosen_but_missing_falls_back_to_powershell()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");

        await Shell().OpenTerminal(Folder, TerminalChoice.WindowsTerminal);

        Assert.Equal("powershell.exe", Assert.Single(_started).FileName);
    }

    [Fact]
    public async Task OpenTerminal_cmd_when_chosen_in_the_directory()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");

        await Shell(wt: WindowsTerminalPath).OpenTerminal(Folder, TerminalChoice.Cmd);

        var info = Assert.Single(_started);
        Assert.Equal("cmd.exe", info.FileName);
        Assert.Equal(Folder, info.WorkingDirectory);
    }

    [Fact]
    public async Task OpenTerminal_iterm_is_not_here_so_the_default_opens()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");

        await Shell().OpenTerminal(Folder, TerminalChoice.ITerm);

        Assert.Equal("powershell.exe", Assert.Single(_started).FileName);
    }

    [Fact]
    public async Task Relative_paths_are_refused()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var shell = Shell();

        await Assert.ThrowsAsync<ArgumentException>(() => shell.OpenDefault("file.txt"));
        await Assert.ThrowsAsync<ArgumentException>(() => shell.OpenInTextEditor(@"dir\file.txt"));
        await Assert.ThrowsAsync<ArgumentException>(() => shell.RevealInFinder(@"C:file.txt"));
        await Assert.ThrowsAsync<ArgumentException>(() => shell.OpenTerminal("dir", TerminalChoice.Default));
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
        await Assert.ThrowsAsync<IOException>(() => shell.OpenTerminal(Folder, TerminalChoice.Default));
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
