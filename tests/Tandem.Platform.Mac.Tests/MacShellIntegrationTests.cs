using System.Runtime.Versioning;
using Tandem.Core.Platform;
using Tandem.Core.Settings;

namespace Tandem.Platform.Mac.Tests;

/// <summary>Nothing here launches anything: every launch goes to a <see cref="RecordingProcessRunner"/>. Only
/// <see cref="MacShellIntegration.GetAppsFor"/> calls the real (read-only) <c>NSWorkspace</c>.</summary>
[SupportedOSPlatform("macos")]
public sealed class MacShellIntegrationTests : IDisposable
{
    private const string Open = "/usr/bin/open";

    private readonly TempDir _temp = new();
    private readonly RecordingProcessRunner _runner = new();
    private readonly MacShellIntegration _shell;

    public MacShellIntegrationTests() => _shell = new MacShellIntegration(_runner);

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Reveal_runs_open_R()
    {
        _shell.RevealInFinder("/tmp/x.txt");

        var spec = Assert.Single(_runner.Specs);
        Assert.Equal(Open, spec.FileName);
        Assert.Equal(["-R", "/tmp/x.txt"], spec.Arguments);
    }

    [Fact]
    public void OpenDefault_runs_open_with_the_path()
    {
        _shell.OpenDefault("/tmp/x.txt");

        var spec = Assert.Single(_runner.Specs);
        Assert.Equal(Open, spec.FileName);
        Assert.Equal(["/tmp/x.txt"], spec.Arguments);
    }

    [Fact]
    public void OpenInTextEditor_runs_open_t()
    {
        _shell.OpenInTextEditor("/tmp/x.txt");

        var spec = Assert.Single(_runner.Specs);
        Assert.Equal(Open, spec.FileName);
        Assert.Equal(["-t", "/tmp/x.txt"], spec.Arguments);
    }

    [Fact]
    public void OpenWith_passes_bundle_path()
    {
        _shell.OpenWith("/tmp/x.txt", new AppInfo("Some Editor", "/Applications/Some Editor.app"));

        var spec = Assert.Single(_runner.Specs);
        Assert.Equal(Open, spec.FileName);
        Assert.Equal(["-a", "/Applications/Some Editor.app", "/tmp/x.txt"], spec.Arguments);
    }

    [Fact]
    public void OpenTerminal_runs_open_a_Terminal()
    {
        _shell.OpenTerminal("/tmp/some dir", TerminalChoice.Default);

        var spec = Assert.Single(_runner.Specs);
        Assert.Equal(Open, spec.FileName);
        Assert.Equal(["-a", "Terminal", "/tmp/some dir"], spec.Arguments);
    }

    [Fact]
    public void OpenTerminal_runs_open_iTerm_when_chosen()
    {
        _shell.OpenTerminal("/tmp/some dir", TerminalChoice.ITerm);

        var spec = Assert.Single(_runner.Specs);
        Assert.Equal(["-a", "iTerm", "/tmp/some dir"], spec.Arguments);
    }

    [Fact]
    public void OpenFullDiskAccessSettings_opens_the_privacy_pane()
    {
        _shell.OpenFullDiskAccessSettings();

        var spec = Assert.Single(_runner.Specs);
        Assert.Equal(Open, spec.FileName);
        Assert.Equal(["x-apple.systempreferences:com.apple.preference.security?Privacy_AllFiles"], spec.Arguments);
    }

    [Fact]
    public void Path_with_spaces_and_quotes_is_single_argument()
    {
        const string path = "/tmp/a b \"c\".txt";

        _shell.RevealInFinder(path);
        _shell.OpenDefault(path);

        Assert.All(_runner.Specs, spec => Assert.Equal(path, spec.Arguments[^1]));
        Assert.Equal(["-R", path], _runner.Specs[0].Arguments);
        Assert.Equal([path], _runner.Specs[1].Arguments);
    }

    [Fact]
    public void Launch_returns_without_waiting_and_disposes_the_process_once_it_exits()
    {
        _shell.OpenDefault("/tmp/x.txt");

        var process = Assert.Single(_runner.Started);
        Assert.False(process.HasExited);
        Assert.Equal(0, process.Kills);

        process.Exit();

        Assert.True(SpinWait.SpinUntil(() => process.Disposals == 1, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task A_launcher_that_succeeds_completes_its_task()
    {
        var launched = _shell.OpenDefault("/tmp/x.txt");
        Assert.False(launched.IsCompleted); // returned at once, without waiting

        Assert.Single(_runner.Started).Exit(0);

        await launched.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_launcher_that_fails_faults_its_task_with_what_it_wrote()
    {
        var launched = _shell.RevealInFinder("/tmp/gone.txt");

        var process = Assert.Single(_runner.Started);
        process.Exit(1, "The file /tmp/gone.txt does not exist.");

        var error = await Assert.ThrowsAsync<IOException>(() =>
            launched.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Equal("The file /tmp/gone.txt does not exist.", error.Message);
        Assert.True(SpinWait.SpinUntil(() => process.Disposals == 1, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task A_launcher_that_fails_silently_names_its_exit_code()
    {
        var launched = _shell.OpenWith("/tmp/x.txt", new AppInfo("Broken", "/Applications/Broken.app"));

        Assert.Single(_runner.Started).Exit(1);

        var error = await Assert.ThrowsAsync<IOException>(() =>
            launched.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Contains("1", error.Message);
        Assert.Contains("/usr/bin/open", error.Message);
    }

    [Fact]
    public void Launch_failure_is_an_IOException()
    {
        _runner.StartFailure = new System.ComponentModel.Win32Exception(2, "No such file or directory");

        // Thrown at once, not through the task: nothing was started.
        var error = Assert.Throws<IOException>(() => { _ = _shell.OpenDefault("/tmp/x.txt"); });
        Assert.IsType<System.ComponentModel.Win32Exception>(error.InnerException);
    }

    [Fact]
    public void GetAppsFor_txt_returns_at_least_TextEdit()
    {
        var file = _temp.Combine("note.txt");
        File.WriteAllText(file, "text");

        var apps = _shell.GetAppsFor(file);

        var textEdit = Assert.Single(apps, a => a.Name == "TextEdit");
        Assert.EndsWith("/TextEdit.app", textEdit.BundlePath, StringComparison.Ordinal);
        Assert.Equal(apps.Count, apps.Select(a => a.BundlePath).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(apps.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(a => a.BundlePath, StringComparer.Ordinal).Select(a => a.BundlePath),
            apps.Select(a => a.BundlePath));
        Assert.All(apps, a => Assert.False(a.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase)));
        Assert.Empty(_runner.Specs);
    }

    [Fact]
    public void GetAppsFor_missing_file_is_empty()
    {
        Assert.Empty(_shell.GetAppsFor(_temp.Combine("missing.txt")));
    }

    [Fact]
    public void GetAppsFor_relative_path_throws()
    {
        Assert.Throws<ArgumentException>(() => _shell.GetAppsFor("note.txt"));
    }
}
