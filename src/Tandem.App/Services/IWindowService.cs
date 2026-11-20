using Tandem.App.ViewModels.Editing;
using Tandem.App.ViewModels.Settings;
using Tandem.App.ViewModels.Viewing;
using Tandem.Core.Vfs;

namespace Tandem.App.Services;

/// <summary>
/// The app's windows besides the main one and its dialogs: the editor and viewer windows (later the search
/// window), which are not modal, and the settings window. The main window closes them when the app quits. Called on the UI thread.
/// </summary>
public interface IWindowService
{
    /// <summary>Shows <paramref name="editor"/> in a window of its own.</summary>
    void ShowEditor(EditorViewModel editor);

    /// <summary>Shows <paramref name="viewer"/> in a window of its own; closing the window disposes it.</summary>
    void ShowViewer(ViewerViewModel viewer);

    /// <summary>Shows the settings window for <paramref name="settings"/>, modal to the main window, and completes
    /// when it closes: true when its settings were applied (OK or Apply). One at a time: while it is open, it is
    /// brought to the front instead and the answer is false.</summary>
    Task<bool> ShowSettingsAsync(SettingsViewModel settings);

    /// <summary>Brings the editor window of <paramref name="path"/> to the front; false when none is open. The path
    /// is first resolved to the file it really is, links followed (<see cref="EditorViewModel.ResolveTargetAsync"/>),
    /// and editors are matched by that file only: "/tmp/x" and "/private/tmp/x", or a link and its target, are one
    /// editor, and a link since pointed elsewhere no longer finds the old target's editor.</summary>
    Task<bool> TryActivateEditorAsync(VfsPath path);

    /// <summary>Brings the editor window of the already resolved file <paramref name="target"/>
    /// (<see cref="EditorViewModel.Target"/>) to the front; false when none is open. Synchronous, so a caller can
    /// check and then show a new editor with nothing in between.</summary>
    bool TryActivateEditorFor(VfsPath target);

    /// <summary>True while any window of this service is open.</summary>
    bool HasOpenWindows { get; }

    /// <summary>Closes every window of this service; an editor with unsaved changes asks first. False when the user
    /// kept one open (or its save failed): the windows from there on stay open.</summary>
    Task<bool> CloseAllAsync();
}
