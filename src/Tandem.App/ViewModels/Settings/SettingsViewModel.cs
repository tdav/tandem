using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Tandem.App.Commands;
using Tandem.App.Resources;
using Tandem.App.Services;
using Tandem.App.Views;
using Tandem.Core.Platform;
using Tandem.Core.Settings;
using Tandem.Core.Themes;

namespace Tandem.App.ViewModels.Settings;

/// <summary>What <see cref="SettingsViewModel.Assign"/> did with a chord.</summary>
public enum ChordAssignment
{
    /// <summary>The command has the chord now.</summary>
    Assigned,

    /// <summary>The chord cannot be a shortcut (<see cref="KeyChordRules.IsAssignable"/>); nothing changed.</summary>
    NotAssignable,

    /// <summary>Another command has the chord; nothing changed. Assigning it again with <c>takeFromOther</c> moves
    /// it.</summary>
    TakenByOther,
}

/// <summary>A theme in the settings window's list, with its label.</summary>
public sealed record ThemeOption(ThemeChoice Value, string Label);

/// <summary>A theme of <see cref="ThemeCatalog.List"/> in the settings window's list. One whose file cannot be read
/// (<see cref="Error"/>) is shown marked but cannot be chosen.</summary>
public sealed record ColorSchemeOption(string Id, string Label, string? Error, bool IsBuiltIn)
{
    public bool IsSelectable => Error is null;

    internal static ColorSchemeOption From(ThemeInfo theme) => new(theme.Id,
        theme.Error is null ? theme.Name : Loc.Format("Settings_ColorSchemeBroken", theme.Name), theme.Error,
        theme.Path is null);
}

/// <summary>A colour of <see cref="AppSettings.ColorNames"/> in the settings window's list, with its label.</summary>
public sealed record ColorOption(string Value, string Label);

/// <summary>A chord of the selected command on the Keys tab, with its text as the Mac shows it.</summary>
public sealed record ChordItem(KeyChord Chord, string Text);

/// <summary>One command on the Keys tab: its menu label and the chords it has in the window (primary first).
/// <see cref="IsChanged"/> while they differ from its default chords.</summary>
public sealed class ShortcutRow : ObservableObject
{
    private readonly IReadOnlyList<KeyChord> _defaults;
    private IReadOnlyList<KeyChord> _chords;

    internal ShortcutRow(string commandId, IReadOnlyList<KeyChord> chords, IReadOnlyList<KeyChord> defaults)
    {
        CommandId = commandId;
        Label = Loc.Get(MainMenu.LabelKey(commandId));
        _chords = chords;
        _defaults = defaults;
    }

    public string CommandId { get; }

    public string Label { get; }

    public IReadOnlyList<KeyChord> Chords
    {
        get => _chords;
        internal set
        {
            var changed = IsChanged;
            if (!SetProperty(ref _chords, value)) return;
            OnPropertyChanged(nameof(ChordsText));
            OnPropertyChanged(nameof(ChordItems));
            if (changed != IsChanged) OnPropertyChanged(nameof(IsChanged));
        }
    }

    /// <summary>The chords as the Mac shows them (<see cref="KeyChordText"/>), comma-separated.</summary>
    public string ChordsText => string.Join(", ", Chords.Select(KeyChordText.Format));

    /// <summary>The chords with their shown text, for the list of the selected command.</summary>
    public IReadOnlyList<ChordItem> ChordItems => Chords.Select(c => new ChordItem(c, KeyChordText.Format(c))).ToArray();

    /// <summary>True when the label, the chords as shown or the chords as saved ("Shift+F8") contain
    /// <paramref name="filter"/>, case ignored.</summary>
    internal bool Matches(string filter) =>
        Label.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ||
        ChordsText.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
        string.Join(", ", Chords).Contains(filter, StringComparison.OrdinalIgnoreCase);

    public bool IsChanged => !Chords.SequenceEqual(_defaults);

    internal IReadOnlyList<KeyChord> Defaults => _defaults;
}

/// <summary>A colour rule on the View tab: files matching <see cref="Mask"/> are shown in <see cref="Color"/>.</summary>
public sealed partial class FileColorRuleRow : ObservableObject
{
    [ObservableProperty]
    private string _mask = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedColor))]
    private string _color = AppSettings.ColorNames[0];

    /// <summary>The colours a rule may name, with their labels.</summary>
    public static IReadOnlyList<ColorOption> ColorOptions { get; } =
        AppSettings.ColorNames.Select(n => new ColorOption(n, Loc.Get("Settings_Color_" + n))).ToArray();

    /// <summary><see cref="Color"/> as one of <see cref="ColorOptions"/>, for a list box.</summary>
    public ColorOption? SelectedColor
    {
        get => ColorOptions.FirstOrDefault(o => o.Value == Color);
        set
        {
            if (value is not null) Color = value.Value;
        }
    }

    internal FileColorRule ToRule() => new(Mask.Trim(), Color);
}

/// <summary>A column of a panel on the Columns tab.</summary>
public sealed partial class ColumnRow(ColumnId column, bool visible, double width) : ObservableObject
{
    public ColumnId Column { get; } = column;

    public string Label { get; } = Loc.Get(column switch
    {
        ColumnId.Extension => "Column_Ext",
        ColumnId.Size => "Column_Size",
        ColumnId.Modified => "Column_Date",
        _ => "Column_Mode",
    });

    [ObservableProperty]
    private bool _visible = visible;

    [ObservableProperty]
    private double _width = width;

    public ColumnSetting ToSetting() => new(Column, Visible, Width);
}

/// <summary>A button of the button bar on its tab.</summary>
public sealed partial class UserButtonRow : ObservableObject
{
    [ObservableProperty]
    private string _title = "";

    [ObservableProperty]
    private string _program = "";

    [ObservableProperty]
    private string _arguments = "";

    internal UserButton ToButton() => new(Title.Trim(), Program.Trim(), Arguments);
}

/// <summary>
/// The settings window's model: a working copy of the settings that reaches <see cref="SettingsService"/> only through
/// <see cref="Apply"/>; dropping the model changes nothing. The Keys tab starts from the key map in effect and saves
/// only what differs from <see cref="KeyMap.Default"/>. UI thread only.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly ShortcutRow[] _shortcuts;
    private readonly IShellIntegration? shell;
    private string colorSchemeId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedTheme))]
    private ThemeChoice _theme;

    [ObservableProperty]
    private double _fontSize;

    [ObservableProperty]
    private double _rowHeight;

    [ObservableProperty]
    private string _externalEditor;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SshInSeparateWindow))]
    private bool _sshInPanelTab;

    public bool SshInSeparateWindow
    {
        get => !SshInPanelTab;
        set { if (value) SshInPanelTab = false; }
    }

    [ObservableProperty]
    private int _sshPanelIndex;

    /// <summary>The terminals this platform has, in the order of the combo box; the first is the default.</summary>
    public static IReadOnlyList<TerminalChoice> TerminalChoices { get; } = OperatingSystem.IsMacOS()
        ? [TerminalChoice.Default, TerminalChoice.ITerm]
        : [TerminalChoice.Default, TerminalChoice.WindowsTerminal, TerminalChoice.Cmd];

    public static IReadOnlyList<string> TerminalOptions { get; } = TerminalChoices.Select(c => c switch
    {
        TerminalChoice.WindowsTerminal => Loc.Get("Settings_TerminalWindowsTerminal"),
        TerminalChoice.Cmd => Loc.Get("Settings_TerminalCmd"),
        TerminalChoice.ITerm => Loc.Get("Settings_TerminalITerm"),
        _ => Loc.Get(OperatingSystem.IsMacOS() ? "Settings_TerminalMacDefault" : "Settings_TerminalPowerShell"),
    }).ToArray();

    [ObservableProperty]
    private int _terminalIndex;

    public static IReadOnlyList<string> SshPanelOptions =>
        [Loc.Get("Settings_LeftPanel"), Loc.Get("Settings_RightPanel")];

    [ObservableProperty]
    private ShortcutRow? _selectedShortcut;

    /// <summary>Why the last chord recorded was not taken; null when it was, or nothing was recorded yet.</summary>
    [ObservableProperty]
    private string? _shortcutMessage;

    /// <summary>Why the last <see cref="Apply"/> refused; null after one that succeeded.</summary>
    [ObservableProperty]
    private string? _error;

    private string _shortcutFilter = "";

    /// <param name="keyMap">The key map in effect (the main window's); when null, the default map with the
    /// settings' overrides, which is the same.</param>
    /// <param name="shell">Opens the themes folder; without it <see cref="OpenThemesFolderAsync"/> does nothing.</param>
    public SettingsViewModel(SettingsService settings, KeyMap? keyMap = null, IShellIntegration? shell = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this.shell = shell;
        var current = settings.Current;
        keyMap ??= KeyMap.Platform.WithOverrides(current.KeyBindings);
        _theme = current.Theme;
        colorSchemeId = current.ThemeName ?? BuiltInThemes.StoneId;
        var themes = settings.Themes?.List()
                     ?? [new ThemeInfo(BuiltInThemes.StoneId, BuiltInThemes.Stone.Name, null, null)];
        ColorSchemes = themes.Select(ColorSchemeOption.From).ToArray();
        _fontSize = current.FontSize;
        _rowHeight = current.RowHeight;
        _externalEditor = current.ExternalEditor ?? "";
        _sshInPanelTab = current.SshConsolePlacement == SshConsolePlacement.PanelTab;
        _sshPanelIndex = current.SshConsolePanel == SshConsoleSide.Left ? 0 : 1;
        _terminalIndex = Math.Max(0, TerminalChoices.ToList().IndexOf(current.Terminal));
        FileColors = new(current.FileColors.Select(r => new FileColorRuleRow { Mask = r.Mask, Color = r.Color }));
        LeftColumns = new(current.LeftColumns.Select(c => new ColumnRow(c.Column, c.Visible, c.Width)));
        RightColumns = new(current.RightColumns.Select(c => new ColumnRow(c.Column, c.Visible, c.Width)));
        Buttons = new(current.Buttons.Select(b => new UserButtonRow
            { Title = b.Title, Program = b.Program, Arguments = b.Arguments }));
        _shortcuts = CommandIds.All
            .Select(id => new ShortcutRow(id, keyMap.ChordsFor(id), KeyMap.Platform.ChordsFor(id)))
            .ToArray();
        VisibleShortcuts = new(_shortcuts);
    }

    // The limits of the number fields, for the view.
    public static decimal MinFontSize => (decimal)AppSettings.MinFontSize;
    public static decimal MaxFontSize => (decimal)AppSettings.MaxFontSize;
    public static decimal MinRowHeight => (decimal)AppSettings.MinRowHeight;
    public static decimal MaxRowHeight => (decimal)AppSettings.MaxRowHeight;
    public static decimal MinColumnWidth => (decimal)AppSettings.MinColumnWidth;
    public static decimal MaxColumnWidth => (decimal)AppSettings.MaxColumnWidth;

    // View (a number field left empty holds NaN, which Validate refuses)

    public static IReadOnlyList<ThemeOption> ThemeOptions { get; } =
    [
        new(ThemeChoice.System, Loc.Get("Menu_ThemeSystem")),
        new(ThemeChoice.Light, Loc.Get("Menu_ThemeLight")),
        new(ThemeChoice.Dark, Loc.Get("Menu_ThemeDark")),
    ];

    /// <summary><see cref="Theme"/> as one of <see cref="ThemeOptions"/>, for a list box.</summary>
    public ThemeOption? SelectedTheme
    {
        get => ThemeOptions.FirstOrDefault(o => o.Value == Theme);
        set
        {
            if (value is not null) Theme = value.Value;
        }
    }

    /// <summary>The themes there are, the built-in one first; read when the window opens.</summary>
    public IReadOnlyList<ColorSchemeOption> ColorSchemes { get; }

    /// <summary>The theme <see cref="Apply"/> saves as <see cref="AppSettings.ThemeName"/>; null when the saved name
    /// is not in <see cref="ColorSchemes"/> (then it stays). One that cannot be read is not taken.</summary>
    public ColorSchemeOption? SelectedColorScheme
    {
        get => ColorSchemes.FirstOrDefault(o => string.Equals(o.Id, colorSchemeId, StringComparison.OrdinalIgnoreCase));
        set
        {
            if (value is { IsSelectable: true } && value.Id != colorSchemeId) colorSchemeId = value.Id;
            // A refused choice puts the list back on the theme kept.
            OnPropertyChanged();
        }
    }

    /// <summary>Opens the themes folder in the system file browser, making it first when it is missing. A failure
    /// shows in <see cref="Error"/>.</summary>
    public async Task OpenThemesFolderAsync()
    {
        if (shell is null || _settings.Themes is not { } themes) return;
        try
        {
            Directory.CreateDirectory(themes.Directory);
            await shell.OpenDefault(themes.Directory);
        }
        catch (Exception e)
        {
            // Whatever the launcher throws (a process that cannot start, too) is told here, not lost.
            Error = e.Message;
        }
    }

    public ObservableCollection<FileColorRuleRow> FileColors { get; }

    public FileColorRuleRow AddFileColor() => Add(FileColors, new FileColorRuleRow());

    public void Remove(FileColorRuleRow row) => FileColors.Remove(row);

    public void MoveUp(FileColorRuleRow row) => Move(FileColors, row, -1);

    public void MoveDown(FileColorRuleRow row) => Move(FileColors, row, 1);

    // Columns

    public ObservableCollection<ColumnRow> LeftColumns { get; }

    public ObservableCollection<ColumnRow> RightColumns { get; }

    public void MoveUp(ColumnRow row) => Move(LeftColumns.Contains(row) ? LeftColumns : RightColumns, row, -1);

    public void MoveDown(ColumnRow row) => Move(LeftColumns.Contains(row) ? LeftColumns : RightColumns, row, 1);

    // Keys

    /// <summary>One row per command, in <see cref="CommandIds.All"/> order.</summary>
    public IReadOnlyList<ShortcutRow> Shortcuts => _shortcuts;

    /// <summary>The rows <see cref="ShortcutFilter"/> lets through, in table order. A row edited while the filter
    /// is on stays (so it stays selected) until the filter text changes, even when it no longer matches.</summary>
    public ObservableCollection<ShortcutRow> VisibleShortcuts { get; }

    /// <summary>Shows in <see cref="VisibleShortcuts"/> only the rows whose label or chord text (as shown or as
    /// saved) contains it, case ignored; all rows when blank.</summary>
    public string ShortcutFilter
    {
        get => _shortcutFilter;
        set
        {
            if (SetProperty(ref _shortcutFilter, value ?? "")) ApplyFilter(keepShown: false);
        }
    }

    /// <summary>Gives <paramref name="row"/>'s command <paramref name="chord"/> as its last chord. A chord another
    /// command has is moved only with <paramref name="takeFromOther"/>; a chord that cannot be a shortcut is refused,
    /// and <see cref="ShortcutMessage"/> says why.</summary>
    public ChordAssignment Assign(ShortcutRow row, KeyChord chord, bool takeFromOther = false)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (KeyChordRules.RefusalOf(chord) is { } refusal)
        {
            ShortcutMessage = Loc.Format("Settings_ChordRefused_" + refusal, KeyChordText.Format(chord));
            return ChordAssignment.NotAssignable;
        }
        ShortcutMessage = null;
        if (row.Chords.Contains(chord)) return ChordAssignment.Assigned;
        if (OwnerOf(chord) is { } owner)
        {
            if (!takeFromOther) return ChordAssignment.TakenByOther;
            owner.Chords = owner.Chords.Where(c => c != chord).ToArray();
        }
        row.Chords = [..row.Chords, chord];
        ApplyFilter(keepShown: true);
        return ChordAssignment.Assigned;
    }

    /// <summary>The row whose command has <paramref name="chord"/> in the window, or null.</summary>
    public ShortcutRow? OwnerOf(KeyChord chord) => _shortcuts.FirstOrDefault(r => r.Chords.Contains(chord));

    public void RemoveChord(ShortcutRow row, KeyChord chord)
    {
        ArgumentNullException.ThrowIfNull(row);
        row.Chords = row.Chords.Where(c => c != chord).ToArray();
        ApplyFilter(keepShown: true);
    }

    /// <summary>Gives the command its default chords again, taking them from any command that has them now.</summary>
    public void ResetShortcut(ShortcutRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        foreach (var other in _shortcuts.Where(r => r != row && r.Chords.Any(row.Defaults.Contains)))
            other.Chords = other.Chords.Where(c => !row.Defaults.Contains(c)).ToArray();
        row.Chords = row.Defaults;
        ApplyFilter(keepShown: true);
    }

    /// <summary>Every command gets its default chords: <see cref="Apply"/> then saves no overrides.</summary>
    public void ResetAllShortcuts()
    {
        foreach (var row in _shortcuts) row.Chords = row.Defaults;
        ShortcutMessage = null;
        ApplyFilter(keepShown: true);
    }

    // Button bar and editing

    public ObservableCollection<UserButtonRow> Buttons { get; }

    public UserButtonRow AddButton() => Add(Buttons, new UserButtonRow());

    public void Remove(UserButtonRow row) => Buttons.Remove(row);

    public void MoveUp(UserButtonRow row) => Move(Buttons, row, -1);

    public void MoveDown(UserButtonRow row) => Move(Buttons, row, 1);

    /// <summary>The first thing that keeps the settings from being applied, as localized text; null when they can
    /// be.</summary>
    public string? Validate()
    {
        if (FileColors.Any(r => string.IsNullOrWhiteSpace(r.Mask))) return Loc.Get("Settings_Error_EmptyMask");
        if (Buttons.Any(b => string.IsNullOrWhiteSpace(b.Title) || string.IsNullOrWhiteSpace(b.Program)))
            return Loc.Get("Settings_Error_IncompleteButton");
        if (!InRange(FontSize, AppSettings.MinFontSize, AppSettings.MaxFontSize))
            return Loc.Format("Settings_Error_FontSize", AppSettings.MinFontSize, AppSettings.MaxFontSize);
        if (!InRange(RowHeight, AppSettings.MinRowHeight, AppSettings.MaxRowHeight))
            return Loc.Format("Settings_Error_RowHeight", AppSettings.MinRowHeight, AppSettings.MaxRowHeight);
        if (RowHeight < FontSize + AppSettings.RowPadding)
            return Loc.Format("Settings_Error_RowTooLow", FontSize + AppSettings.RowPadding);
        if (LeftColumns.Concat(RightColumns)
            .Any(c => !InRange(c.Width, AppSettings.MinColumnWidth, AppSettings.MaxColumnWidth)))
            return Loc.Format("Settings_Error_ColumnWidth", AppSettings.MinColumnWidth, AppSettings.MaxColumnWidth);
        return null;
    }

    /// <summary>Makes the window's settings the app's (<see cref="SettingsService.Update"/>), which applies and
    /// saves them; the key bindings as their differences from <see cref="KeyMap.Default"/>. False, with
    /// <see cref="Error"/> set and nothing changed, when <see cref="Validate"/> finds an error.</summary>
    public bool Apply()
    {
        if (Validate() is { } error)
        {
            Error = error;
            return false;
        }
        Error = null;
        var keyMap = new KeyMap(_shortcuts.SelectMany(r => r.Chords.Select(c => (r.CommandId, c))));
        var current = _settings.Current;
        // Overrides of commands this build does not know (a typo, a newer build's command) stay as they were.
        var unknown = current.KeyBindings.Where(b => !CommandIds.All.Contains(b.CommandId));
        _settings.Update(current with
        {
            Theme = Theme,
            ThemeName = SelectedColorScheme is { } scheme
                ? scheme.IsBuiltIn ? null : scheme.Id
                : current.ThemeName,
            SshConsolePlacement = SshInPanelTab ? SshConsolePlacement.PanelTab : SshConsolePlacement.SeparateWindow,
            SshConsolePanel = SshPanelIndex == 0 ? SshConsoleSide.Left : SshConsoleSide.Right,
            Terminal = TerminalChoices[Math.Clamp(TerminalIndex, 0, TerminalChoices.Count - 1)],
            FontSize = FontSize,
            RowHeight = RowHeight,
            LeftColumns = LeftColumns.Select(c => c.ToSetting()).ToArray(),
            RightColumns = RightColumns.Select(c => c.ToSetting()).ToArray(),
            FileColors = FileColors.Select(r => r.ToRule()).ToArray(),
            KeyBindings = [..keyMap.OverridesAgainst(KeyMap.Platform), ..unknown],
            Buttons = Buttons.Select(b => b.ToButton()).ToArray(),
            ExternalEditor = string.IsNullOrWhiteSpace(ExternalEditor) ? null : ExternalEditor.Trim(),
        });
        // The numbers as the settings keep them (a row is never lower than the font needs).
        FontSize = _settings.Current.FontSize;
        RowHeight = _settings.Current.RowHeight;
        return true;
    }

    /// <summary>False for NaN too (an empty field).</summary>
    private static bool InRange(double value, double min, double max) => value >= min && value <= max;

    /// <param name="keepShown">After an edit: the rows shown now stay, so the edited one does not vanish from
    /// under the user; rows that match now are added.</param>
    private void ApplyFilter(bool keepShown)
    {
        var filter = ShortcutFilter.Trim();
        var shown = keepShown ? VisibleShortcuts.ToHashSet() : [];
        var rows = _shortcuts.Where(r => filter.Length == 0 || r.Matches(filter) || shown.Contains(r)).ToList();
        // Changed in place, so the list keeps its selection.
        for (var k = VisibleShortcuts.Count - 1; k >= 0; k--)
            if (!rows.Contains(VisibleShortcuts[k])) VisibleShortcuts.RemoveAt(k);
        for (var k = 0; k < rows.Count; k++)
            if (k >= VisibleShortcuts.Count || VisibleShortcuts[k] != rows[k]) VisibleShortcuts.Insert(k, rows[k]);
    }

    private static T Add<T>(ObservableCollection<T> rows, T row)
    {
        rows.Add(row);
        return row;
    }

    private static void Move<T>(ObservableCollection<T> rows, T row, int step)
    {
        var index = rows.IndexOf(row);
        var target = index + step;
        if (index < 0 || target < 0 || target >= rows.Count) return;
        rows.Move(index, target);
    }
}
