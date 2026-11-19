namespace Tandem.App.Commands;

/// <summary>
/// Which key chord runs which command: an immutable list of (command id, chord) pairs. A command may have several
/// chords (listed primary first); a chord belongs to at most one command.
/// </summary>
public sealed class KeyMap
{
    private readonly Dictionary<KeyChord, string> _commandByChord = [];

    public KeyMap(IEnumerable<(string CommandId, KeyChord Chord)> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);

        Bindings = bindings.ToArray();
        foreach (var (commandId, chord) in Bindings)
        {
            if (!_commandByChord.TryAdd(chord, commandId))
                throw new ArgumentException(
                    $"Chord {chord} is bound to both {_commandByChord[chord]} and {commandId}.", nameof(bindings));
        }
    }

    /// <summary>The default bindings (stage 1 plan, section 7.2 of the spec).</summary>
    public static KeyMap Default { get; } = new(
        new (string, string)[]
        {
            (CommandIds.FileView, "F3"), (CommandIds.FileView, "Cmd+3"),
            (CommandIds.FileEdit, "F4"), (CommandIds.FileEdit, "Cmd+4"),
            (CommandIds.FileCopy, "F5"), (CommandIds.FileCopy, "Cmd+5"),
            (CommandIds.FileMove, "F6"), (CommandIds.FileMove, "Cmd+6"),
            (CommandIds.FileMkdir, "F7"), (CommandIds.FileMkdir, "Cmd+7"),
            (CommandIds.FileDelete, "F8"), (CommandIds.FileDelete, "Cmd+Back"),
            (CommandIds.FileDeletePermanent, "Shift+F8"), (CommandIds.FileDeletePermanent, "Cmd+Alt+Back"),
            (CommandIds.FileRename, "Shift+F6"), (CommandIds.FileRename, "F2"), (CommandIds.FileRename, "Cmd+Shift+R"),
            (CommandIds.FileQuickLook, "Cmd+Y"),
            (CommandIds.FileProperties, "Cmd+I"),
            (CommandIds.PanelSwitch, "Tab"),
            (CommandIds.PanelRefresh, "Cmd+R"),
            (CommandIds.PanelSwap, "Cmd+U"),
            (CommandIds.PanelMirror, "Cmd+Shift+U"),
            (CommandIds.NavUp, "Back"), (CommandIds.NavUp, "Cmd+Up"),
            (CommandIds.NavBack, "Cmd+OemOpenBrackets"),
            (CommandIds.NavForward, "Cmd+OemCloseBrackets"),
            (CommandIds.PathEdit, "Cmd+Shift+G"),
            (CommandIds.CmdlineFocus, "Cmd+L"),
            (CommandIds.SelectToggleDown, "Space"),
            (CommandIds.SelectAll, "Cmd+A"),
            (CommandIds.SelectByMask, "Cmd+OemPlus"),
            (CommandIds.UnselectByMask, "Cmd+OemMinus"),
            (CommandIds.SelectInvert, "Cmd+Shift+A"),
            (CommandIds.TabNew, "Cmd+T"),
            (CommandIds.TabClose, "Cmd+W"),
            (CommandIds.TabNext, "Ctrl+Tab"),
            (CommandIds.TabPrev, "Ctrl+Shift+Tab"),
            (CommandIds.FavoritesShow, "Cmd+D"),
            (CommandIds.ViewToggleHidden, "Cmd+Shift+OemPeriod"),
            // Finder's "Sort By" chords. Not Ctrl+F3…F8: macOS keeps Ctrl with an F-key for keyboard focus moves.
            (CommandIds.SortByName, "Ctrl+Cmd+1"),
            (CommandIds.SortByExtension, "Ctrl+Cmd+2"),
            (CommandIds.SortBySize, "Ctrl+Cmd+3"),
            (CommandIds.SortByDate, "Ctrl+Cmd+4"),
            (CommandIds.SortByMode, "Ctrl+Cmd+5"),
            (CommandIds.SortReverse, "Ctrl+Cmd+R"),
            (CommandIds.TabTogglePin, "Ctrl+Cmd+P"),
        }.Select(b => (b.Item1, KeyChord.Parse(b.Item2))));

    /// <summary>All bindings in order; for a key map editor and for showing shortcuts in menus.</summary>
    public IReadOnlyList<(string CommandId, KeyChord Chord)> Bindings { get; }

    /// <summary>The id of the command bound to <paramref name="chord"/>, or null.</summary>
    public string? Find(KeyChord chord) => _commandByChord.GetValueOrDefault(chord);

    /// <summary>The chords bound to a command, primary first; empty if it has none.</summary>
    public IReadOnlyList<KeyChord> ChordsFor(string commandId) =>
        Bindings.Where(b => b.CommandId == commandId).Select(b => b.Chord).ToArray();
}
