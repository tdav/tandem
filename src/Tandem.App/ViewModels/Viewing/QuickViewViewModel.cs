using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Tandem.App.Resources;
using Tandem.App.Services;
using Tandem.Core.Vfs;

namespace Tandem.App.ViewModels.Viewing;

/// <summary>
/// The quick-view panel (Ctrl+Q): the side that was passive when it opened shows, in place of its file list, the file
/// under the active panel's cursor in a <see cref="ViewerViewModel"/>, and follows the cursor once it has rested for
/// <see cref="FollowDelay"/>.
/// </summary>
/// <remarks>
/// <para>Created and called on the UI thread; files are opened off it by <see cref="ViewerViewModel.OpenAsync"/>.</para>
/// <para>At most one open runs at a time: a cursor move cancels the one waiting or opening, and an open that finishes
/// anyway for a position the cursor has left is disposed unseen. The viewer shown stays until the next one is ready,
/// and every viewer replaced or dropped is disposed (its stream closed, its temporary copy released).</para>
/// <para>The replaced side is not touched: its tabs, folder, cursor and selection are as they were when the panel
/// closes. It closes on a second <see cref="Toggle"/>, on <see cref="Close"/> (the window closing), and when the
/// active panel changes (another side or another tab).</para>
/// </remarks>
public sealed partial class QuickViewViewModel : ObservableObject, IDisposable
{
    /// <summary>How long the cursor rests before the panel shows the file under it.</summary>
    public static readonly TimeSpan FollowDelay = TimeSpan.FromMilliseconds(150);

    private readonly WorkspaceViewModel _workspace;
    private readonly ProviderRegistry _providers;
    private readonly TempFileStore _temp;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Action<Exception>? _reportFailure;
    private readonly List<Task> _work = [];
    private PanelViewModel? _panel;
    private CancellationTokenSource? _show;
    private VfsPath? _shown;
    private bool _disposed;

    /// <param name="delay">Waits <see cref="FollowDelay"/> before following the cursor; <see cref="Task.Delay(TimeSpan,
    /// CancellationToken)"/> when null. Tests pass one they complete themselves.</param>
    /// <param name="reportFailure">Told about an unexpected failure (a bug, not an unreadable file), which the panel
    /// also shows as a message.</param>
    public QuickViewViewModel(WorkspaceViewModel workspace, ProviderRegistry providers, TempFileStore temp,
        Func<TimeSpan, CancellationToken, Task>? delay = null, Action<Exception>? reportFailure = null)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
        _temp = temp ?? throw new ArgumentNullException(nameof(temp));
        _delay = delay ?? Task.Delay;
        _reportFailure = reportFailure;
        workspace.PropertyChanged += OnWorkspacePropertyChanged;
    }

    [ObservableProperty]
    public partial bool IsOpen { get; private set; }

    /// <summary>The side whose file list is replaced: the passive one when the panel opened; null while closed.</summary>
    [ObservableProperty]
    public partial PanelSide? Side { get; private set; }

    /// <summary>The file under the cursor; null for a folder, "..", an empty panel, a file that cannot be read, and
    /// until the first file has opened.</summary>
    [ObservableProperty]
    public partial ViewerViewModel? Viewer { get; private set; }

    /// <summary>Shown instead of a viewer: the folder's name and "Folder", or why the file could not be opened.</summary>
    [ObservableProperty]
    public partial string? Message { get; private set; }

    /// <summary>Opens the panel on the passive side for the file under the active panel's cursor, shown at once; or
    /// closes it when open.</summary>
    public void Toggle()
    {
        if (IsOpen)
        {
            Close();
            return;
        }
        if (_disposed || !_workspace.IsLoaded) return;
        _panel = _workspace.ActivePanel;
        _panel.PropertyChanged += OnPanelPropertyChanged;
        Side = _workspace.ActiveSide == PanelSide.Left ? PanelSide.Right : PanelSide.Left;
        IsOpen = true;
        Show(wait: false);
    }

    /// <summary>Closes the panel: the open in progress is cancelled and the viewer disposed. Nothing when closed.</summary>
    public void Close()
    {
        if (!IsOpen) return;
        CancelShow();
        if (_panel is not null) _panel.PropertyChanged -= OnPanelPropertyChanged;
        _panel = null;
        Display(null, null, null);
        IsOpen = false;
        Side = null;
    }

    /// <summary>Completes when no open is in progress and every viewer let go of has been disposed.</summary>
    public async Task IdleAsync()
    {
        while (_work.Count > 0)
        {
            var work = _work.ToArray();
            _work.Clear();
            await Task.WhenAll(work).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing |
                                                    ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        Close();
        _disposed = true;
        _workspace.PropertyChanged -= OnWorkspacePropertyChanged;
    }

    private void OnWorkspacePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!IsOpen || e.PropertyName is not (nameof(WorkspaceViewModel.ActiveSide)
                or nameof(WorkspaceViewModel.ActivePanel))) return;
        if (_workspace.ActiveSide == Side || !ReferenceEquals(_workspace.ActivePanel, _panel)) Close();
    }

    private void OnPanelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Raised for every cursor move and for every new listing (another folder, a refresh), once each.
        if (e.PropertyName == nameof(PanelViewModel.CursorRow)) Show(wait: true);
    }

    private void Show(bool wait)
    {
        CancelShow();
        var show = new CancellationTokenSource();
        _show = show;
        Track(ShowAsync(show, wait));
    }

    private async Task ShowAsync(CancellationTokenSource show, bool wait)
    {
        try
        {
            if (wait)
            {
                try
                {
                    await _delay(FollowDelay, show.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
            if (_show != show) return;

            var entry = _panel is { CursorRow: { IsParentLink: false, Entry: { } e } } panel &&
                        panel.CurrentPath != default
                ? e
                : null;
            if (entry is not null && entry.Path == _shown) return; // shown already (the cursor came back)
            if (entry is null)
            {
                Display(null, null, null);
                return;
            }
            if (entry.IsDirectoryLike)
            {
                Display(entry.Path, null, Loc.Format("QuickView_Folder", entry.Name));
                return;
            }

            ViewerViewModel viewer;
            try
            {
                viewer = await ViewerViewModel.OpenAsync(_providers, _temp, entry.Path, show.Token);
            }
            catch (OperationCanceledException) when (show.IsCancellationRequested)
            {
                return;
            }
            catch (Exception failure)
            {
                if (_show != show) return;
                if (failure is not (IOException or UnauthorizedAccessException)) _reportFailure?.Invoke(failure);
                Display(entry.Path, null,
                    Loc.Format("QuickView_OpenFailed", entry.Name, OperationTexts.ForFailure(failure)));
                return;
            }
            // The cursor moved on (or the panel closed) while the file opened: it is not shown.
            if (_show != show)
            {
                Track(viewer.DisposeAsync().AsTask());
                return;
            }
            Display(entry.Path, viewer, null);
        }
        catch (Exception failure)
        {
            _reportFailure?.Invoke(failure); // the delay itself failed
        }
        finally
        {
            if (_show == show) _show = null;
            show.Dispose();
        }
    }

    /// <summary>Shows <paramref name="viewer"/> or <paramref name="message"/> for <paramref name="path"/>, disposing
    /// the viewer shown before.</summary>
    private void Display(VfsPath? path, ViewerViewModel? viewer, string? message)
    {
        var previous = Viewer;
        _shown = path;
        Viewer = viewer;
        Message = message;
        if (previous is not null && !ReferenceEquals(previous, viewer)) Track(previous.DisposeAsync().AsTask());
    }

    /// <summary>Cancels the open waiting or in progress; its task disposes its token source when it ends.</summary>
    private void CancelShow()
    {
        if (_show is not { } show) return;
        _show = null;
        show.Cancel();
    }

    private void Track(Task task)
    {
        _work.RemoveAll(t => t.IsCompleted);
        if (!task.IsCompleted) _work.Add(task);
    }
}
