using System.ComponentModel;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tandem.App.Resources;
using Tandem.App.Services;
using Tandem.Core.Platform;
using Tandem.Core.Vfs;

namespace Tandem.App.ViewModels;

/// <summary>
/// The command line under the panels. <c>cd</c> moves the active panel (<see cref="CdCommandParser"/>); anything
/// else runs as <c>/bin/zsh -lc &lt;text&gt;</c> in the active panel's folder, one command at a time, its output
/// shown in the output pane. Only for a panel on the local disk.
/// </summary>
/// <remarks>
/// Created and called on the UI thread. Output is read in the background and shown in batches, at most once per
/// <see cref="FlushInterval"/>, so a chatty command cannot flood the UI; the last <see cref="MaxOutputLines"/> lines
/// are kept. <see cref="Dispose"/> kills a running command.
/// </remarks>
public sealed partial class CommandLineViewModel : ObservableObject, IDisposable
{
    public const int MaxOutputLines = 5000;
    public const int MaxHistory = 100;
    public const string Shell = "/bin/zsh";

    /// <summary>The shortest time between two updates of <see cref="Output"/> while a command writes.</summary>
    public static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(50);

    private readonly WorkspaceViewModel _workspace;
    private readonly IProcessRunner _runner;
    private readonly string _homeDirectory;
    private readonly Func<TimeSpan, CancellationToken, Task> _flushDelay;
    private readonly SynchronizationContext _ui;
    private readonly CancellationTokenSource _lifetime = new();

    private readonly List<string> _history = [];
    private int _historyIndex = -1; // -1: not walking the history
    private string _draft = "";
    private bool _settingFromHistory;

    private readonly Queue<string> _lines = new();
    private bool _droppedLines;

    private readonly Lock _pendingLock = new();
    private List<string> _pending = [];
    private bool _flushScheduled;

    private PanelViewModel? _panel;
    private IRunningProcess? _process;
    private bool _disposed;

    /// <param name="homeDirectory">Where <c>cd</c> and <c>cd ~</c> go.</param>
    /// <param name="flushDelay">Waits between output updates; <see cref="Task.Delay(TimeSpan, CancellationToken)"/>
    /// when null.</param>
    public CommandLineViewModel(WorkspaceViewModel workspace, IProcessRunner runner, string homeDirectory,
        Func<TimeSpan, CancellationToken, Task>? flushDelay = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentException.ThrowIfNullOrEmpty(homeDirectory);
        _ui = SynchronizationContext.Current
              ?? throw new InvalidOperationException("The command line must be created on the UI thread.");
        _workspace = workspace;
        _runner = runner;
        _homeDirectory = homeDirectory;
        _flushDelay = flushDelay ?? Task.Delay;
        workspace.PropertyChanged += OnWorkspacePropertyChanged;
        FollowActivePanel();
    }

    /// <summary>The text being typed. Changing it (other than by walking the history) ends the walk.</summary>
    [ObservableProperty]
    public partial string Text { get; set; } = "";

    /// <summary>The active panel's folder; empty when it shows none.</summary>
    [ObservableProperty]
    public partial string Prompt { get; private set; } = "";

    /// <summary>Whether commands can be typed: the active panel shows a folder on the local disk.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Placeholder))]
    public partial bool IsAvailable { get; private set; }

    /// <summary>What the empty field says: why it is disabled, or nothing.</summary>
    public string? Placeholder => IsAvailable ? null : Loc.Get("CommandLine_Unavailable");

    /// <summary>The commands entered this session, oldest first, at most <see cref="MaxHistory"/>; a command
    /// repeated straight after itself is kept once.</summary>
    public IReadOnlyList<string> History => _history;

    /// <summary>The output pane's text: the commands' output and the app's messages, one line each, the last
    /// <see cref="MaxOutputLines"/> of them (after a marker line once older ones have been dropped).</summary>
    [ObservableProperty]
    public partial string Output { get; private set; } = "";

    /// <summary>Whether a command is running; then no other command starts.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(KillCommand))]
    public partial bool IsRunning { get; private set; }

    /// <summary>Whether the output pane is open; it opens by itself when output arrives.</summary>
    [ObservableProperty]
    public partial bool IsOutputVisible { get; set; }

    /// <summary>
    /// Enter: runs the text. Blank text does nothing; nothing runs while a command is running or the command line is
    /// unavailable. A <c>cd</c> moves the active panel and clears the field; when it cannot, the reason goes to the
    /// output and the text stays for correction. Any other text is passed to zsh as one argument, exactly as typed;
    /// the returned task ends with the command, after which the panel that ran it is refreshed (if it still shows the
    /// same folder).
    /// </summary>
    public async Task ExecuteAsync()
    {
        var text = Text;
        if (string.IsNullOrWhiteSpace(text) || IsRunning || _disposed || !IsAvailable || _panel is not { } panel)
            return;
        Record(text);

        var folder = panel.CurrentPath;
        if (CdCommandParser.Parse(text, folder.Path, _homeDirectory) is { } cd)
            await ChangeFolderAsync(panel, cd);
        else
            await RunAsync(panel, folder, text);
    }

    /// <summary>Up in the field: the previous (older) history entry; the text being typed is kept for
    /// <see cref="HistoryNext"/>.</summary>
    public void HistoryPrevious()
    {
        if (_history.Count == 0) return;
        if (_historyIndex < 0)
        {
            _draft = Text;
            _historyIndex = _history.Count - 1;
        }
        else if (_historyIndex > 0)
        {
            _historyIndex--;
        }
        SetTextFromHistory(_history[_historyIndex]);
    }

    /// <summary>Down in the field: the next (newer) history entry; past the newest, the text that was being typed.</summary>
    public void HistoryNext()
    {
        if (_historyIndex < 0) return;
        if (++_historyIndex < _history.Count)
        {
            SetTextFromHistory(_history[_historyIndex]);
            return;
        }
        _historyIndex = -1;
        SetTextFromHistory(_draft);
    }

    /// <summary>Kills the running command; does nothing when none runs.</summary>
    [RelayCommand(CanExecute = nameof(IsRunning))]
    public void Kill() => _process?.Kill();

    /// <summary>Empties the output pane.</summary>
    [RelayCommand]
    public void ClearOutput()
    {
        _lines.Clear();
        _droppedLines = false;
        Output = "";
    }

    [RelayCommand]
    public void CloseOutput() => IsOutputVisible = false;

    /// <summary>Kills a running command (its process is disposed) and stops following the workspace.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _workspace.PropertyChanged -= OnWorkspacePropertyChanged;
        if (_panel is not null) _panel.PropertyChanged -= OnPanelPropertyChanged;
        _panel = null;
        _lifetime.Cancel();
        if (_process is { } process) _ = process.DisposeAsync().AsTask(); // kills at once, then drains in the background
    }

    partial void OnTextChanged(string value)
    {
        if (!_settingFromHistory) _historyIndex = -1;
    }

    // ---- cd -------------------------------------------------------------------------------------------------

    private async Task ChangeFolderAsync(PanelViewModel panel, CdCommand cd)
    {
        if (cd.Target is not { } targetPath)
        {
            AppendMessage(Loc.Get(cd.Problem switch
            {
                CdProblem.PreviousFolderUnsupported => "CommandLine_CdPreviousUnsupported",
                CdProblem.UserHomeUnsupported => "CommandLine_CdUserHomeUnsupported",
                CdProblem.Unterminated => "CommandLine_CdUnterminated",
                _ => "CommandLine_CdTooManyArguments",
            }));
            return;
        }

        var target = VfsPath.Local(targetPath);
        await panel.NavigateToAsync(target);
        if (_disposed) return;
        if (panel.CurrentPath == target && panel.Error is null)
            Text = "";
        else
            AppendMessage(Loc.Format("CommandLine_CdFailed", target.Path, panel.Error ?? ""));
    }

    // ---- Running a command ----------------------------------------------------------------------------------

    private async Task RunAsync(PanelViewModel panel, VfsPath folder, string text)
    {
        IRunningProcess process;
        try
        {
            process = _runner.Start(new ProcessSpec(Shell, ["-lc", text], folder.Path));
        }
        catch (Exception e)
        {
            AppendMessage(Loc.Format("CommandLine_StartFailed", e.Message));
            return;
        }

        _process = process;
        IsRunning = true;
        Text = "";
        AppendMessage($"{folder.Path}> {text}");
        string ending;
        try
        {
            var ct = _lifetime.Token;
            await Task.Run(async () =>
            {
                await foreach (var line in process.OutputLines.WithCancellation(ct).ConfigureAwait(false))
                    Enqueue(StripControlSequences(line));
            }, CancellationToken.None);
            ending = Loc.Format("Output_ExitCode", await process.ExitCode);
        }
        catch (Exception e)
        {
            // Cancelled when the window closed (nobody reads it then), or reading the output failed.
            ending = Loc.Format("CommandLine_Failed", e.Message);
        }
        finally
        {
            _process = null;
            await process.DisposeAsync();
        }

        if (!_disposed)
        {
            FlushPending();
            AppendMessage(ending);
        }
        IsRunning = false;
        // The command may have changed the folder: show it, unless the panel has moved on meanwhile.
        if (!_disposed && panel.CurrentPath == folder) await panel.RefreshAsync();
    }

    /// <summary>Any thread: keeps a line for the next flush, which is scheduled if none is.</summary>
    private void Enqueue(string line)
    {
        lock (_pendingLock)
        {
            _pending.Add(line);
            if (_flushScheduled) return;
            _flushScheduled = true;
        }
        _ = ScheduleFlushAsync();
    }

    private async Task ScheduleFlushAsync()
    {
        try
        {
            await _flushDelay(FlushInterval, _lifetime.Token).ConfigureAwait(false);
            _ui.Post(_ => FlushPending(), null);
        }
        catch (Exception)
        {
            // Disposed, or the UI is shutting down: nobody reads the output any more.
        }
    }

    /// <summary>UI thread: shows the lines kept so far.</summary>
    private void FlushPending()
    {
        List<string> lines;
        lock (_pendingLock)
        {
            lines = _pending;
            _pending = [];
            _flushScheduled = false;
        }
        if (_disposed || lines.Count == 0) return;
        AppendLines(lines);
    }

    // ---- Output ---------------------------------------------------------------------------------------------

    private void AppendMessage(string line) => AppendLines([line]);

    private void AppendLines(IEnumerable<string> lines)
    {
        foreach (var line in lines) _lines.Enqueue(line);
        while (_lines.Count > MaxOutputLines)
        {
            _lines.Dequeue();
            _droppedLines = true;
        }
        var body = string.Join('\n', _lines);
        Output = _droppedLines ? Loc.Get("Output_EarlierDropped") + "\n" + body : body;
        IsOutputVisible = true;
    }

    /// <summary>The line as plain text: ANSI escape sequences (colours, cursor moves, window titles) removed, and
    /// a trailing carriage return.</summary>
    internal static string StripControlSequences(string line) =>
        line.Contains('\e') ? EscapeSequence().Replace(line, "").TrimEnd('\r') : line.TrimEnd('\r');

    // CSI (ESC [ … final byte), OSC (ESC ] … BEL or ESC \), and the two-character escapes.
    [GeneratedRegex(@"\x1B\[[0-?]*[ -/]*[@-~]|\x1B\][^\x07\x1B]*(?:\x07|\x1B\\)?|\x1B[@-Z\\-_]")]
    private static partial Regex EscapeSequence();

    // ---- History --------------------------------------------------------------------------------------------

    private void Record(string text)
    {
        _historyIndex = -1;
        if (_history.Count > 0 && _history[^1] == text) return;
        _history.Add(text);
        if (_history.Count > MaxHistory) _history.RemoveAt(0);
    }

    private void SetTextFromHistory(string text)
    {
        _settingFromHistory = true;
        try
        {
            Text = text;
        }
        finally
        {
            _settingFromHistory = false;
        }
    }

    // ---- Following the active panel -------------------------------------------------------------------------

    private void OnWorkspacePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkspaceViewModel.ActivePanel)) FollowActivePanel();
    }

    private void OnPanelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PanelViewModel.CurrentPath)) UpdateAvailability();
    }

    private void FollowActivePanel()
    {
        var panel = _workspace.IsLoaded ? _workspace.ActivePanel : null;
        if (panel == _panel) return;
        if (_panel is not null) _panel.PropertyChanged -= OnPanelPropertyChanged;
        _panel = panel;
        if (panel is not null) panel.PropertyChanged += OnPanelPropertyChanged;
        UpdateAvailability();
    }

    private void UpdateAvailability()
    {
        var path = _panel?.CurrentPath ?? default;
        var shown = path != default;
        Prompt = shown ? path.Path : "";
        IsAvailable = shown && path.ProviderId == VfsPath.LocalProviderId;
    }
}
