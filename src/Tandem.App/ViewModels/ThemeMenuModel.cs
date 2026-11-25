using Tandem.App.Resources;
using Tandem.App.Services;
using Tandem.Core.Themes;

namespace Tandem.App.ViewModels;

/// <summary>A theme in the "View → Theme" menu: its label (a broken file's marked), why its file cannot be read, and
/// whether it is the chosen one.</summary>
public sealed record ThemeMenuEntry(string Id, string Label, string? Error, bool IsSelected)
{
    /// <summary>A theme whose file cannot be read is shown but cannot be chosen.</summary>
    public bool IsEnabled => Error is null;
}

/// <summary>
/// The themes of the "View → Theme" menu: the built-in one and every file of <see cref="SettingsService.Themes"/>,
/// in the catalog's order, read anew by every <see cref="Entries"/>. <see cref="Choose"/> saves the choice as
/// <see cref="Tandem.Core.Settings.AppSettings.ThemeName"/>, as the settings window's Apply does, and the main view
/// model applies it on the settings change.
/// </summary>
public sealed class ThemeMenuModel(SettingsService settings)
{
    private readonly SettingsService settings = settings ?? throw new ArgumentNullException(nameof(settings));

    /// <summary>The themes there are now, the chosen one selected.</summary>
    public IReadOnlyList<ThemeMenuEntry> Entries() =>
        Themes().Select(t => new ThemeMenuEntry(t.Id,
            t.Error is null ? t.Name : Loc.Format("Settings_ColorSchemeBroken", t.Name), t.Error,
            t.Error is null && IsChosen(t.Id))).ToList();

    /// <summary>Whether <paramref name="id"/> is the theme of the current settings (the built-in one when none is
    /// named).</summary>
    public bool IsChosen(string id) =>
        string.Equals(id, settings.Current.ThemeName ?? BuiltInThemes.StoneId, StringComparison.OrdinalIgnoreCase);

    /// <summary>Makes the theme <paramref name="id"/> the chosen one and saves the settings; false (nothing changes)
    /// when there is no such theme or its file cannot be read.</summary>
    public bool Choose(string id)
    {
        var theme = Themes().FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
        if (theme is not { Error: null }) return false;
        // The built-in theme is saved as no name; a file named like it (it replaces it) by its name.
        var name = theme.Path is null ? null : theme.Id;
        var current = settings.Current;
        if (!string.Equals(current.ThemeName, name, StringComparison.Ordinal))
            settings.Update(current with { ThemeName = name });
        return true;
    }

    private IReadOnlyList<ThemeInfo> Themes() =>
        settings.Themes?.List() ?? [new ThemeInfo(BuiltInThemes.StoneId, BuiltInThemes.Stone.Name, null, null)];
}
