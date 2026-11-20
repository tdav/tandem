using Avalonia.Controls;
using Avalonia.Interactivity;
using Tandem.App.Commands;
using Tandem.App.Services;
using Tandem.App.ViewModels.Settings;

namespace Tandem.App.Views.Settings;

/// <summary>
/// The settings window over a <see cref="SettingsViewModel"/>: OK applies and closes, Apply applies and stays, Cancel
/// (or Esc, or closing the window) drops what was not applied. A shortcut recorded for the selected command is given
/// to it; one another command has is moved only after <see cref="IDialogService.ConfirmReassignShortcutAsync"/>,
/// modal to this window.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly IDialogService? _dialogs;

    /// <summary>For the designer only.</summary>
    public SettingsWindow()
    {
        InitializeComponent();
        Settings = null!;
    }

    public SettingsWindow(SettingsViewModel settings, IDialogService dialogs)
    {
        ArgumentNullException.ThrowIfNull(settings);
        InitializeComponent();
        Settings = settings;
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        DataContext = settings;
        Recorder.ChordRecorded += OnChordRecorded;
        // The rows' buttons (move, remove) are told apart by their class and act on their Tag.
        AddHandler(Button.ClickEvent, OnRowButtonClick);
    }

    public SettingsViewModel Settings { get; }

    /// <summary>True once OK or Apply has applied the settings.</summary>
    public bool Applied { get; private set; }

    private async void OnChordRecorded(object? sender, KeyChord chord)
    {
        if (Settings.SelectedShortcut is not { } row || _dialogs is not { } dialogs) return;
        if (Settings.Assign(row, chord) != ChordAssignment.TakenByOther || Settings.OwnerOf(chord) is not { } owner)
            return;
        if (await dialogs.ConfirmReassignShortcutAsync(KeyChordText.Format(chord), owner.Label, this))
            Settings.Assign(row, chord, takeFromOther: true);
    }

    private void OnRowButtonClick(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not Button { Tag: { } tag } button) return;
        var classes = button.Classes;
        switch (tag)
        {
            case KeyChord chord when classes.Contains("removeChord") && Settings.SelectedShortcut is { } row:
                Settings.RemoveChord(row, chord);
                break;
            case FileColorRuleRow rule:
                if (classes.Contains("moveUp")) Settings.MoveUp(rule);
                else if (classes.Contains("moveDown")) Settings.MoveDown(rule);
                else if (classes.Contains("remove")) Settings.Remove(rule);
                break;
            case ColumnRow column:
                if (classes.Contains("moveUp")) Settings.MoveUp(column);
                else if (classes.Contains("moveDown")) Settings.MoveDown(column);
                break;
            case UserButtonRow userButton:
                if (classes.Contains("moveUp")) Settings.MoveUp(userButton);
                else if (classes.Contains("moveDown")) Settings.MoveDown(userButton);
                else if (classes.Contains("remove")) Settings.Remove(userButton);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        if (!Settings.Apply()) return;
        Applied = true;
        Close();
    }

    private void OnApply(object? sender, RoutedEventArgs e)
    {
        if (Settings.Apply()) Applied = true;
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private void OnResetShortcut(object? sender, RoutedEventArgs e)
    {
        if (Settings.SelectedShortcut is { } row) Settings.ResetShortcut(row);
    }

    private void OnResetAllShortcuts(object? sender, RoutedEventArgs e) => Settings.ResetAllShortcuts();

    private void OnAddFileColor(object? sender, RoutedEventArgs e) => Settings.AddFileColor();

    private void OnAddButton(object? sender, RoutedEventArgs e) => Settings.AddButton();
}
