using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tandem.App.Services;
using Tandem.App.Views.Dialogs;

namespace Tandem.App.Tests.Views;

/// <summary>The tool windows of the real <see cref="ToolWindows"/>, shown without a screen; the models and windows
/// here are test ones, registered through <see cref="ToolWindows.Register{TViewModel}"/>.</summary>
[Collection(LanguageCollection.Name)]
public class ToolWindowsTests : LanguageTestBase
{
    private sealed class DialogModel : IDialogModel<string>, IDisposable
    {
        public event EventHandler<string?>? CloseRequested;

        public bool Disposed => Disposals > 0;

        public int Disposals { get; private set; }

        public void Finish(string? result) => CloseRequested?.Invoke(this, result);

        public void Dispose() => Disposals++;
    }

    private sealed class ToolModel : IAsyncDisposable
    {
        public int Disposals { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposals++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class GuardedModel : ICloseGuard, IDisposable
    {
        public Queue<bool> Answers { get; } = new();

        public int Asked { get; private set; }

        public bool Disposed { get; private set; }

        public Task<bool> CanCloseAsync()
        {
            Asked++;
            return Task.FromResult(Answers.Count > 0 && Answers.Dequeue());
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class ModelWindow : Window
    {
        public ModelWindow(object model)
        {
            DataContext = model;
            Width = 300;
            Height = 200;
        }
    }

    private static async Task<TestApp> StartAsync()
    {
        var app = await TestApp.StartAsync();
        var tools = app.ToolWindows;
        tools.Register<DialogModel>(m => new ModelWindow(m));
        tools.Register<ToolModel>(m => new ModelWindow(m));
        tools.Register<GuardedModel>(m => new ModelWindow(m));
        return app;
    }

    private static async Task<T> Finish<T>(Task<T> task)
    {
        await TestApp.WaitUntil(() =>
        {
            Dispatcher.UIThread.RunJobs();
            return task.IsCompleted;
        });
        return await task;
    }

    private static void Press(Window window, Key key)
    {
        window.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, null);
        if (window.IsVisible) window.KeyRelease(key, RawInputModifiers.None, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
    }

    private static T Owned<T>(Window owner) where T : Window
    {
        Dispatcher.UIThread.RunJobs();
        return owner.OwnedWindows.OfType<T>().Single();
    }

    private static Button Button(Window window, string name) =>
        window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == name);

    private static void Click(Button button)
    {
        button.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task Dialog_model_closes_its_window_with_the_result()
    {
        await using var app = await StartAsync();
        var tools = app.ToolWindows;

        var model = new DialogModel();
        var showing = tools.ShowDialogAsync(model);
        var window = Owned<ModelWindow>(app.Window); // modal to the main window
        Assert.Same(model, window.DataContext);
        Assert.True(tools.HasOpenWindows);
        model.Finish("done");
        Assert.Equal("done", await Finish(showing));
        Assert.False(window.IsVisible);
        Assert.True(model.Disposed);
        Assert.False(tools.HasOpenWindows);

        // Closed with the close button or with Esc: the default answer.
        showing = tools.ShowDialogAsync(new DialogModel());
        Owned<ModelWindow>(app.Window).Close();
        Assert.Null(await Finish(showing));
        showing = tools.ShowDialogAsync(new DialogModel());
        Press(Owned<ModelWindow>(app.Window), Key.Escape);
        Assert.Null(await Finish(showing));

        // Modal to the window given.
        var owner = new Window { Width = 400, Height = 300 };
        owner.Show();
        model = new DialogModel();
        showing = tools.ShowDialogAsync(model, owner);
        Assert.Same(model, Owned<ModelWindow>(owner).DataContext);
        Assert.Empty(app.Window.OwnedWindows.OfType<ModelWindow>());
        model.Finish(null);
        Assert.Null(await Finish(showing));
        owner.Close();
    }

    [AvaloniaFact]
    public async Task A_dialog_asked_for_a_model_belongs_to_that_model_s_window()
    {
        await using var app = await StartAsync();
        var tools = app.ToolWindows;
        var tool = new ToolModel();
        tools.Show(tool);
        var toolWindow = Assert.Single(tools.Open);

        var asking = tools.ShowDialogAsync(new ConfirmModel("Synchronize?", null, "Synchronize"), ownerModel: tool);
        var dialog = Owned<ConfirmDialog>(toolWindow);
        Assert.Empty(app.Window.OwnedWindows.OfType<ConfirmDialog>());
        Press(dialog, Key.Escape);
        Assert.False(await Finish(asking));

        // A model without an open window: the main window.
        asking = tools.ShowDialogAsync(new ConfirmModel("Go?", null, "Go"), ownerModel: new ToolModel());
        Press(Owned<ConfirmDialog>(app.Window), Key.Escape);
        Assert.False(await Finish(asking));
    }

    [AvaloniaFact]
    public async Task Asking_the_same_dialog_model_again_brings_its_window_forward()
    {
        await using var app = await StartAsync();
        var tools = app.ToolWindows;
        var model = new DialogModel();

        var first = tools.ShowDialogAsync(model);
        var window = Owned<ModelWindow>(app.Window);
        var second = tools.ShowDialogAsync(model);
        Dispatcher.UIThread.RunJobs();

        Assert.Same(window, Owned<ModelWindow>(app.Window)); // still one window
        Assert.Single(tools.Open);
        model.Finish("done");
        Assert.Equal("done", await Finish(first));
        Assert.Equal("done", await Finish(second));
        Assert.Equal(1, model.Disposals);
    }

    [AvaloniaFact]
    public async Task Confirm_model_answers_true_on_its_button_and_false_on_cancel_or_Escape()
    {
        await using var app = await StartAsync();
        var tools = app.ToolWindows;
        var question = new ConfirmModel("Restore 3 names?", "They were renamed a moment ago.", "Restore");

        var asking = tools.ShowDialogAsync(question);
        var dialog = Owned<ConfirmDialog>(app.Window);
        var texts = dialog.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("Restore 3 names?", texts);
        Assert.Contains("They were renamed a moment ago.", texts);
        Assert.Equal("Restore", Button(dialog, "Confirm").Content);
        Assert.True(Button(dialog, "Cancel").IsDefault); // cancelIsDefault: Enter does not go ahead
        Assert.False(Button(dialog, "Confirm").IsDefault);
        Click(Button(dialog, "Confirm"));
        Assert.True(await Finish(asking));

        asking = tools.ShowDialogAsync(question);
        Click(Button(Owned<ConfirmDialog>(app.Window), "Cancel"));
        Assert.False(await Finish(asking));

        asking = tools.ShowDialogAsync(question);
        Press(Owned<ConfirmDialog>(app.Window), Key.Escape);
        Assert.False(await Finish(asking));

        asking = tools.ShowDialogAsync(new ConfirmModel("Close?", null, "Close", cancelIsDefault: false));
        dialog = Owned<ConfirmDialog>(app.Window);
        Assert.True(Button(dialog, "Confirm").IsDefault);
        Press(dialog, Key.Enter);
        Assert.True(await Finish(asking));
    }

    [AvaloniaFact]
    public async Task Close_guard_can_keep_its_window_open_and_stops_CloseAll()
    {
        await using var app = await StartAsync();
        var tools = app.ToolWindows;
        var guarded = new GuardedModel();
        tools.Show(guarded);
        var window = Assert.IsType<ModelWindow>(Assert.Single(tools.Open));

        window.Close(); // the guard says no
        await TestApp.WaitUntil(() => guarded.Asked == 1);
        app.Pump();
        Assert.True(window.IsVisible);
        Assert.False(guarded.Disposed);

        Assert.False(await tools.CloseAllAsync());
        Assert.Equal(2, guarded.Asked);
        Assert.True(window.IsVisible);
        Assert.True(tools.HasOpenWindows);

        guarded.Answers.Enqueue(true);
        Assert.True(await tools.CloseAllAsync());
        Assert.False(window.IsVisible);
        Assert.True(guarded.Disposed);
        Assert.False(tools.HasOpenWindows);

        // A window whose guard agrees closes on its own close too.
        guarded = new GuardedModel();
        guarded.Answers.Enqueue(true);
        tools.Show(guarded);
        window = Assert.IsType<ModelWindow>(Assert.Single(tools.Open));
        window.Close();
        await TestApp.WaitUntil(() =>
        {
            app.Pump();
            return !window.IsVisible;
        });
        Assert.True(guarded.Disposed);
    }

    [AvaloniaFact]
    public void Showing_the_same_model_twice_keeps_one_window()
    {
        var tools = new ToolWindows();
        tools.Register<ToolModel>(m => new ModelWindow(m));
        var model = new ToolModel();

        tools.Show(model);
        var window = Assert.Single(tools.Open);
        tools.Show(model);
        Assert.Same(window, Assert.Single(tools.Open));
        Assert.True(window.IsVisible);

        var other = new ToolModel();
        tools.Show(other);
        Assert.Equal(2, tools.Open.Count);
        Assert.Throws<InvalidOperationException>(() => tools.Show(new object())); // no window for that model

        window.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, model.Disposals);
        Assert.Equal(0, other.Disposals);
        tools.Open.Single().Close();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, other.Disposals);
        Assert.False(tools.HasOpenWindows);
    }

    [AvaloniaFact]
    public async Task Closing_the_app_closes_tool_windows()
    {
        await using var app = await StartAsync();
        var tools = app.ToolWindows;
        var model = new ToolModel();
        var guarded = new GuardedModel();
        tools.Show(model);
        tools.Show(guarded);
        Assert.True(app.Main.HasOpenWindows);

        // A guard that says no keeps the app open.
        app.Window.Close();
        await TestApp.WaitUntil(() => guarded.Asked == 1);
        app.Pump();
        Assert.True(app.Window.IsVisible);
        Assert.Single(tools.Open);

        guarded.Answers.Enqueue(true);
        app.Window.Close();
        await TestApp.WaitUntil(() =>
        {
            app.Pump();
            return !app.Window.IsVisible;
        });
        Assert.Empty(tools.Open);
        Assert.Equal(1, model.Disposals);
        Assert.True(guarded.Disposed);
    }

    [AvaloniaFact]
    public async Task Scripted_tool_windows_record_models_and_answer_dialogs()
    {
        await using var app = await TestApp.StartAsync(show: false, scriptedToolWindows: true);
        var tools = app.ScriptedToolWindows;
        Assert.Same(tools, app.Main.Services.ToolWindows);
        var question = new ConfirmModel("Go?", null, "Go");

        Assert.False(await tools.ShowDialogAsync(question)); // unanswered: the default
        tools.OnDialog = model => model is ConfirmModel ? true : null;
        Assert.True(await tools.ShowDialogAsync(question));
        var model = new ToolModel();
        tools.Show(model);

        Assert.Equal([question, question, model], tools.Shown);
        Assert.True(tools.HasOpenWindows);
        Assert.True(await tools.CloseAllAsync());
        Assert.Equal(1, model.Disposals);
        Assert.False(tools.HasOpenWindows);

        // A model shown non-modally that asks to close is closed and disposed, as by the real windows.
        var dialogModel = new DialogModel();
        tools.Show(dialogModel);
        Assert.True(tools.HasOpenWindows);
        dialogModel.Finish("done");
        Assert.False(tools.HasOpenWindows);
        Assert.Equal(1, dialogModel.Disposals);

        // The owner model is recorded.
        await tools.ShowDialogAsync(question, ownerModel: model);
        Assert.Same(model, tools.Dialogs[^1].OwnerModel);
    }

    [AvaloniaFact]
    public async Task Disposing_the_test_app_fails_when_a_tool_window_refuses_to_close()
    {
        var app = await StartAsync();
        app.ToolWindows.Show(new GuardedModel()); // says no to every close

        var failure = await Assert.ThrowsAsync<Xunit.Sdk.FailException>(() => app.DisposeAsync().AsTask());
        Assert.Contains("refused to close", failure.Message);
    }
}
