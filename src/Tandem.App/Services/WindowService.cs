using Avalonia;
using Avalonia.Controls;
using Tandem.App.ViewModels.Editing;
using Tandem.App.ViewModels.Search;
using Tandem.App.ViewModels.Viewing;
using Tandem.App.Views.Editing;
using Tandem.App.Views.Search;
using Tandem.App.Views.Viewing;
using Tandem.Core.Vfs;

namespace Tandem.App.Services;

/// <summary>
/// Shows the editor, viewer and search windows as independent windows (not owned, so each comes forward on its own),
/// placed over <see cref="Owner"/> (the main window) when it is shown. Their "save changes?" question goes through
/// <paramref name="dialogs"/>; the files of <paramref name="providers"/> are told apart by what they really are, with
/// links followed. UI thread only.
/// </summary>
public sealed class WindowService(IDialogService dialogs, ProviderRegistry providers) : IWindowService
{
    /// <summary>How far each window opens from the main window's corner.</summary>
    private const int Offset = 40;

    private readonly IDialogService _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
    private readonly ProviderRegistry _providers = providers ?? throw new ArgumentNullException(nameof(providers));
    private readonly List<Window> _open = [];

    /// <summary>The main window; set by the app once it exists.</summary>
    public Window? Owner { get; set; }

    /// <summary>The windows open now, oldest first.</summary>
    public IReadOnlyList<Window> Open => _open;

    public bool HasOpenWindows => _open.Count > 0;

    public void ShowEditor(EditorViewModel editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        Show(new EditorWindow(editor, _dialogs));
    }

    public void ShowViewer(ViewerViewModel viewer)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        Show(new ViewerWindow(viewer));
    }

    public void ShowSearch(SearchViewModel search)
    {
        ArgumentNullException.ThrowIfNull(search);
        if (_open.OfType<SearchWindow>().FirstOrDefault() is not { } window)
        {
            Show(new SearchWindow(search));
            return;
        }
        window.Attach(search);
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    private void Show(Window window)
    {
        if (Owner is { IsVisible: true } owner)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            var step = Offset * (_open.Count + 1);
            window.Position = owner.Position + new PixelPoint(step, step);
        }
        else
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        _open.Add(window);
        window.Closed += (_, _) => _open.Remove(window);
        window.Show();
    }

    public async Task<bool> TryActivateEditorAsync(VfsPath path)
    {
        if (!_open.OfType<EditorWindow>().Any()) return false;
        VfsPath target;
        try
        {
            target = await EditorViewModel.ResolveTargetAsync(_providers, path, CancellationToken.None);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false; // opening it will tell why
        }
        return TryActivateEditorFor(target);
    }

    public bool TryActivateEditorFor(VfsPath target)
    {
        if (_open.OfType<EditorWindow>().FirstOrDefault(w => w.Editor.Target == target) is not { } window)
            return false;
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
        return true;
    }

    public async Task<bool> CloseAllAsync()
    {
        foreach (var window in _open.ToArray())
        {
            if (window is EditorWindow editor)
            {
                if (!await editor.CloseAsync()) return false;
            }
            else
            {
                window.Close();
            }
        }
        return true;
    }
}
