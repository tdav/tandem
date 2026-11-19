using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Tandem.App.Resources;
using Tandem.App.Services;
using Tandem.App.ViewModels;
using Tandem.App.Views;

namespace Tandem.App;

public partial class App : Application
{
    private ErrorLog? _log;
    private AppServices? _services;
    private MainViewModel? _main;
    private MainWindow? _window;
    private bool _showingError;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Headless tests run without a lifetime and build their own windows.
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            _log = new ErrorLog(Path.Combine(home, "Library", "Logs", "Tandem", "tandem.log"));
            InstallErrorHandlers();
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            desktop.Exit += (_, _) =>
            {
                _main?.Dispose();
                _services?.Dispose();
            };
            _ = StartAsync(desktop, home);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Loads the saved state off the UI thread, then shows the window on the loaded workspace.</summary>
    private async Task StartAsync(IClassicDesktopStyleApplicationLifetime desktop, string home)
    {
        try
        {
            if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("Tandem runs on macOS only.");
            _services = Composition.CreatePlatformServices();
            var stateDirectory = Path.Combine(home, "Library", "Application Support", "Tandem");
            _main = await Composition.StartAsync(_services, stateDirectory, home);
            _main.CommandFailed += (_, e) => ReportUnexpected(e, "command");
            _window = new MainWindow(_main);
            desktop.MainWindow = _window;
            _window.Show();
        }
        catch (Exception e)
        {
            _log?.Write(e, "start");
            await MessageDialog.ShowAsync(null, Loc.Get("Error_Title"),
                Loc.Format("Error_StartFailed", e.Message, _log?.File ?? ""));
            desktop.Shutdown(1);
        }
    }

    /// <summary>One handler for everything unexpected: exceptions on the UI thread, faulted tasks nobody observed,
    /// and failing commands. Each is logged and shown; the app keeps running.</summary>
    private void InstallErrorHandlers()
    {
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            e.Handled = true;
            ReportUnexpected(e.Exception, "ui");
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            e.SetObserved();
            ReportUnexpected(e.Exception, "task");
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            _log?.Write(e.ExceptionObject as Exception ?? new InvalidOperationException(e.ExceptionObject?.ToString()),
                "fatal");
    }

    /// <summary>Any thread. Logs the error and shows it, one message box at a time.</summary>
    private void ReportUnexpected(Exception error, string context)
    {
        _log?.Write(error, context);
        try
        {
            Dispatcher.UIThread.Post(async () =>
            {
                if (_showingError) return;
                _showingError = true;
                try
                {
                    var message = error is AggregateException { InnerExceptions.Count: 1 } single
                        ? single.InnerExceptions[0].Message
                        : error.Message;
                    await MessageDialog.ShowAsync(_window, Loc.Get("Error_Title"),
                        Loc.Format("Error_Unexpected", message, _log?.File ?? ""));
                }
                catch (Exception e)
                {
                    _log?.Write(e, "error dialog");
                }
                finally
                {
                    _showingError = false;
                }
            });
        }
        catch (Exception e)
        {
            _log?.Write(e, "error dialog");
        }
    }
}
