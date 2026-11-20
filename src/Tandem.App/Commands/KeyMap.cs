using Tandem.Core.Settings;

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
            (CommandIds.NavEnter, "Cmd+Down"),
            (CommandIds.NavBack, "Cmd+OemOpenBrackets"),
            (CommandIds.NavForward, "Cmd+OemCloseBrackets"),
            (CommandIds.PathEdit, "Cmd+Shift+G"),
            (CommandIds.CmdlineFocus, "Cmd+L"),
            (CommandIds.SelectToggleDown, "Space"), (CommandIds.SelectToggleDown, "Insert"),
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
            (CommandIds.ViewQuickView, "Ctrl+Q"),
            // Finder's "Sort By" chords. Not Ctrl+F3…F8: macOS keeps Ctrl with an F-key for keyboard focus moves.
            (CommandIds.SortByName, "Ctrl+Cmd+1"),
            (CommandIds.SortByExtension, "Ctrl+Cmd+2"),
            (CommandIds.SortBySize, "Ctrl+Cmd+3"),
            (CommandIds.SortByDate, "Ctrl+Cmd+4"),
            (CommandIds.SortByMode, "Ctrl+Cmd+5"),
            (CommandIds.SortReverse, "Ctrl+Cmd+R"),
            (CommandIds.TabTogglePin, "Ctrl+Cmd+P"),
            (CommandIds.AppSettings, "Cmd+OemComma"),
            (CommandIds.SearchFiles, "Alt+F7"), (CommandIds.SearchFiles, "Cmd+F"),
            (CommandIds.ArchivePack, "Alt+F5"), (CommandIds.ArchivePack, "Cmd+Alt+5"),
            (CommandIds.ArchiveUnpack, "Alt+F9"), (CommandIds.ArchiveUnpack, "Cmd+Alt+9"),
        }.Select(b => (b.Item1, KeyChord.Parse(b.Item2))));

    /// <summary>All bindings in order; for a key map editor and for showing shortcuts in menus.</summary>
    public IReadOnlyList<(string CommandId, KeyChord Chord)> Bindings { get; }

    /// <summary>The id of the command bound to <paramref name="chord"/>, or null.</summary>
    public string? Find(KeyChord chord) => _commandByChord.GetValueOrDefault(chord);

    /// <summary>
    /// This map with the user's overrides applied in order. Each override of a command in <see cref="CommandIds.All"/>
    /// replaces that command's chords with its own (in their order, where the command's chords stood), keeping those
    /// that parse and <see cref="KeyChordRules.IsAssignable"/> allows; a chord it takes is taken away from every other
    /// command, so a later override wins a chord two of them name. An empty list leaves the command without chords.
    /// Overrides of unknown commands, without a chord list, or whose chords are all unusable change nothing. Never
    /// throws for the overrides' content.
    /// </summary>
    public KeyMap WithOverrides(IReadOnlyList<KeyBindingOverride> overrides)
    {
        ArgumentNullException.ThrowIfNull(overrides);

        var order = new List<string>();
        var chordsOf = new Dictionary<string, List<KeyChord>>();
        foreach (var (commandId, chord) in Bindings)
        {
            if (!chordsOf.TryGetValue(commandId, out var list))
            {
                chordsOf[commandId] = list = [];
                order.Add(commandId);
            }
            list.Add(chord);
        }

        foreach (var binding in overrides)
        {
            if (binding?.Chords is null || !CommandIds.All.Contains(binding.CommandId)) continue;
            var given = binding.Chords.Select(TryParse).OfType<KeyChord>().Where(KeyChordRules.IsAssignable)
                .Distinct().ToList();
            if (given.Count == 0 && binding.Chords.Count > 0) continue;

            foreach (var list in chordsOf.Values) list.RemoveAll(given.Contains);
            if (!chordsOf.ContainsKey(binding.CommandId)) order.Add(binding.CommandId);
            chordsOf[binding.CommandId] = given;
        }

        return new KeyMap(order.SelectMany(id => chordsOf[id].Select(chord => (id, chord))));
    }

    /// <summary>The overrides that turn <paramref name="defaults"/> into this map: one for each command whose chords
    /// (or their order) differ, holding all of its chords as text; none when the maps bind alike.</summary>
    public IReadOnlyList<KeyBindingOverride> OverridesAgainst(KeyMap defaults)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        return defaults.Bindings.Concat(Bindings)
            .Select(b => b.CommandId)
            .Distinct()
            .Where(id => !ChordsFor(id).SequenceEqual(defaults.ChordsFor(id)))
            .Select(id => new KeyBindingOverride(id, ChordsFor(id).Select(c => c.ToString()).ToArray()))
            .ToArray();
    }

    private static KeyChord? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            return KeyChord.Parse(text);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>The chords bound to a command, primary first; empty if it has none.</summary>
    public IReadOnlyList<KeyChord> ChordsFor(string commandId) =>
        Bindings.Where(b => b.CommandId == commandId).Select(b => b.Chord).ToArray();
}
