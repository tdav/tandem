using Avalonia.Headless.XUnit;
using Tandem.App.Tests.Views;
using Tandem.Core.Vfs.InMemory;

namespace Tandem.App.Tests.ViewModels;

/// <summary>The output pane's docked / floating state on the command line's view model.</summary>
[Collection(LanguageCollection.Name)]
public class OutputFloatingTests : LanguageTestBase
{
    private static async Task<TestApp> StartAsync() =>
        await TestApp.StartAsync(TestApp.DefaultFileSystem(), show: false);

    [AvaloniaFact]
    public async Task OpenPane_IsDockedByDefault()
    {
        await using var app = await StartAsync();
        var line = app.Main.CommandLine;

        line.IsOutputVisible = true;

        Assert.False(line.IsOutputFloating);
        Assert.True(line.IsOutputDocked);
    }

    [AvaloniaFact]
    public async Task Float_DetachesOpenPane_AndDockBringsItBack()
    {
        await using var app = await StartAsync();
        var line = app.Main.CommandLine;
        line.IsOutputVisible = true;

        line.FloatOutputCommand.Execute(null);
        Assert.True(line.IsOutputFloating);
        Assert.False(line.IsOutputDocked);
        Assert.True(line.IsOutputVisible);

        line.DockOutputCommand.Execute(null);
        Assert.False(line.IsOutputFloating);
        Assert.True(line.IsOutputDocked);
    }

    [AvaloniaFact]
    public async Task Float_WhilePaneClosed_DoesNothing()
    {
        await using var app = await StartAsync();
        var line = app.Main.CommandLine;

        line.FloatOutputCommand.Execute(null);

        Assert.False(line.IsOutputFloating);
        Assert.False(line.IsOutputDocked);
    }

    [AvaloniaFact]
    public async Task ClosingFloatingPane_ClearsFloating_AndKeepsOutput()
    {
        await using var app = await StartAsync();
        var line = app.Main.CommandLine;
        line.IsOutputVisible = true;
        line.FloatOutputCommand.Execute(null);

        line.CloseOutputCommand.Execute(null);

        Assert.False(line.IsOutputVisible);
        Assert.False(line.IsOutputFloating);
        Assert.False(line.IsOutputDocked);
    }

    [AvaloniaFact]
    public async Task DockedChange_IsNotified()
    {
        await using var app = await StartAsync();
        var line = app.Main.CommandLine;
        line.IsOutputVisible = true;
        var changed = new List<string?>();
        line.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        line.FloatOutputCommand.Execute(null);

        Assert.Contains(nameof(line.IsOutputDocked), changed);
    }
}
