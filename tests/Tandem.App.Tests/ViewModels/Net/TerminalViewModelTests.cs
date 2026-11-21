using System.Text;
using System.Threading.Channels;
using Avalonia.Headless.XUnit;
using Avalonia.Headless;
using Avalonia.Input;
using Tandem.App.Services;
using Tandem.App.ViewModels.Net;
using Tandem.App.Views.Net;
using Tandem.App.Tests.Views;
using Tandem.Net.Ssh;

namespace Tandem.App.Tests.ViewModels.Net;

public sealed class TerminalViewModelTests
{
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
        clipboard.Text = "one\r\ntwo";
        await model.SecondaryClickAsync();
        Assert.Equal("\e[200~one\rtwo\e[201~",
            await transport.ReadSentAsync(TestContext.Current.CancellationToken));
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
            window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, "");
            window.KeyRelease(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, "");
            var ct = TestContext.Current.CancellationToken;
            Assert.Equal("й", await transport.ReadSentAsync(ct));
            Assert.Equal("\e[A", await transport.ReadSentAsync(ct));

            window.KeyPress(Key.W, RawInputModifiers.Meta, PhysicalKey.W, "w");
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
        public Task<string?> GetTextAsync() => Task.FromResult(Text);
        public Task SetTextAsync(string text) { Text = text; return Task.CompletedTask; }
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
