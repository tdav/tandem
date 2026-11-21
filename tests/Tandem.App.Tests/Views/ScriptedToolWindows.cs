using System.Reflection;
using Avalonia.Controls;
using Tandem.App.Services;

namespace Tandem.App.Tests.Views;

/// <summary>
/// Tool windows without windows: records every model shown and answers the modal ones through
/// <see cref="OnDialog"/>. A non-modal model stays "open" until <see cref="CloseAsync"/> or
/// <see cref="CloseAllAsync"/>, which ask its <see cref="ICloseGuard"/> and dispose it as the real
/// <see cref="ToolWindows"/> does, or until it raises <see cref="IDialogModel{TResult}.CloseRequested"/> (closed and
/// disposed at once, no guard asked); a modal one is disposed once answered.
/// </summary>
internal sealed class ScriptedToolWindows : IToolWindows
{
    private static readonly MethodInfo CloseOnRequestMethod =
        typeof(ScriptedToolWindows).GetMethod(nameof(CloseOnRequest), BindingFlags.NonPublic | BindingFlags.Instance)!;

    private readonly List<object> _open = [];

    /// <summary>Every model shown, modal or not, in order.</summary>
    public List<object> Shown { get; } = [];

    /// <summary>The models shown modally, in order, with the window and the model whose window they were to be
    /// modal to (both null: the main window).</summary>
    public List<(object Model, Window? Owner, object? OwnerModel)> Dialogs { get; } = [];

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
        if (_open.Contains(viewModel)) return;
        _open.Add(viewModel);
        foreach (var type in viewModel.GetType().GetInterfaces())
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IDialogModel<>))
                CloseOnRequestMethod.MakeGenericMethod(type.GetGenericArguments()[0]).Invoke(this, [viewModel]);
        }
    }

    private void CloseOnRequest<TResult>(object viewModel)
    {
        var model = (IDialogModel<TResult>)viewModel;
        void OnCloseRequested(object? sender, TResult? answer)
        {
            model.CloseRequested -= OnCloseRequested;
            if (!_open.Remove(viewModel)) return;
            _ = DisposeAsync(viewModel);
        }

        model.CloseRequested += OnCloseRequested;
    }

    public async Task<TResult?> ShowDialogAsync<TResult>(IDialogModel<TResult> viewModel, Window? owner = null,
        object? ownerModel = null)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        Shown.Add(viewModel);
        Dialogs.Add((viewModel, owner, ownerModel));
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
