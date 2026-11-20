using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Tandem.App.Commands;
using Tandem.App.Resources;
using Tandem.App.Tests.Views;
using Tandem.App.ViewModels;
using Tandem.App.Views;
using Tandem.Core.Settings;
using Tandem.Core.Vfs;
using Tandem.Core.Vfs.InMemory;

namespace Tandem.App.Tests.ViewModels;

/// <summary>The button bar on the whole app over the in-memory "local disk" (both panels at /home: .., docs, src,
/// README.md, notes.txt, report.pdf); programs are started by a <see cref="FakeProcessRunner"/>, so nothing real
/// runs.</summary>
[Collection(LanguageCollection.Name)]
public class ButtonBarTests : LanguageTestBase
{
    private static readonly UserButton Zip = new("Zip", "/usr/bin/zip", "-r %t/out.zip %s");

    private static int IndexOf(PanelViewModel panel, string name) => panel.Rows.ToList().FindIndex(r => r.Name == name);

    private static Task<TestApp> StartAsync(FakeProcessRunner processes, bool show = false,
        params UserButton[] buttons)
    {
        var remote = new InMemoryFileSystem("remote");
        remote.AddFile("/shared/a.txt", "a");
        return TestApp.StartAsync(show: show, processes: processes, extraProviders: [remote],
            settings: AppSettings.Default with { Buttons = buttons });
    }

    [AvaloniaFact]
    public async Task Button_runs_program_with_expanded_arguments_in_active_folder()
    {
        var processes = new FakeProcessRunner();
        await using var app = await StartAsync(processes, buttons: Zip);
        await app.Right.NavigateToAsync(VfsPath.Local("/home/docs"));
        app.Left.Rows.Single(r => r.Name == "notes.txt").IsSelected = true;
        app.Left.Rows.Single(r => r.Name == "report.pdf").IsSelected = true;
        var bar = app.Main.ButtonBar;
        Assert.Equal([Zip], bar.Buttons);
        Assert.True(bar.CanRun);

        await bar.RunAsync(bar.Buttons[0]);

        var spec = Assert.Single(processes.Started);
        Assert.Equal("/usr/bin/zip", spec.FileName);
        Assert.Equal(["-r", "/home/docs/out.zip", "/home/notes.txt", "/home/report.pdf"], spec.Arguments);
        Assert.Equal("/home", spec.WorkingDirectory);

        // The output is read and dropped; the process is let go when it ends, never killed; its code is not shown.
        var process = processes.Last;
        process.Write("adding: notes.txt");
        process.Exit(12);
        await TestApp.WaitUntil(() => process.IsDisposed);
        Assert.Equal(0, process.Kills);
        Assert.Empty(app.Dialogs.Messages);
        Assert.Empty(app.Failures);
    }

    [AvaloniaFact]
    public async Task Button_in_the_right_panel_uses_its_folder_and_cursor()
    {
        var processes = new FakeProcessRunner();
        var open = new UserButton("Open", "~/bin/show.app", "%n %f");
        await using var app = await StartAsync(processes, buttons: open);
        await app.Right.NavigateToAsync(VfsPath.Local("/home/docs"));
        app.Main.Workspace.SwitchPanel();
        app.Right.CursorIndex = 0; // ".." gives no item: %n and %f are left out

        await app.Main.ButtonBar.RunAsync(open);

        var spec = Assert.Single(processes.Started);
        Assert.Equal("/usr/bin/open", spec.FileName);
        Assert.Equal(["-a", TestApp.Home + "/bin/show.app"], spec.Arguments);
        Assert.Equal("/home/docs", spec.WorkingDirectory);
    }

    [AvaloniaFact]
    public async Task Start_failure_shows_message()
    {
        var processes = new FakeProcessRunner
        {
            StartFailure = new Win32Exception("No such file or directory"),
        };
        await using var app = await StartAsync(processes, buttons: Zip);
        app.Left.CursorIndex = IndexOf(app.Left, "notes.txt");

        await app.Main.ButtonBar.RunAsync(Zip);

        Assert.Equal([(Loc.Format("UserCommand_StartFailed", "Zip"), "No such file or directory")],
            app.Dialogs.MessagesWithDetail);
        Assert.Empty(app.Failures);
    }

    [AvaloniaFact]
    public async Task Application_that_cannot_be_launched_is_told_with_the_launchers_words()
    {
        var processes = new FakeProcessRunner();
        var button = new UserButton("Show", "/Applications/Missing.app", "%f");
        await using var app = await StartAsync(processes, buttons: button);
        app.Left.CursorIndex = IndexOf(app.Left, "notes.txt");

        await app.Main.ButtonBar.RunAsync(button);
        processes.Last.Write("Unable to find application named 'Missing'");
        processes.Last.Exit(1);
        await TestApp.WaitUntil(() =>
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            return app.Dialogs.Messages.Count > 0;
        });

        Assert.Equal([(Loc.Format("UserCommand_StartFailed", "Show"), "Unable to find application named 'Missing'")],
            app.Dialogs.MessagesWithDetail);
        Assert.Empty(app.Failures);
    }

    [AvaloniaFact]
    public async Task Command_needing_the_other_folder_is_not_started_when_that_panel_is_not_local()
    {
        var processes = new FakeProcessRunner();
        var copy = new UserButton("Copy", "/bin/cp", "%s %t");
        await using var app = await StartAsync(processes, buttons: copy);
        await app.Right.NavigateToAsync(new VfsPath("remote", "/shared"));
        app.Left.Rows.Single(r => r.Name == "notes.txt").IsSelected = true;
        app.Left.Rows.Single(r => r.Name == "report.pdf").IsSelected = true;
        Assert.True(app.Main.ButtonBar.CanRun);

        await app.Main.ButtonBar.RunAsync(copy);

        Assert.Empty(processes.Started);
        Assert.Equal([Loc.Format("UserCommand_OtherPanelNotLocal", "Copy")], app.Dialogs.Messages);
        Assert.Empty(app.Failures);

        // A command that does not use the other folder still runs.
        var zip = new UserButton("Zip", "/usr/bin/zip", "-r out.zip %s");
        await app.Main.ButtonBar.RunAsync(zip);
        Assert.Single(processes.Started);
    }

    [AvaloniaFact]
    public async Task Buttons_cannot_run_on_a_non_local_panel()
    {
        var processes = new FakeProcessRunner();
        await using var app = await StartAsync(processes, buttons: Zip);
        var bar = app.Main.ButtonBar;
        var changes = new List<string?>();
        bar.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        await app.Left.NavigateToAsync(new VfsPath("remote", "/shared"));

        Assert.False(bar.CanRun);
        Assert.Contains(nameof(ButtonBarViewModel.CanRun), changes);
        Assert.False(bar.RunCommand.CanExecute(Zip));
        await bar.RunAsync(Zip);
        Assert.Empty(processes.Started);

        // The other panel is local: switching to it makes the buttons work again.
        app.Main.Workspace.SwitchPanel();
        Assert.True(bar.CanRun);
        Assert.True(bar.RunCommand.CanExecute(Zip));
    }

    [AvaloniaFact]
    public async Task Button_bar_follows_settings_and_view_toggle()
    {
        await using var app = await StartAsync(new FakeProcessRunner(), show: true);
        var bar = app.Find<ButtonBar>("ButtonBar");
        var host = app.Find<Control>("ButtonBarHost");
        Assert.True(host.IsEffectivelyVisible);
        Assert.True(app.Main.Workspace.View.ButtonBar);

        // No buttons yet: one button leads to the settings (not available before the settings window exists).
        var configure = Assert.Single(UserButtons(bar));
        Assert.Equal("ConfigureButtons", configure.Name);
        Assert.Equal(CommandIds.AppSettings, configure.CommandParameter);
        Assert.Equal(app.Main.Commands.CanExecute(CommandIds.AppSettings), configure.IsEffectivelyEnabled);

        var changed = 0;
        app.Main.ButtonBar.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ButtonBarViewModel.Buttons)) changed++;
        };
        app.Main.Settings.Update(app.Main.Settings.Current with
        {
            Buttons = [Zip, new UserButton("Diff", "/usr/bin/diff", "%f %t/%n")],
        });
        app.Pump();

        Assert.Equal(1, changed);
        Assert.Equal(["Zip", "Diff"], UserButtons(bar).Select(Title));
        Assert.All(UserButtons(bar), b => Assert.True(b.IsEffectivelyEnabled));

        // A change elsewhere in the settings leaves the buttons alone.
        app.Main.Settings.Update(app.Main.Settings.Current with { FontSize = 16 });
        Assert.Equal(1, changed);

        var item = ViewItem(app, "Menu_ButtonBar");
        Assert.True(item.IsChecked);
        item.Command!.Execute(null);
        app.Pump();

        Assert.False(app.Main.Workspace.View.ButtonBar);
        Assert.False(host.IsEffectivelyVisible);
        Assert.False(item.IsChecked);

        app.Main.ToggleButtonBar();
        app.Pump();
        Assert.True(host.IsEffectivelyVisible);
        Assert.True(item.IsChecked);
    }

    [AvaloniaFact]
    public async Task Clicking_a_button_runs_it()
    {
        var processes = new FakeProcessRunner();
        await using var app = await StartAsync(processes, show: true, buttons: Zip);
        app.Left.CursorIndex = IndexOf(app.Left, "notes.txt");
        var button = Assert.Single(UserButtons(app.Find<ButtonBar>("ButtonBar")));

        app.Click(button);
        await TestApp.WaitUntil(() => processes.Started.Count == 1);

        Assert.Equal(["-r", "/home/out.zip", "/home/notes.txt"], processes.Started[0].Arguments);
    }

    [AvaloniaFact]
    public async Task Button_bar_hidden_in_the_saved_state_stays_hidden()
    {
        var state = AppState.Default(TestApp.Home) with
        {
            Language = "en",
            View = new ViewState(ButtonBar: false),
        };
        await using var app = await TestApp.StartAsync(state: state);

        Assert.False(app.Find<Control>("ButtonBarHost").IsEffectivelyVisible);
    }

    private static string Title(Button button) =>
        button.GetVisualDescendants().OfType<TextBlock>().First().Text ?? "";

    private static List<Button> UserButtons(ButtonBar bar) =>
        bar.GetVisualDescendants().OfType<Button>().Where(b => b.IsVisible).ToList();

    private static NativeMenuItem ViewItem(TestApp app, string labelKey) =>
        NativeMenu.GetMenu(app.Window)!.Items.OfType<NativeMenuItem>().Single(i => i.Header == Loc.Get("Menu_View"))
            .Menu!.Items.OfType<NativeMenuItem>().Single(i => i.Header == Loc.Get(labelKey));
}
