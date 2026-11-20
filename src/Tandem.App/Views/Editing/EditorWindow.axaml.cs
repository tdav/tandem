using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Tandem.App.Commands;
using Tandem.App.Input;
using Tandem.App.Resources;
using Tandem.App.Services;
using Tandem.App.ViewModels.Editing;

namespace Tandem.App.Views.Editing;

/// <summary>
/// The window of one <see cref="EditorViewModel"/>: an AvaloniaEdit text editor (line numbers, its search panel on
/// Cmd+F, undo on Cmd+Z). Cmd+S saves; Cmd+W and Esc close the window (Esc closes an open search panel first). Closing
/// it with unsaved changes, in any way, asks first (<see cref="IDialogService.AskSaveChangesAsync"/>): Save closes only
/// once the save succeeded, Cancel keeps the window open.
/// </summary>
public partial class EditorWindow : Window
{
    private static readonly KeyChord SaveChord = KeyChord.Parse("Cmd+S");
    private static readonly KeyChord CloseChord = KeyChord.Parse("Cmd+W");
    private static readonly KeyChord EscapeChord = new(Key.Escape, KeyModifiers.None);

    private readonly IDialogService? _dialogs;
    private Task<bool>? _closing;
    private bool _closeConfirmed;
    private bool _closed;

    /// <summary>For the designer only.</summary>
    public EditorWindow()
    {
        InitializeComponent();
        Editor = null!;
    }

    public EditorWindow(EditorViewModel editor, IDialogService dialogs)
    {
        ArgumentNullException.ThrowIfNull(editor);
        InitializeComponent();
        Editor = editor;
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        DataContext = editor;
        TextEditor.Document = editor.Document;
        EncodingText.Text = (editor.HasBom ? Loc.Format("Editor_EncodingWithBom", editor.Encoding.Id) : editor.Encoding.Id)
                            + (editor.IsReadOnly ? " · " + Loc.Get("Editor_ReadOnlyStatus") : "");
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        Opened += (_, _) => TextEditor.TextArea.Focus();
        Closing += OnClosing;
        Closed += (_, _) => _closed = true;
    }

    /// <summary>The file shown.</summary>
    public EditorViewModel Editor { get; }

    /// <summary>Closes the window, asking first while there are unsaved changes. True once it is closed; false when
    /// the user kept it open or the save they chose failed. A close already asking gives that question's
    /// answer.</summary>
    public Task<bool> CloseAsync()
    {
        if (_closing is { IsCompleted: false } asking) return asking;
        return _closing = CloseCoreAsync();
    }

    private async Task<bool> CloseCoreAsync()
    {
        if (_closed) return true;
        if (Editor.IsModified && _dialogs is { } dialogs)
        {
            var answer = await dialogs.AskSaveChangesAsync(Editor.File.Name);
            if (answer == SaveChangesAnswer.Cancel) return false;
            if (answer == SaveChangesAnswer.Save && !await Editor.SaveAsync()) return false;
        }
        _closeConfirmed = true;
        Close();
        return true;
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeConfirmed || !Editor.IsModified) return;
        e.Cancel = true;
        Run(CloseAsync);
    }

    /// <summary>Starts <paramref name="action"/> from an event handler; what it throws unexpectedly goes to the UI
    /// thread's unhandled-exception handler (the app logs and shows it).</summary>
    private static async void Run(Func<Task> action) => await action();

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        var chord = KeyRouting.ChordFor(e.Key, e.PhysicalKey, e.KeyModifiers);
        if (chord == SaveChord)
        {
            e.Handled = true;
            Run(Editor.SaveAsync);
        }
        else if (chord == CloseChord || chord == EscapeChord && !TextEditor.SearchPanel.IsOpened)
        {
            e.Handled = true;
            Run(CloseAsync);
        }
    }
}
