using Tandem.App.ViewModels.Editing;
using Tandem.App.ViewModels.Viewing;
using Tandem.Core.Vfs;

namespace Tandem.App.Services;

/// <summary>
/// The app's windows besides the main one and its dialogs: the editor and viewer windows (later the search
/// window). They are not modal; the main window waits for them when the app quits. Called on the UI thread.
/// </summary>
public interface IWindowService
{
    /// <summary>Shows <paramref name="editor"/> in a window of its own.</summary>
    void ShowEditor(EditorViewModel editor);

    /// <summary>Shows <paramref name="viewer"/> in a window of its own; closing the window disposes it.</summary>
    void ShowViewer(ViewerViewModel viewer);

    /// <summary>Brings the editor window of <paramref name="path"/> to the front; false when none is open. The file is
    /// matched by what it really is, links followed (<see cref="EditorViewModel.ResolveTargetAsync"/>): "/tmp/x" and
    /// "/private/tmp/x", or a link and its target, are one editor.</summary>
    Task<bool> TryActivateEditorAsync(VfsPath path);

    /// <summary>True while any window of this service is open.</summary>
    bool HasOpenWindows { get; }

    /// <summary>Closes every window of this service; an editor with unsaved changes asks first. False when the user
    /// kept one open (or its save failed): the windows from there on stay open.</summary>
    Task<bool> CloseAllAsync();
}
