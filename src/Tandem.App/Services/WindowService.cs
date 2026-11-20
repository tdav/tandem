using Avalonia;
using Avalonia.Controls;
using Tandem.App.ViewModels.Editing;
using Tandem.App.Views.Editing;
using Tandem.Core.Vfs;

namespace Tandem.App.Services;

/// <summary>
/// Shows the editor windows as independent windows (not owned, so each comes forward on its own), placed over
/// <see cref="Owner"/> (the main window) when it is shown. Their "save changes?" question goes through
/// <paramref name="dialogs"/>. UI thread only.
/// </summary>
public sealed class WindowService(IDialogService dialogs) : IWindowService
{
    /// <summary>How far each window opens from the main window's corner.</summary>
    private const int Offset = 40;

    private readonly IDialogService _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
    private readonly List<Window> _open = [];

    /// <summary>The main window; set by the app once it exists.</summary>
    public Window? Owner { get; set; }

    /// <summary>The windows open now, oldest first.</summary>
    public IReadOnlyList<Window> Open => _open;

    public bool HasOpenWindows => _open.Count > 0;

    public void ShowEditor(EditorViewModel editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        var window = new EditorWindow(editor, _dialogs);
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

    public bool TryActivateEditor(VfsPath path)
    {
        if (_open.OfType<EditorWindow>().FirstOrDefault(w => w.Editor.File == path) is not { } window) return false;
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
