using System.Runtime.Versioning;

namespace Tandem.Platform.Mac.Tests;

/// <summary>Quick Look is never really opened here: <c>qlmanage</c> goes to a <see cref="RecordingProcessRunner"/>.</summary>
[SupportedOSPlatform("macos")]
public sealed class MacQuickLookTests
{
    private readonly RecordingProcessRunner _runner = new();
    private readonly MacQuickLook _quickLook;

    public MacQuickLookTests() => _quickLook = new MacQuickLook(_runner);

    [Fact]
    public void QuickLook_toggle_starts_then_kills()
    {
        _quickLook.Toggle(["/tmp/a b.txt", "/tmp/c.png"]);

        var process = Assert.Single(_runner.Started);
        Assert.Equal("/usr/bin/qlmanage", process.Spec.FileName);
        Assert.Equal(["-p", "/tmp/a b.txt", "/tmp/c.png"], process.Spec.Arguments);
        Assert.True(_quickLook.IsOpen);

        _quickLook.Toggle(["/tmp/a b.txt"]);

        Assert.Single(_runner.Started);
        Assert.Equal(1, process.Kills);
        Assert.False(_quickLook.IsOpen);
        Assert.True(SpinWait.SpinUntil(() => process.Disposals == 1, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void QuickLook_resets_when_process_exits()
    {
        _quickLook.Toggle(["/tmp/a.txt"]);
        var process = Assert.Single(_runner.Started);

        process.Exit();

        Assert.True(SpinWait.SpinUntil(() => !_quickLook.IsOpen, TimeSpan.FromSeconds(5)));
        Assert.True(SpinWait.SpinUntil(() => process.Disposals == 1, TimeSpan.FromSeconds(5)));
        Assert.Equal(0, process.Kills);

        _quickLook.Toggle(["/tmp/a.txt"]);
        Assert.Equal(2, _runner.Started.Count);
        Assert.True(_quickLook.IsOpen);
    }

    [Fact]
    public void Exit_of_a_closed_preview_does_not_reset_the_next_one()
    {
        _quickLook.Toggle(["/tmp/a.txt"]);
        _quickLook.Close();
        _quickLook.Toggle(["/tmp/b.txt"]);
        var first = _runner.Started[0];

        Assert.True(SpinWait.SpinUntil(() => first.Disposals == 1, TimeSpan.FromSeconds(5)));
        Assert.True(_quickLook.IsOpen);
    }

    [Fact]
    public void Close_kills_the_preview_and_is_harmless_when_closed()
    {
        _quickLook.Close();
        Assert.Empty(_runner.Started);

        _quickLook.Toggle(["/tmp/a.txt"]);
        _quickLook.Close();
        _quickLook.Close();

        var process = Assert.Single(_runner.Started);
        Assert.Equal(1, process.Kills);
        Assert.False(_quickLook.IsOpen);
    }

    [Fact]
    public void Toggle_with_no_paths_opens_nothing()
    {
        _quickLook.Toggle([]);

        Assert.Empty(_runner.Started);
        Assert.False(_quickLook.IsOpen);
    }

    [Fact]
    public void Start_failure_is_an_IOException_and_leaves_it_closed()
    {
        _runner.StartFailure = new System.ComponentModel.Win32Exception(2, "No such file or directory");

        Assert.Throws<IOException>(() => _quickLook.Toggle(["/tmp/a.txt"]));
        Assert.False(_quickLook.IsOpen);
    }
}
