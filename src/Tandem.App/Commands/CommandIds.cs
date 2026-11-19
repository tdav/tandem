using System.Reflection;

namespace Tandem.App.Commands;

/// <summary>
/// The id of every command the app has. Key bindings (<see cref="KeyMap"/>) and menus refer to commands by these ids;
/// <see cref="CommandRegistry"/> holds what each one does.
/// </summary>
public static class CommandIds
{
    public const string FileView = "file.view";
    public const string FileEdit = "file.edit";
    public const string FileCopy = "file.copy";
    public const string FileMove = "file.move";
    public const string FileMkdir = "file.mkdir";
    public const string FileDelete = "file.delete";
    public const string FileDeletePermanent = "file.deletePermanent";
    public const string FileRename = "file.rename";
    public const string FileQuickLook = "file.quickLook";
    public const string FileProperties = "file.properties";

    public const string PanelSwitch = "panel.switch";
    public const string PanelRefresh = "panel.refresh";
    public const string PanelSwap = "panel.swap";
    public const string PanelMirror = "panel.mirror";

    public const string NavUp = "nav.up";
    public const string NavBack = "nav.back";
    public const string NavForward = "nav.forward";

    public const string PathEdit = "path.edit";
    public const string CmdlineFocus = "cmdline.focus";

    public const string SelectToggleDown = "select.toggleDown";
    public const string SelectAll = "select.all";
    public const string SelectByMask = "select.byMask";
    public const string UnselectByMask = "unselect.byMask";
    public const string SelectInvert = "select.invert";

    public const string TabNew = "tab.new";
    public const string TabClose = "tab.close";
    public const string TabNext = "tab.next";
    public const string TabPrev = "tab.prev";

    public const string FavoritesShow = "favorites.show";
    public const string ViewToggleHidden = "view.toggleHidden";

    // Invoked from menus only; no default key binding.
    public const string ShellReveal = "shell.reveal";
    public const string ShellOpenTerminal = "shell.openTerminal";
    public const string ShellOpenWith = "shell.openWith";

    /// <summary>Every id declared above, in declaration order.</summary>
    public static IReadOnlyList<string> All { get; } =
        typeof(CommandIds)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToArray();
}
