using System.Text;
using System.Threading.Channels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Tandem.App.Services;
using Tandem.App.ViewModels.Net;
using Tandem.App.Views.Net;
using Tandem.App.Tests.Views;
using Tandem.Net.Ssh;

namespace Tandem.App.Tests.ViewModels.Net;

public sealed class TerminalViewModelTests
{
    [AvaloniaFact]
    public async Task Main_window_button_restores_the_minimized_terminal()
    {
        await using var app = await TestApp.StartAsync();
        var button = app.Find<Button>("ShowSshConsole");
        Assert.False(button.IsEffectivelyVisible);

        var model = new TerminalViewModel("Local test", () => new Transport(), new Clipboard(),
            _ => Task.FromResult(true), _ => { });
        app.ToolWindows.Show(model);
        var window = Assert.IsType<TerminalWindow>(Assert.Single(app.ToolWindows.Open));
        window.WindowState = WindowState.Minimized;
        app.Pump();
        Assert.True(button.IsEffectivelyVisible);

        app.Click(button);

        Assert.Same(window, Assert.Single(app.ToolWindows.Open));
        Assert.Equal(WindowState.Normal, window.WindowState);
        Assert.True(await app.ToolWindows.CloseAllAsync());
        app.Pump();
        Assert.False(button.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void Terminal_window_keeps_its_dark_palette_and_can_be_pinned()
    {
        var parent = new Window { RequestedThemeVariant = ThemeVariant.Light };
        var window = new TerminalWindow();
        parent.Show();
        window.Show(parent);
        try
        {
            var terminal = window.FindControl<TerminalView>("ConsoleView")!.FindControl<TerminalControl>("Terminal")!;
            var pin = window.FindControl<TerminalView>("ConsoleView")!
                .FindControl<Avalonia.Controls.Primitives.ToggleButton>("KeepOnTop")!;
            Assert.Equal(ThemeVariant.Dark, terminal.ActualThemeVariant);
            Assert.True(terminal.TryFindResource("Tandem.TerminalBackground", terminal.ActualThemeVariant, out var brush));
            Assert.Equal(Colors.Black, Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color);
            Assert.False(window.Topmost);
            pin.IsChecked = true;
            Assert.True(window.Topmost);
            parent.RequestedThemeVariant = ThemeVariant.Dark;
            Assert.Equal(ThemeVariant.Dark, terminal.ActualThemeVariant);
            pin.IsChecked = false;
            Assert.False(window.Topmost);
        }
        finally
        {
            window.Close();
            parent.Close();
        }
    }

    [AvaloniaFact]
    public async Task Right_click_copies_then_pastes_as_terminal_input()
    {
        var transport = new Transport();
        var clipboard = new Clipboard();
        await using var model = new TerminalViewModel("Test", () => transport, clipboard, _ => Task.FromResult(true),
            e => throw new InvalidOperationException("Unexpected terminal failure", e));
        await model.StartAsync(20, 6);
        lock (model.ScreenLock) model.Screen.Feed("hello"u8);
        model.SelectRange((0, 0), (4, 0));

        await model.SecondaryClickAsync();
        Assert.Equal("hello", clipboard.Text);
        Assert.Null(model.Selection);
        Assert.False(transport.Sent.Reader.TryRead(out _));

        lock (model.ScreenLock) model.Screen.Feed("\e[?2004h"u8);
        clipboard.Text = "one\r\ntwo\r\n";
        await model.SecondaryClickAsync();
        Assert.Equal("\e[200~one\rtwo\e[201~",
            await transport.ReadSentAsync(TestContext.Current.CancellationToken));
    }

    [AvaloniaFact]
    public async Task Right_click_copies_selected_rows_then_pastes_without_sending_enter()
    {
        var transport = new Transport();
        var clipboard = new Clipboard { Text = "previous clipboard" };
        await using var model = new TerminalViewModel("Test", () => transport, clipboard, _ => Task.FromResult(true),
            e => throw new InvalidOperationException("Unexpected terminal failure", e));
        var window = new TerminalWindow(model);
        window.Show();
        try
        {
            await TestApp.WaitUntil(() => model.State == TerminalState.Connected);
            lock (model.ScreenLock) model.Screen.Feed("first\r\nsecond\r\n"u8);
            model.SelectRange((0, 0), (0, 2));

            var terminal = window.FindControl<TerminalView>("ConsoleView")!.FindControl<TerminalControl>("Terminal")!;
            var point = terminal.TranslatePoint(new Point(5, 5), window)!.Value;
            window.MouseDown(point, MouseButton.Right);
            window.MouseUp(point, MouseButton.Right);
            Assert.Equal("first\nsecond\n", clipboard.Text);
            Assert.False(transport.Sent.Reader.TryRead(out _));

            window.MouseDown(point, MouseButton.Right);
            window.MouseUp(point, MouseButton.Right);
            Assert.Equal("first second", await transport.ReadSentAsync(TestContext.Current.CancellationToken));
            Assert.False(transport.Sent.Reader.TryRead(out _));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task A_second_right_click_waits_for_the_first_click_to_finish_copying()
    {
        var transport = new Transport();
        var copying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clipboard = new Clipboard { Text = "previous clipboard", FinishWriting = copying.Task };
        await using var model = new TerminalViewModel("Test", () => transport, clipboard, _ => Task.FromResult(true),
            e => throw new InvalidOperationException("Unexpected terminal failure", e));
        await model.StartAsync(20, 6);
        lock (model.ScreenLock) model.Screen.Feed("selected"u8);
        model.SelectRange((0, 0), (7, 0));

        var first = model.SecondaryClickAsync();
        var second = model.SecondaryClickAsync();
        copying.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, clipboard.Writes);
        Assert.Equal("selected", await transport.ReadSentAsync(TestContext.Current.CancellationToken));
    }

    [AvaloniaFact]
    public async Task Connected_terminal_asks_before_closing()
    {
        var transport = new Transport();
        var confirmations = 0;
        await using var model = new TerminalViewModel("Test", () => transport, new Clipboard(), _ =>
        {
            confirmations++;
            return Task.FromResult(false);
        }, _ => { });
        await model.StartAsync(20, 6);
        Assert.Equal(TerminalState.Connected, model.State);
        Assert.False(await model.CanCloseAsync());
        Assert.Equal(1, confirmations);
    }

    [AvaloniaFact]
    public async Task Terminal_window_sends_text_and_arrow_input_and_closes_with_command_w()
    {
        var transport = new Transport();
        await using var model = new TerminalViewModel("Test", () => transport, new Clipboard(), _ => Task.FromResult(true),
            _ => { });
        var window = new TerminalWindow(model);
        window.Show();
        try
        {
            await TestApp.WaitUntil(() => model.State == TerminalState.Connected);
            window.KeyTextInput("й");
            window.KeyPress(Key.Up, TestKeys.Platform(RawInputModifiers.None), PhysicalKey.ArrowUp, "");
            window.KeyRelease(Key.Up, TestKeys.Platform(RawInputModifiers.None), PhysicalKey.ArrowUp, "");
            var ct = TestContext.Current.CancellationToken;
            Assert.Equal("й", await transport.ReadSentAsync(ct));
            Assert.Equal("\e[A", await transport.ReadSentAsync(ct));

            window.KeyPress(Key.W, TestKeys.Platform(RawInputModifiers.Meta), PhysicalKey.W, "w");
            await TestApp.WaitUntil(() => !window.IsVisible);
        }
        finally
        {
            if (window.IsVisible) window.Close();
        }
    }

    private sealed class Clipboard : ITerminalClipboard
    {
        public string? Text { get; set; }
        public Task FinishWriting { get; init; } = Task.CompletedTask;
        public int Writes { get; private set; }
        public Task<string?> GetTextAsync() => Task.FromResult(Text);
        public async Task SetTextAsync(string text)
        {
            Writes++;
            await FinishWriting;
            Text = text;
        }
    }

    private sealed class Transport : ITerminalTransport
    {
        public Channel<string> Sent { get; } = Channel.CreateUnbounded<string>();
        public Task<string> ReadSentAsync(CancellationToken ct) =>
            Sent.Reader.ReadAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(5), ct);
        public Task ConnectAsync(int columns, int rows, CancellationToken ct) => Task.CompletedTask;
        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }
        public Task ResizeAsync(int columns, int rows, CancellationToken ct) => Task.CompletedTask;
        public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct) =>
            Sent.Writer.WriteAsync(Encoding.UTF8.GetString(data.Span), ct).AsTask();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
