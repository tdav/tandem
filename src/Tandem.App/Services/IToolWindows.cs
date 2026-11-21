using Avalonia.Controls;

namespace Tandem.App.Services;

/// <summary>A dialog's model: it asks for its window to close and gives the answer (default: closed without one).
/// Raised on the UI thread.</summary>
public interface IDialogModel<TResult>
{
    event EventHandler<TResult?>? CloseRequested;
}

/// <summary>
/// The windows of the tools and of the network part (multi-rename, comparison, synchronization, checksums, the
/// console…). Which window shows which model is decided by a table in <see cref="ToolWindows"/>, by the model's type.
/// Called on the UI thread. The main window closes them when the app quits.
/// </summary>
public interface IToolWindows
{
    /// <summary>A non-modal window for <paramref name="viewModel"/>; the window already open for the same model comes
    /// to the front instead. Closing the window disposes the model (<see cref="IAsyncDisposable"/> or
    /// <see cref="IDisposable"/>).</summary>
    void Show(object viewModel);

    /// <summary>A modal window for <paramref name="viewModel"/> (to <paramref name="owner"/>, else to the main
    /// window), completing when it closes: with the answer of <see cref="IDialogModel{TResult}.CloseRequested"/>, or
    /// default when the window is closed with its close button or Esc (or there is no window to be modal to). The model
    /// is disposed when the window closes.</summary>
    Task<TResult?> ShowDialogAsync<TResult>(IDialogModel<TResult> viewModel, Window? owner = null);

    /// <summary>True while any window of this service is open.</summary>
    bool HasOpenWindows { get; }

    /// <summary>Closes every window, oldest first; a model that is an <see cref="ICloseGuard"/> is asked first. False
    /// when one refused: it and the windows after it stay open.</summary>
    Task<bool> CloseAllAsync();
}

/// <summary>The model of a window that may not close silently: <see cref="IToolWindows"/> asks it when its window is
/// closed and in <see cref="IToolWindows.CloseAllAsync"/>; false keeps the window open.</summary>
public interface ICloseGuard
{
    Task<bool> CanCloseAsync();
}

/// <summary>
/// A ready "go ahead / cancel" question for the tools and the console (undoing a rename, confirming a
/// synchronization, closing a console), shown through <see cref="IToolWindows.ShowDialogAsync{TResult}"/> in one
/// shared window; in tests <c>ScriptedToolWindows.OnDialog</c> answers it. True to go ahead. With
/// <paramref name="cancelIsDefault"/> Enter cancels; Esc and closing the window always cancel.
/// </summary>
/// <param name="text">The question, localized.</param>
/// <param name="detail">Secondary text under it (counts, what cannot be undone), or null.</param>
/// <param name="confirmLabel">The label of the button that goes ahead, localized.</param>
public sealed class ConfirmModel(string text, string? detail, string confirmLabel, bool cancelIsDefault = true)
    : IDialogModel<bool>
{
    public string Text { get; } = text ?? throw new ArgumentNullException(nameof(text));

    public string? Detail { get; } = detail;

    public string ConfirmLabel { get; } = confirmLabel ?? throw new ArgumentNullException(nameof(confirmLabel));

    public bool CancelIsDefault { get; } = cancelIsDefault;

    public event EventHandler<bool>? CloseRequested;

    /// <summary>The go-ahead button: closes the window answering true.</summary>
    public void Confirm() => CloseRequested?.Invoke(this, true);

    /// <summary>The cancel button: closes the window answering false.</summary>
    public void Cancel() => CloseRequested?.Invoke(this, false);
}
