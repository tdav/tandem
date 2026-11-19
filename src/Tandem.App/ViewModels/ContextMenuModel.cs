using Tandem.App.Commands;
using Tandem.App.Resources;

namespace Tandem.App.ViewModels;

/// <summary>
/// One item of a panel's context menu, as the view renders it: its label, the shortcut the key map gives its command,
/// whether it can run now, and either something to run or a submenu. A submenu's items are made only when it is first
/// asked for (<see cref="GetChildrenAsync"/>), when the submenu opens.
/// </summary>
public sealed class ContextMenuItem
{
    private readonly Func<Task>? _execute;
    private readonly Func<Task<IReadOnlyList<ContextMenuItem>>>? _children;
    private Task<IReadOnlyList<ContextMenuItem>>? _loaded;

    public ContextMenuItem(string? commandId, string labelKey, string label, KeyChord? chord, bool isEnabled,
        Func<Task>? execute, Func<Task<IReadOnlyList<ContextMenuItem>>>? children = null, bool isLiteralLabel = false)
    {
        IsLiteralLabel = isLiteralLabel;
        CommandId = commandId;
        LabelKey = labelKey;
        Label = label;
        Chord = chord;
        IsEnabled = isEnabled;
        _execute = execute;
        _children = children;
    }

    /// <summary>The command the item runs; null for one that is not a command (Open, an application).</summary>
    public string? CommandId { get; }

    /// <summary>The resource key of the label; for an application, "Context_App".</summary>
    public string LabelKey { get; }

    public string Label { get; }

    /// <summary>The label is a name to show exactly as it is (an application's), not menu text that may mark an
    /// access key.</summary>
    public bool IsLiteralLabel { get; }

    /// <summary>The first chord the key map binds to the command; null when it has none.</summary>
    public KeyChord? Chord { get; }

    public bool IsEnabled { get; }

    public bool HasChildren => _children is not null;

    /// <summary>The submenu's items, asked for on the first call only (later calls share that answer); empty for an
    /// item without a submenu.</summary>
    public Task<IReadOnlyList<ContextMenuItem>> GetChildrenAsync() =>
        _loaded ??= _children?.Invoke() ?? Task.FromResult<IReadOnlyList<ContextMenuItem>>([]);

    /// <summary>Runs the item; never throws (failures are reported by what it runs).</summary>
    public Task ExecuteAsync() => IsEnabled && _execute is not null ? _execute() : Task.CompletedTask;
}

/// <summary>The context menus of a panel: for a row (acting on the cursor row of the active panel) and for the empty
/// space below the rows (acting on the folder).</summary>
public static class ContextMenuModel
{
    /// <summary>Open, Open With ▸, Reveal in Finder, Quick Look, Copy, Move, Rename, Delete, Properties.</summary>
    public static IReadOnlyList<ContextMenuItem> ForRow(MainViewModel main)
    {
        ArgumentNullException.ThrowIfNull(main);
        var side = main.ActiveSide;
        var panel = main.Workspace.ActivePanel;
        var row = panel.CursorRow;
        var index = panel.CursorIndex;
        var canOpen = row is not null && (row.IsDirectoryLike || ShellCommands.IsLocal(panel));
        return
        [
            new ContextMenuItem(null, "Context_Open", Loc.Get("Context_Open"), null, canOpen,
                () => side.OpenRowAsync(index)),
            new ContextMenuItem(CommandIds.ShellOpenWith, "Context_OpenWith", Loc.Get("Context_OpenWith"),
                ChordOf(main, CommandIds.ShellOpenWith), main.Commands.CanExecute(CommandIds.ShellOpenWith), null,
                async () => await OpenWithItemsAsync(main) ?? NoApps()),
            Command(main, CommandIds.ShellReveal, "Context_Reveal"),
            Command(main, CommandIds.FileQuickLook, "Context_QuickLook"),
            Command(main, CommandIds.FileCopy, "Context_Copy"),
            Command(main, CommandIds.FileMove, "Context_Move"),
            Command(main, CommandIds.FileRename, "Context_Rename"),
            Command(main, CommandIds.FileDelete, "Context_Delete"),
            Command(main, CommandIds.FileProperties, "Context_Properties"),
        ];
    }

    /// <summary>New folder, Open terminal here, Refresh.</summary>
    public static IReadOnlyList<ContextMenuItem> ForEmptySpace(MainViewModel main)
    {
        ArgumentNullException.ThrowIfNull(main);
        return
        [
            Command(main, CommandIds.FileMkdir, "Context_NewFolder"),
            Command(main, CommandIds.ShellOpenTerminal, "Context_OpenTerminal"),
            Command(main, CommandIds.PanelRefresh, "Context_Refresh"),
        ];
    }

    /// <summary>New tab, Duplicate, Pin (Unpin for a pinned tab), Close, Close others: for the active tab of the
    /// active side, which a right click on a tab has made the clicked one.</summary>
    public static IReadOnlyList<ContextMenuItem> ForTab(MainViewModel main)
    {
        ArgumentNullException.ThrowIfNull(main);
        var pinned = main.ActiveSide.Tabs.Active.IsPinned;
        return
        [
            Command(main, CommandIds.TabNew, "Context_TabNew"),
            Command(main, CommandIds.TabDuplicate, "Context_TabDuplicate"),
            Command(main, CommandIds.TabTogglePin, pinned ? "Context_TabUnpin" : "Context_TabPin"),
            Command(main, CommandIds.TabClose, "Context_TabClose"),
            Command(main, CommandIds.TabCloseOthers, "Context_TabCloseOthers"),
        ];
    }

    /// <summary>The applications that can open the cursor item, asked for now (off the UI thread); one disabled "No
    /// applications" item when there are none. A failing query is told or reported and shows as none. Null when the
    /// cursor moved to another item while the query ran (nothing to show). Each item opens the item the applications
    /// were asked for, wherever the cursor is when it is chosen.</summary>
    public static async Task<IReadOnlyList<ContextMenuItem>?> OpenWithItemsAsync(MainViewModel main)
    {
        ArgumentNullException.ThrowIfNull(main);
        if (await main.Shell.QueryOpenWithAsync() is not { } query) return null;
        if (query.Apps.Count == 0) return NoApps();
        return query.Apps.Select(app => new ContextMenuItem(null, "Context_App", app.Name, null, true, () =>
        {
            main.Shell.OpenWith(query.Entry, app);
            return Task.CompletedTask;
        }, isLiteralLabel: true)).ToArray();
    }

    private static IReadOnlyList<ContextMenuItem> NoApps() =>
        [new ContextMenuItem(null, "Context_NoApps", Loc.Get("Context_NoApps"), null, false, null)];

    private static ContextMenuItem Command(MainViewModel main, string id, string labelKey) =>
        new(id, labelKey, Loc.Get(labelKey), ChordOf(main, id), main.Commands.CanExecute(id),
            () => main.ExecuteCommandAsync(id));

    private static KeyChord? ChordOf(MainViewModel main, string id) =>
        main.KeyMap.ChordsFor(id) is [var chord, ..] ? chord : null;
}
