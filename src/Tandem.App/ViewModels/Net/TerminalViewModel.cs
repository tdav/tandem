using System.Threading.Channels;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tandem.App.Resources;
using Tandem.App.Services;
using Tandem.Core.Settings;
using Tandem.Core.Vfs;
using Tandem.Net;
using Tandem.Net.Ssh;
using Tandem.Net.Terminal;

namespace Tandem.App.ViewModels.Net;

public enum TerminalState { Connecting, Connected, Ended, Failed }

public sealed partial class TerminalViewModel : ObservableObject, ICloseGuard, IAsyncDisposable
{
    private readonly string _name;
    private readonly Func<ITerminalTransport> _open;
    private readonly ITerminalClipboard _clipboard;
    private readonly Func<ConfirmModel, Task<bool>> _confirm;
    private readonly Action<Exception> _reportFailure;
    private readonly DispatcherTimer _redraw = new() { Interval = TimeSpan.FromMilliseconds(17) };
    private readonly DispatcherTimer _resize = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private CancellationTokenSource? _lifetime;
    private ChannelWriter<Func<ITerminalTransport, CancellationToken, Task>>? _requests;
    private Task? _run;
    private TerminalState _state = TerminalState.Connecting;
    private string? _errorKey;
    private int _scrollOffset;
    private int _dirty;
    private bool _disposed;

    public TerminalViewModel(string title, Func<ITerminalTransport> open, ITerminalClipboard clipboard,
        Func<ConfirmModel, Task<bool>> confirm, Action<Exception> reportFailure)
    {
        _name = title;
        _open = open;
        _clipboard = clipboard;
        _confirm = confirm;
        _reportFailure = reportFailure;
        Screen = new TerminalEmulator(80, 24);
        RegisterScreen();
        _redraw.Tick += (_, _) => Redraw();
        _resize.Tick += (_, _) =>
        {
            _resize.Stop();
            int columns, rows;
            lock (ScreenLock) (columns, rows) = (Screen.Columns, Screen.Rows);
            Enqueue((transport, ct) => transport.ResizeAsync(columns, rows, ct));
        };
    }

    public TerminalEmulator Screen { get; private set; }
    public object ScreenLock { get; } = new();
    public double FontSize { get; init; } = AppSettings.DefaultFontSize;
    public TerminalState State => _state;
    public string Title
    {
        get
        {
            lock (ScreenLock)
                return Screen.Title.Length == 0 ? _name : Loc.Format("Terminal_Title", _name,
                    new string(Screen.Title.Where(c => !char.IsControl(c)).Take(200).ToArray()));
        }
    }

    public string? StatusText => State == TerminalState.Connected ? null : _errorKey is { } key
        ? Loc.Get(key) : Loc.Enum(State);
    public bool HasStatus => StatusText is not null;
    public bool CanReconnect => State is TerminalState.Ended or TerminalState.Failed;

    public int ScrollOffset
    {
        get => _scrollOffset;
        set
        {
            int max;
            lock (ScreenLock) max = Screen.ScrollbackCount;
            if (SetProperty(ref _scrollOffset, Math.Clamp(value, 0, max))) ScreenChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public ((int Column, int Line) From, (int Column, int Line) To)? Selection { get; private set; }
    public event EventHandler? ScreenChanged;

    public async Task StartAsync(int columns, int rows)
    {
        if (_disposed || _run is { IsCompleted: false }) return;
        lock (ScreenLock)
        {
            Screen.Dispose();
            Screen = new TerminalEmulator(columns, rows);
            RegisterScreen();
        }
        ScrollOffset = 0;
        ClearSelection();
        var lifetime = new CancellationTokenSource();
        var requests = Channel.CreateUnbounded<Func<ITerminalTransport, CancellationToken, Task>>(
            new UnboundedChannelOptions { SingleReader = true });
        _lifetime = lifetime;
        _requests = requests.Writer;
        ChangeState(TerminalState.Connecting);
        _redraw.Start();
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _run = RunAsync(lifetime, requests, columns, rows, connected);
        await connected.Task;
    }

    public void Resize(int columns, int rows)
    {
        if (_disposed) return;
        lock (ScreenLock)
        {
            if ((columns, rows) == (Screen.Columns, Screen.Rows)) return;
            Screen.Resize(columns, rows);
        }
        ScrollOffset = ScrollOffset;
        ClearSelection();
        if (!_resize.IsEnabled) _resize.Start();
    }

    public void SendKey(TerminalKey key, TerminalModifiers modifiers)
    {
        bool application;
        lock (ScreenLock) application = Screen.ApplicationCursorKeys;
        Send(TerminalInput.Key(key, modifiers, application));
    }

    public void SendText(string text, TerminalModifiers modifiers) => Send(TerminalInput.Text(text, modifiers));

    public void SelectRange((int Column, int Line) from, (int Column, int Line) to)
    {
        Selection = (from, to);
        ScreenChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SelectWordAt(int column, int line)
    {
        var start = column;
        var end = column;
        lock (ScreenLock)
        {
            if (start > 0 && Screen.CellAt(start, line).Width == 0) start--;
            var wordCharacter = IsWord(start);
            if (wordCharacter)
            {
                while (start > 0 && IsWord(start - 1)) start--;
                while (end + 1 < Screen.Columns && IsWord(end + 1)) end++;
            }
        }
        SelectRange((start, line), (end, line));

        bool IsWord(int index)
        {
            var cell = Screen.CellAt(index, line);
            if (cell.Width == 0 && index > 0) cell = Screen.CellAt(index - 1, line);
            return cell.Text.Length != 0 && (char.IsLetterOrDigit(cell.Text[0]) || cell.Text[0] == '_');
        }
    }

    public void SelectLineAt(int line)
    {
        int lastColumn;
        lock (ScreenLock) lastColumn = Screen.Columns - 1;
        SelectRange((0, line), (lastColumn, line));
    }

    public void ClearSelection()
    {
        if (Selection is null) return;
        Selection = null;
        ScreenChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task CopySelectionAsync()
    {
        if (Selection is not { } selection) return Task.CompletedTask;
        string text;
        lock (ScreenLock) text = TerminalSelectionText.Extract(Screen, selection.From, selection.To);
        return _clipboard.SetTextAsync(text);
    }

    public async Task PasteAsync()
    {
        if (State != TerminalState.Connected) return;
        var text = await _clipboard.GetTextAsync();
        if (string.IsNullOrEmpty(text)) return;
        bool bracketed;
        lock (ScreenLock) bracketed = Screen.BracketedPaste;
        Send(TerminalInput.Paste(text, bracketed));
    }

    public async Task SecondaryClickAsync()
    {
        if (Selection is null) { await PasteAsync(); return; }
        await CopySelectionAsync();
        ClearSelection();
    }

    [RelayCommand]
    public Task ReconnectAsync()
    {
        int columns, rows;
        lock (ScreenLock) (columns, rows) = (Screen.Columns, Screen.Rows);
        return CanReconnect ? StartAsync(columns, rows) : Task.CompletedTask;
    }

    public Task<bool> CanCloseAsync() => State != TerminalState.Connected ? Task.FromResult(true) :
        _confirm(new ConfirmModel(Loc.Get("Terminal_CloseQuestion"), Loc.Get("Terminal_CloseDetail"),
            Loc.Get("Terminal_CloseButton")));

    internal void AttachClipboard(TopLevel? window)
    {
        if (_clipboard is WindowTerminalClipboard clipboard) clipboard.Window = window;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _redraw.Stop();
        _resize.Stop();
        _lifetime?.Cancel();
        if (_run is not null) await _run;
        lock (ScreenLock) Screen.Dispose();
        AttachClipboard(null);
    }

    private void RegisterScreen()
    {
        Screen.Reply += data => Enqueue((transport, ct) => transport.WriteAsync(data, ct));
    }

    private async Task RunAsync(CancellationTokenSource lifetime,
        Channel<Func<ITerminalTransport, CancellationToken, Task>> requests,
        int columns, int rows, TaskCompletionSource connected)
    {
        try
        {
            await using var transport = _open();
            await Task.Run(() => transport.ConnectAsync(columns, rows, lifetime.Token), lifetime.Token);
            ChangeState(TerminalState.Connected);
            lock (ScreenLock)
                if ((columns, rows) != (Screen.Columns, Screen.Rows)) _resize.Start();
            connected.TrySetResult();
            var reader = Task.Run(() => ReadAsync(transport, lifetime.Token));
            var writer = Task.Run(async () =>
            {
                await foreach (var request in requests.Reader.ReadAllAsync(lifetime.Token).ConfigureAwait(false))
                    await request(transport, lifetime.Token).ConfigureAwait(false);
            });
            var done = await Task.WhenAny(reader, writer);
            lifetime.Cancel();
            try { await done; }
            finally
            {
                try { await Task.WhenAll(reader, writer); }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
                catch when (done.IsFaulted) { }
            }
            ChangeState(TerminalState.Ended);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { ChangeState(TerminalState.Ended); }
        catch (NetAuthenticationException e) when (e.Declined) { ChangeState(TerminalState.Ended); }
        catch (Exception error)
        {
            var key = error switch
            {
                VfsConnectionLostException => "Terminal_Disconnected",
                NetAuthenticationException => "Terminal_AuthenticationFailed",
                NetTrustException => "Terminal_TrustRefused",
                _ => null,
            };
            if (key is null) _reportFailure(new IOException("SSH terminal failure."));
            ChangeState(TerminalState.Failed, key);
        }
        finally
        {
            requests.Writer.TryComplete();
            _requests = null;
            _lifetime = null;
            lifetime.Dispose();
            Redraw();
            _redraw.Stop();
            connected.TrySetResult();
        }
    }

    private async Task ReadAsync(ITerminalTransport transport, CancellationToken ct)
    {
        var buffer = new byte[32768];
        while (true)
        {
            var count = await transport.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (count == 0) return;
            lock (ScreenLock)
            {
                Screen.Feed(buffer.AsSpan(0, count));
                Interlocked.Exchange(ref _dirty, 1);
            }
        }
    }

    private void Send(byte[] bytes)
    {
        if (State != TerminalState.Connected || bytes.Length == 0) return;
        ScrollOffset = 0;
        ClearSelection();
        Enqueue((transport, ct) => transport.WriteAsync(bytes, ct));
    }

    private void Enqueue(Func<ITerminalTransport, CancellationToken, Task> request) =>
        _requests?.TryWrite(request);

    private void Redraw()
    {
        if (Interlocked.Exchange(ref _dirty, 0) == 0) return;
        OnPropertyChanged(nameof(Title));
        ScreenChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ChangeState(TerminalState state, string? errorKey = null)
    {
        _errorKey = errorKey;
        _state = state;
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(HasStatus));
        OnPropertyChanged(nameof(CanReconnect));
    }
}
