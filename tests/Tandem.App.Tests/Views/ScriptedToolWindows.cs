using Avalonia.Controls;
using Tandem.App.Services;

namespace Tandem.App.Tests.Views;

/// <summary>
/// Tool windows without windows: records every model shown and answers the modal ones through
/// <see cref="OnDialog"/>. A non-modal model stays "open" until <see cref="CloseAsync"/> or
/// <see cref="CloseAllAsync"/>, which ask its <see cref="ICloseGuard"/> and dispose it as the real
/// <see cref="ToolWindows"/> does; a modal one is disposed once answered.
/// </summary>
internal sealed class ScriptedToolWindows : IToolWindows
{
    private readonly List<object> _open = [];

    /// <summary>Every model shown, modal or not, in order.</summary>
    public List<object> Shown { get; } = [];

    /// <summary>The models shown modally, in order, with the window they were to be modal to (null: the main
    /// window).</summary>
    public List<(object Model, Window? Owner)> Dialogs { get; } = [];

    /// <summary>Answers a modal window: the model in, the dialog's result out (null: closed without an answer, the
    /// default). Unset, every modal window answers the default.</summary>
    public Func<object, object?>? OnDialog { get; set; }

    /// <summary>The non-modal models still "open", oldest first.</summary>
    public IReadOnlyList<object> Open => _open;

    public bool HasOpenWindows => _open.Count > 0;

    public void Show(object viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        Shown.Add(viewModel);
        if (!_open.Contains(viewModel)) _open.Add(viewModel);
    }

    public async Task<TResult?> ShowDialogAsync<TResult>(IDialogModel<TResult> viewModel, Window? owner = null)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        Shown.Add(viewModel);
        Dialogs.Add((viewModel, owner));
        var answer = OnDialog?.Invoke(viewModel);
        await DisposeAsync(viewModel);
        return answer is null ? default : (TResult)answer;
    }

    /// <summary>The user closes the window of <paramref name="viewModel"/>: its guard is asked; false when it kept the
    /// window open.</summary>
    public async Task<bool> CloseAsync(object viewModel)
    {
        if (!_open.Contains(viewModel)) return true;
        if (viewModel is ICloseGuard guard && !await guard.CanCloseAsync()) return false;
        _open.Remove(viewModel);
        await DisposeAsync(viewModel);
        return true;
    }

    public async Task<bool> CloseAllAsync()
    {
        foreach (var model in _open.ToArray())
            if (!await CloseAsync(model))
                return false;
        return true;
    }

    private static async Task DisposeAsync(object model)
    {
        switch (model)
        {
            case IAsyncDisposable asyncDisposable:
                await asyncDisposable.DisposeAsync();
                break;
            case IDisposable disposable:
                disposable.Dispose();
                break;
        }
    }
}
