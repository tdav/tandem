using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Tandem.App.Commands;
using Tandem.App.Input;
using Tandem.App.ViewModels.Search;

namespace Tandem.App.Views.Search;

/// <summary>
/// The file search window (Alt+F7, Cmd+F): the criteria of a <see cref="SearchViewModel"/>, its results and its
/// status. Enter in the criteria starts the search; Enter or a double click on a result goes to it; Esc stops a running
/// search and otherwise closes the window, as Cmd+W does (fixed keys, not commands). Once closed, the model shown is
/// disposed, which stops its search.
/// </summary>
public partial class SearchWindow : Window
{
    private static readonly KeyChord CloseChord = KeyChord.Parse("Cmd+W");

    /// <summary>For the designer only.</summary>
    public SearchWindow()
    {
        InitializeComponent();
        Search = null!;
    }

    public SearchWindow(SearchViewModel search)
    {
        ArgumentNullException.ThrowIfNull(search);
        InitializeComponent();
        Search = null!;
        Attach(search);
        // Enter before the text boxes see it (they may take it); Esc and Cmd+W after the controls (an open list or
        // calendar closes on Esc first).
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Bubble);
        StartButton.Click += (_, _) => Run(Search.StartAsync());
        StopButton.Click += (_, _) => Search.Stop();
        ShowInPanelButton.Click += (_, _) => Run(Search.ShowInPanelAsync());
        ResultsList.DoubleTapped += (_, e) =>
        {
            if (e.Source is Visual source && source.FindAncestorOfType<ListBoxItem>(includeSelf: true) is not null)
                GoToSelected();
        };
        Opened += (_, _) => NameMasksBox.Focus();
        Closed += (_, _) =>
        {
            Search.CloseRequested -= OnCloseRequested;
            Search.Dispose();
        };
    }

    /// <summary>The search shown.</summary>
    public SearchViewModel Search { get; private set; }

    /// <summary>Shows <paramref name="search"/> instead of the search shown now, which is disposed (stopping it).</summary>
    public void Attach(SearchViewModel search)
    {
        ArgumentNullException.ThrowIfNull(search);
        if (ReferenceEquals(search, Search)) return;
        if (Search is { } previous)
        {
            previous.CloseRequested -= OnCloseRequested;
            previous.Dispose();
        }
        Search = search;
        DataContext = search;
        search.CloseRequested += OnCloseRequested;
    }

    private void OnCloseRequested(object? sender, EventArgs e) => Close();

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var chord = KeyRouting.ChordFor(e.Key, e.PhysicalKey, e.KeyModifiers);
        if (chord == CloseChord)
        {
            e.Handled = true;
            Close();
            return;
        }
        if (chord.Modifiers != KeyModifiers.None) return;
        switch (chord.Key)
        {
            case Key.Escape:
                e.Handled = true;
                if (Search.IsRunning) Search.Stop();
                else Close();
                break;
        }
    }

    /// <summary>Enter on the results goes to the selected one; in the criteria (check boxes included) it starts the
    /// search, except in a date field or a list, which use Enter themselves, and on a button, which it presses.</summary>
    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None || e.Source is not Visual source) return;
        if (ReferenceEquals(source, ResultsList) || ResultsList.IsVisualAncestorOf(source))
        {
            e.Handled = true;
            GoToSelected();
            return;
        }
        if (source.FindAncestorOfType<CalendarDatePicker>(includeSelf: true) is not null ||
            source.FindAncestorOfType<ComboBox>(includeSelf: true) is not null ||
            source.FindAncestorOfType<Button>(includeSelf: true) is not null and not ToggleButton)
            return;
        e.Handled = true;
        Run(Search.StartAsync());
    }

    private void GoToSelected()
    {
        if (ResultsList.SelectedItem is SearchResultRow row) Run(Search.GoToAsync(row));
    }

    /// <summary>What <paramref name="task"/> throws unexpectedly goes to the UI thread's unhandled-exception handler
    /// (the app logs and shows it).</summary>
    private static async void Run(Task task) => await task;
}
