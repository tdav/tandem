using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tandem.App.ViewModels;

namespace Tandem.App.Views;

/// <summary>
/// The name editor shown inside a row while <see cref="EntryRowViewModel.Rename"/> is set (its data context is the
/// <see cref="InlineRenameViewModel"/>). It takes the focus with the name's part before the extension selected.
/// Enter commits, Esc cancels, losing the focus cancels; a refused name keeps it open with the reason as a tool tip.
/// Being a <see cref="TextBox"/>, the window's focus rule treats it as a text editor.
/// </summary>
public sealed class InlineRenameEditor : TextBox
{
    private InlineRenameViewModel? _session;
    private EntryRowViewModel? _row; // the row this control was made for; the session may move on to a newer one
    private bool _tracking; // the selection is mirrored into the session once this control has set it up

    public InlineRenameEditor()
    {
        Name = "RenameEditor";
        MinHeight = 0;
        Padding = new Thickness(2, 0);
        VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center;
        TextChanged += (_, _) =>
        {
            if (_session is { IsOpen: true } session) session.Text = Text ?? "";
        };
    }

    protected override Type StyleKeyOverride => typeof(TextBox);

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_session is not null) _session.PropertyChanged -= OnSessionPropertyChanged;
        _tracking = false;
        _session = DataContext as InlineRenameViewModel;
        if (_session is not { } session) return;
        _row = session.Row;
        session.PropertyChanged += OnSessionPropertyChanged;
        Text = session.Text;
        ShowError(null);
        // A recycled row may hand this control a session without detaching and attaching it again.
        if (IsAttachedToVisualTree()) TakeFocusLater();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        TakeFocusLater();
    }

    private void TakeFocusLater()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_session is not { IsOpen: true } session || !IsAttachedToVisualTree()) return;
            Focus();
            // Where the user was: the name part at first, the caret and selection after a refresh moved the editor.
            SelectionStart = session.SelectionStart;
            SelectionEnd = session.SelectionEnd;
            _tracking = true;
        }, DispatcherPriority.Loaded);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SelectionStartProperty || change.Property == SelectionEndProperty ||
            change.Property == CaretIndexProperty)
            RememberSelection();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _tracking = false;
        ToolTip.SetIsOpen(this, false);
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_session is { } session && e.KeyModifiers == KeyModifiers.None)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                _ = CommitAsync(session);
                return;
            }
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                var list = this.FindAncestorOfType<FileListView>();
                session.Cancel();
                list?.FocusList();
                return;
            }
        }
        base.OnKeyDown(e);
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        // Nothing happens if it was committed or cancelled already. When a refresh replaced the rows, the session has
        // moved to the new row's editor and this control is merely going away: that is not the user leaving.
        if (_session is { } session && ReferenceEquals(session.Row, _row)) session.Cancel();
    }

    private void RememberSelection()
    {
        if (!_tracking || _session is not { IsOpen: true } session || !ReferenceEquals(session.Row, _row)) return;
        session.SelectionStart = Math.Min(SelectionStart, SelectionEnd);
        session.SelectionEnd = Math.Max(SelectionStart, SelectionEnd);
        if (session.SelectionStart == session.SelectionEnd) session.SelectionStart = session.SelectionEnd = CaretIndex;
    }

    /// <summary>Never throws: the session reports a failing rename to the app's failure path itself.</summary>
    private async Task CommitAsync(InlineRenameViewModel session)
    {
        var list = this.FindAncestorOfType<FileListView>();
        if (await session.CommitAsync()) list?.FocusList();
    }

    private void OnSessionPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(InlineRenameViewModel.Error)) ShowError(_session?.Error);
    }

    private void ShowError(string? error)
    {
        ToolTip.SetTip(this, error);
        ToolTip.SetIsOpen(this, error is not null && IsAttachedToVisualTree());
        Classes.Set("error", error is not null);
    }

    private bool IsAttachedToVisualTree() => TopLevel.GetTopLevel(this) is not null;
}
