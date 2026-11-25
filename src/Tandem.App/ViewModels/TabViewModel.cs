using Tandem.Core.Vfs.Local;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Tandem.App.ViewModels.Net;
using Tandem.Core.Vfs;

namespace Tandem.App.ViewModels;

/// <summary>
/// One tab of a side: a panel and the history of the folders it has shown. Every navigation of the panel is
/// recorded, except the ones made by <see cref="GoBackAsync"/> and <see cref="GoForwardAsync"/>, which only move the
/// history's position, and only once the folder is shown.
/// </summary>
/// <remarks>Created and called on the UI thread. Owns its panel: <see cref="Dispose"/> disposes it.</remarks>
public sealed partial class TabViewModel : ObservableObject, IDisposable
{
    // The back/forward navigation under way; Id tells overlapping requests for the same folder apart.
    private (int Step, VfsPath Target, int Id)? _pendingStep;
    private int _lastStepId;

    internal TabViewModel(PanelViewModel panel)
    {
        Panel = panel;
        Title = TitleOf(panel.DisplayPath);
        panel.Navigated += OnNavigated;
        panel.PropertyChanged += OnPanelPropertyChanged;
    }

    internal TabViewModel(PanelViewModel panel, TerminalViewModel terminal)
    {
        Panel = panel;
        Terminal = terminal;
        Title = terminal.Title;
    }

    public PanelViewModel Panel { get; }

    public TerminalViewModel? Terminal { get; }

    public bool IsTerminal => Terminal is not null;

    public NavigationHistory History { get; } = new();

    /// <summary>A pinned tab cannot be closed.</summary>
    [ObservableProperty]
    public partial bool IsPinned { get; set; }

    /// <summary>The last segment of the panel's <see cref="PanelViewModel.DisplayPath"/> (the name of the folder
    /// shown, or of the file a hosted provider lives in at its root), "/" for the root, "" before the first folder is
    /// shown; a display path that does not start with '/' (a search result set's title) is the title as a whole.</summary>
    [ObservableProperty]
    public partial string Title { get; private set; }

    /// <summary>Raised when something that is saved changes: the folder, the sort or the pin.</summary>
    internal event EventHandler? StateChanged;

    /// <summary>Shows the previous folder of the history. If it cannot be shown, the panel stays where it is (with
    /// the reason in <see cref="PanelViewModel.Error"/>) and a folder that no longer exists is dropped from the
    /// history.</summary>
    internal Task GoBackAsync() => StepAsync(-1);

    /// <summary>Shows the next folder of the history; on failure behaves like <see cref="GoBackAsync"/>.</summary>
    internal Task GoForwardAsync() => StepAsync(+1);

    public void Dispose()
    {
        Panel.Navigated -= OnNavigated;
        Panel.PropertyChanged -= OnPanelPropertyChanged;
        Panel.Dispose();
        if (Terminal is { } terminal) _ = terminal.DisposeAsync();
    }

    private async Task StepAsync(int step)
    {
        if (History.Peek(step) is not { } target) return;
        var pending = (step, target, ++_lastStepId);
        _pendingStep = pending;
        await Panel.NavigateToAsync(target);
        if (_pendingStep != pending) return; // shown (the history moved) or superseded by another navigation
        _pendingStep = null;
        if (Panel is { IsLoading: false, ErrorKind: PanelErrorKind.NotFound } && History.Peek(step) == target)
            History.RemoveAt(step);
    }

    private void OnNavigated(object? sender, VfsPath path)
    {
        var pending = _pendingStep;
        _pendingStep = null;
        if (pending is { } p && p.Target == path && History.Peek(p.Step) == path)
        {
            if (p.Step < 0) History.Back();
            else History.Forward();
        }
        else
        {
            History.Visit(path);
        }
    }

    private void OnPanelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PanelViewModel.DisplayPath):
                Title = TitleOf(Panel.DisplayPath);
                break;
            case nameof(PanelViewModel.CurrentPath):
                StateChanged?.Invoke(this, EventArgs.Empty);
                break;
            case nameof(PanelViewModel.Sort):
                StateChanged?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    partial void OnIsPinnedChanged(bool value) => StateChanged?.Invoke(this, EventArgs.Empty);

    private static string TitleOf(string displayPath)
    {
        if (LocalPath.Current == LocalPathStyle.Windows &&
            LocalPath.ExpandTyped(displayPath, "", LocalPathStyle.Windows) is not null && !displayPath.StartsWith('/'))
        {
            // C:\Users\me, C:\, \\srv\share, C:\a.zip/inner: the last name, or the drive itself.
            var parts = displayPath.Split('\\', '/', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length == 0 ? displayPath : parts[^1];
        }
        if (!displayPath.StartsWith('/')) return displayPath; // a free text, e.g. a search result set's title
        var trimmed = displayPath.TrimEnd('/');
        if (trimmed.Length == 0) return displayPath.Length == 0 ? "" : "/";
        return trimmed[(trimmed.LastIndexOf('/') + 1)..];
    }
}
