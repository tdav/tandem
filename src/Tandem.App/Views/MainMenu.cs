using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using CommunityToolkit.Mvvm.Input;
using Tandem.App.Commands;
using Tandem.App.Resources;
using Tandem.App.ViewModels;
using Tandem.Core.Listing;

namespace Tandem.App.Views;

/// <summary>
/// The application's native menu: File, Selection, Go, View, Window. Every command has an item (one registered by a
/// later part of the app shows disabled until then), labelled from <c>Menu_&lt;id&gt;</c> and showing its first
/// chord from the key map.
/// </summary>
public static class MainMenu
{
    private static readonly string[] FileCommands =
    [
        CommandIds.FileView, CommandIds.FileEdit, CommandIds.FileCopy, CommandIds.FileMove, CommandIds.FileMkdir,
        CommandIds.FileRename, CommandIds.FileDelete, CommandIds.FileDeletePermanent, "-",
        CommandIds.FileQuickLook, CommandIds.FileProperties, "-",
        CommandIds.ShellReveal, CommandIds.ShellOpenTerminal, CommandIds.ShellOpenWith,
    ];

    private static readonly string[] SelectionCommands =
    [
        CommandIds.SelectToggleDown, CommandIds.SelectAll, CommandIds.SelectInvert, "-",
        CommandIds.SelectByMask, CommandIds.UnselectByMask,
    ];

    private static readonly string[] GoCommands =
    [
        CommandIds.NavUp, CommandIds.NavBack, CommandIds.NavForward, CommandIds.PathEdit, CommandIds.FavoritesShow, "-",
        CommandIds.PanelSwitch, CommandIds.PanelSwap, CommandIds.PanelMirror, CommandIds.PanelRefresh, "-",
        CommandIds.CmdlineFocus,
    ];

    private static readonly string[] WindowCommands =
    [
        CommandIds.TabNew, CommandIds.TabClose, CommandIds.TabNext, CommandIds.TabPrev,
    ];

    /// <summary>The resource key of a command's menu label: "Menu_" and the id with '.' as '_'.</summary>
    public static string LabelKey(string commandId) => "Menu_" + commandId.Replace('.', '_');

    public static NativeMenu Build(MainViewModel main)
    {
        ArgumentNullException.ThrowIfNull(main);
        var menu = new NativeMenu();
        menu.Items.Add(Submenu("Menu_File", Commands(main, FileCommands)));
        menu.Items.Add(Submenu("Menu_Selection", Commands(main, SelectionCommands)));
        menu.Items.Add(Submenu("Menu_Go", Commands(main, GoCommands)));
        menu.Items.Add(Submenu("Menu_View", ViewItems(main)));
        menu.Items.Add(Submenu("Menu_Window", Commands(main, WindowCommands)));
        return menu;
    }

    private static NativeMenuItem Submenu(string labelKey, IEnumerable<NativeMenuItemBase> items)
    {
        var submenu = new NativeMenu();
        foreach (var item in items) submenu.Items.Add(item);
        return new NativeMenuItem(Loc.Get(labelKey)) { Menu = submenu };
    }

    private static IEnumerable<NativeMenuItemBase> Commands(MainViewModel main, IEnumerable<string> ids) =>
        ids.Select(id => id == "-" ? new NativeMenuItemSeparator() : (NativeMenuItemBase)CommandItem(main, id));

    private static NativeMenuItem CommandItem(MainViewModel main, string id)
    {
        var item = new NativeMenuItem(Loc.Get(LabelKey(id)))
        {
            Command = main.RunCommand,
            CommandParameter = id,
        };
        if (main.KeyMap.ChordsFor(id) is [var chord, ..]) item.Gesture = new KeyGesture(chord.Key, chord.Modifiers);
        return item;
    }

    private static IEnumerable<NativeMenuItemBase> ViewItems(MainViewModel main)
    {
        var workspace = main.Workspace;
        var sidebar = Toggle("Menu_Sidebar", main.ToggleSidebar);
        var volumeBars = Toggle("Menu_VolumeBars", main.ToggleVolumeBars);
        var commandLine = Toggle("Menu_CommandLine", main.ToggleCommandLine);
        var fkeyBar = Toggle("Menu_FKeyBar", main.ToggleFKeyBar);
        var hidden = CommandItem(main, CommandIds.ViewToggleHidden);
        hidden.ToggleType = MenuItemToggleType.CheckBox;

        (string? Code, NativeMenuItem Item)[] languages =
        [
            (null, Radio("Menu_LanguageSystem", () => main.SetLanguage(null))),
            ("ru", Radio("Menu_LanguageRussian", () => main.SetLanguage("ru"))),
            ("en", Radio("Menu_LanguageEnglish", () => main.SetLanguage("en"))),
        ];
        var languageMenu = new NativeMenu();
        foreach (var (_, item) in languages) languageMenu.Items.Add(item);

        void Update()
        {
            var view = workspace.View;
            sidebar.IsChecked = view.Sidebar;
            volumeBars.IsChecked = view.VolumeBars;
            commandLine.IsChecked = view.CommandLine;
            fkeyBar.IsChecked = view.FKeyBar;
            hidden.IsChecked = workspace.ShowHidden;
            foreach (var (code, item) in languages) item.IsChecked = workspace.Language == code;
        }

        Update();
        workspace.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(WorkspaceViewModel.View) or nameof(WorkspaceViewModel.ShowHidden)
                or nameof(WorkspaceViewModel.Language)) Update();
        };

        return
        [
            sidebar, volumeBars, commandLine, fkeyBar, new NativeMenuItemSeparator(), hidden,
            new NativeMenuItemSeparator(),
            Submenu("Menu_Sort", SortItems(main)),
            new NativeMenuItemSeparator(),
            new NativeMenuItem(Loc.Get("Menu_Language")) { Menu = languageMenu },
        ];
    }

    /// <summary>The sort commands of the active panel: its column checked, and "Reversed" checked while it sorts
    /// the other way round.</summary>
    private static IEnumerable<NativeMenuItemBase> SortItems(MainViewModel main)
    {
        (SortColumn Column, NativeMenuItem Item)[] columns =
        [
            (SortColumn.Name, CommandItem(main, CommandIds.SortByName)),
            (SortColumn.Extension, CommandItem(main, CommandIds.SortByExtension)),
            (SortColumn.Size, CommandItem(main, CommandIds.SortBySize)),
            (SortColumn.Modified, CommandItem(main, CommandIds.SortByDate)),
        ];
        foreach (var (_, item) in columns) item.ToggleType = MenuItemToggleType.Radio;
        var reverse = CommandItem(main, CommandIds.SortReverse);
        reverse.ToggleType = MenuItemToggleType.CheckBox;

        void Update()
        {
            var sort = main.ActiveSide.Panel?.Sort;
            foreach (var (column, item) in columns) item.IsChecked = sort?.Column == column;
            reverse.IsChecked = sort?.Descending == true;
        }

        Update();
        main.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.ActiveSide)) Update();
        };
        foreach (var side in new[] { main.Left, main.Right })
            side.PropertyChanged += (_, e) =>
            {
                // A side raises its sort marks when its panel's sort changes or another tab's panel is shown.
                if (e.PropertyName is nameof(SideViewModel.Panel) or nameof(SideViewModel.NameSortMark)) Update();
            };

        return [..columns.Select(c => c.Item), new NativeMenuItemSeparator(), reverse];
    }

    private static NativeMenuItem Toggle(string labelKey, Action toggle) => new(Loc.Get(labelKey))
    {
        ToggleType = MenuItemToggleType.CheckBox,
        Command = new RelayCommand(toggle),
    };

    private static NativeMenuItem Radio(string labelKey, Action choose) => new(Loc.Get(labelKey))
    {
        ToggleType = MenuItemToggleType.Radio,
        Command = new RelayCommand(choose),
    };
}
