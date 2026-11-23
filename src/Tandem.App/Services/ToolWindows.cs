using System.Runtime.ExceptionServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Tandem.App.Views.Dialogs;

namespace Tandem.App.Services;

/// <summary>
/// Shows the tool windows: each model gets the window <see cref="Register{TViewModel}"/> made for its type (the
/// type itself first, then its base classes). Non-modal windows are independent (not owned, so each comes forward on
/// its own), placed over <see cref="Owner"/> (the main window) when it is shown; modal ones belong to the window they
/// are shown for. Every window closes when its model raises <see cref="IDialogModel{TResult}.CloseRequested"/>; a
/// model that is an <see cref="ICloseGuard"/> is asked before any other close. A closed window's model is disposed.
/// UI thread only.
/// </summary>
/// <remarks>
/// The table is filled by the constructor: <see cref="ConfirmModel"/> here (one shared confirmation window), the rest
/// by the partial methods of the tools' and the console's own files. An unexpected exception of a model's
/// <see cref="ICloseGuard.CanCloseAsync"/> (on a window's own close) or of its disposal is rethrown on the UI thread,
/// where the app reports it; the window then stays open.
/// </remarks>
public sealed partial class ToolWindows : IToolWindows
{
    /// <summary>How far each non-modal window opens from the main window's corner.</summary>
    private const int Offset = 40;

    private readonly Dictionary<Type, Func<object, Window>> _factories = [];
    private readonly List<Entry> _open = [];

    public ToolWindows()
    {
        Register<ConfirmModel>(model => new ConfirmDialog(model));
        AddRename();
        AddChecksums();
        AddCompare();
        AddSync();
        AddConsole();
    }

    /// <summary>The main window; set by the app once it exists.</summary>
    public Window? Owner { get; set; }

    /// <summary>The windows open now, oldest first.</summary>
    public IReadOnlyList<Window> Open => _open.Select(e => e.Window).ToArray();

    public bool HasOpenWindows => _open.Count > 0;

    public event EventHandler? OpenWindowsChanged;

    public T? LastOpen<T>() where T : class => _open.FindLast(entry => entry.Model is T)?.Model as T;

    /// <summary>Models of type <typeparamref name="TViewModel"/> are shown in the window <paramref name="create"/>
    /// makes; registering a type again replaces its window. For the partial methods below and for tests.</summary>
    public void Register<TViewModel>(Func<TViewModel, Window> create)
    {
        ArgumentNullException.ThrowIfNull(create);
        _factories[typeof(TViewModel)] = model => create((TViewModel)model);
    }

    // Each tool's file registers its windows; called from the constructor.
    partial void AddRename();
    partial void AddChecksums();
    partial void AddCompare();
    partial void AddSync();
    partial void AddConsole();

    public void Show(object viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        if (_open.Find(e => ReferenceEquals(e.Model, viewModel)) is { } open)
        {
            Activate(open.Window);
            return;
        }
        var entry = Attach(viewModel);
        if (viewModel is IDialogModel dialog) CloseOnRequest(entry, dialog);
        var window = entry.Window;
        if (Owner is { IsVisible: true } owner)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            var step = Offset * _open.Count;
            window.Position = owner.Position + new PixelPoint(step, step);
        }
        else
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        window.Show();
    }

    public async Task<TResult?> ShowDialogAsync<TResult>(IDialogModel<TResult> viewModel, Window? owner = null,
        object? ownerModel = null)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        if (_open.Find(e => ReferenceEquals(e.Model, viewModel)) is { } open)
        {
            Activate(open.Window);
            return await open.Done.Task is TResult answered ? answered : default;
        }
        var factory = FactoryFor(viewModel);
        var modelWindow = ownerModel is null ? null : _open.Find(e => ReferenceEquals(e.Model, ownerModel))?.Window;
        if ((owner ?? modelWindow ?? Owner) is not { IsVisible: true } shownOwner)
        {
            await DisposeAsync(viewModel);
            return default;
        }

        var entry = Attach(viewModel, factory);
        var window = entry.Window;
        void OnCloseRequested(object? sender, TResult? answer)
        {
            entry.Result = answer;
            entry.CloseApproved = true; // the model's own answer needs no question
            window.Close();
        }

        viewModel.CloseRequested += OnCloseRequested;
        window.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Handled || e.Key != Key.Escape || e.KeyModifiers != KeyModifiers.None) return;
            e.Handled = true;
            window.Close();
        }, RoutingStrategies.Bubble);
        window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        try
        {
            await window.ShowDialog(shownOwner);
        }
        finally
        {
            viewModel.CloseRequested -= OnCloseRequested;
        }
        return entry.Result is TResult result ? result : default;
    }

    public async Task<bool> CloseAllAsync()
    {
        foreach (var entry in _open.ToArray())
        {
            if (!_open.Contains(entry)) continue; // closed meanwhile, with an earlier one
            if (!entry.CloseApproved && entry.Model is ICloseGuard guard)
            {
                if (entry.Asking) return false; // its own question is open: the user has not decided yet
                entry.Asking = true;
                bool canClose;
                try
                {
                    canClose = await guard.CanCloseAsync();
                }
                finally
                {
                    entry.Asking = false;
                }
                if (!canClose) return false;
                entry.CloseApproved = true;
            }
            entry.Window.Close();
        }
        return true;
    }

    /// <summary>One open window and its model. <see cref="CloseApproved"/>: the window may close without asking
    /// the model's guard (it agreed, or the model asked to close). <see cref="Result"/>: the model's answer, if it
    /// gave one; <see cref="Done"/> completes with it once the window has closed.</summary>
    private sealed class Entry(object model, Window window)
    {
        public object Model { get; } = model;
        public Window Window { get; } = window;
        public bool CloseApproved { get; set; }
        public bool Asking { get; set; }
        public object? Result { get; set; }
        public TaskCompletionSource<object?> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private Func<object, Window> FactoryFor(object viewModel)
    {
        for (var type = viewModel.GetType(); type is not null; type = type.BaseType)
            if (_factories.TryGetValue(type, out var factory))
                return factory;
        throw new InvalidOperationException($"No window is registered for {viewModel.GetType().Name}.");
    }

    /// <summary>Makes the model's window and keeps it among the open ones: its close asks the model's guard, and
    /// once closed it leaves the list and the model is disposed.</summary>
    private Entry Attach(object viewModel, Func<object, Window>? factory = null)
    {
        var window = (factory ?? FactoryFor(viewModel))(viewModel);
        var entry = new Entry(viewModel, window);
        _open.Add(entry);
        window.Closing += (_, e) => OnClosing(entry, e);
        window.Closed += (_, _) =>
        {
            _open.Remove(entry);
            entry.Done.TrySetResult(entry.Result);
            _ = DisposeAsync(viewModel);
            OpenWindowsChanged?.Invoke(this, EventArgs.Empty);
        };
        OpenWindowsChanged?.Invoke(this, EventArgs.Empty);
        return entry;
    }

    private void OnClosing(Entry entry, WindowClosingEventArgs e)
    {
        if (entry.CloseApproved || entry.Model is not ICloseGuard guard) return;
        e.Cancel = true;
        if (!entry.Asking) _ = AskThenCloseAsync(entry, guard);
    }

    private static async Task AskThenCloseAsync(Entry entry, ICloseGuard guard)
    {
        entry.Asking = true;
        try
        {
            if (!await guard.CanCloseAsync()) return;
        }
        catch (Exception e)
        {
            Rethrow(e);
            return;
        }
        finally
        {
            entry.Asking = false;
        }
        entry.CloseApproved = true;
        entry.Window.Close();
    }

    /// <summary>A model shown without <see cref="ShowDialogAsync{TResult}"/> that asks to close: its window closes
    /// (its answer goes to a later <see cref="ShowDialogAsync{TResult}"/> of the same model, if any).</summary>
    private static void CloseOnRequest(Entry entry, IDialogModel model)
    {
        var unsubscribe = model.SubscribeToClose(answer =>
        {
            entry.Result = answer;
            entry.CloseApproved = true;
            entry.Window.Close();
        });
        entry.Window.Closed += (_, _) => unsubscribe();
    }

    private static async Task DisposeAsync(object model)
    {
        try
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
        catch (Exception e)
        {
            Rethrow(e);
        }
    }

    private static void Rethrow(Exception e) => Dispatcher.UIThread.Post(() => ExceptionDispatchInfo.Throw(e));

    private static void Activate(Window window)
    {
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }
}
