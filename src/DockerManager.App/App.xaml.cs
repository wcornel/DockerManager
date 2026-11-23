using System.Threading;
using System.Windows;
using System.Windows.Threading;
using DockerManager.App.Services;
using DockerManager.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace DockerManager.App;

public partial class App : System.Windows.Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    private Mutex? _instanceMutex;
    private bool _ownsMutex;
    private EventWaitHandle? _restoreEvent;
    private int _handlingUnhandledException;
    private const string MutexName = "DockerManager_SingleInstance_Mutex";
    private const string EventName = "DockerManager_RestoreEvent";

    protected override void OnStartup(StartupEventArgs e)
    {
        _instanceMutex = new Mutex(true, MutexName, out _ownsMutex);

        if (!_ownsMutex)
        {
            // Another instance is already running! Signal it to restore/show its window and exit immediately.
            try
            {
                if (EventWaitHandle.TryOpenExisting(EventName, out var existingEvent))
                {
                    using (existingEvent)
                    {
                        existingEvent.Set();
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning(nameof(App), "Failed to signal existing instance to restore", ex);
            }

            try { _instanceMutex.Dispose(); } catch { }
            _instanceMutex = null;

            Shutdown();
            return;
        }

        base.OnStartup(e);

        Services = ConfigureServices();

        try
        {
            var settings = Services.GetRequiredService<ISettingsService>();
            LocalizationService.Instance.SetLanguage(settings.Settings.Language);
        }
        catch (Exception ex)
        {
            AppLogger.LogWarning(nameof(App), "Failed to load initial language setting", ex);
        }

        // Start listening for restore signals from any second launch attempts
        try
        {
            _restoreEvent = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
            var listenerThread = new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        _restoreEvent.WaitOne();
                        Dispatcher.Invoke(() =>
                        {
                            if (MainWindow is MainWindow mw)
                            {
                                mw.RestoreWindow();
                            }
                        });
                    }
                    catch (ObjectDisposedException) { break; }
                    catch (Exception ex)
                    {
                        AppLogger.LogWarning(nameof(App), "Error in single-instance restore listener", ex);
                    }
                }
            })
            {
                IsBackground = true
            };
            listenerThread.Start();
        }
        catch (Exception ex)
        {
            AppLogger.LogWarning(nameof(App), "Failed to start single-instance restore listener", ex);
        }

        // Register resilient clipboard handlers on TextBox controls to prevent clipboard lock crashes
        Helpers.SafeClipboardHelper.RegisterGlobalTextBoxClipboardHandlers();

        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

        var mainWindow = Services.GetRequiredService<MainWindow>();
        MainWindow = mainWindow;
        mainWindow.Show();
    }

    private static IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        services.AddSingleton<ICredentialService>(_ => new CredentialService());
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IDialogService>(_ => WpfDialogService.Instance);
        services.AddSingleton<ICloudflareService>(sp => new CloudflareService(
            sp.GetRequiredService<ISettingsService>(),
            sp.GetRequiredService<ICredentialService>()));
        services.AddSingleton<IGitHubProfileService, GitHubProfileService>();
        services.AddSingleton<IDockerService, DockerService>();
        services.AddSingleton<IGitHubAuthService, GitHubAuthService>();
        services.AddSingleton<IBackupService, BackupService>();
        services.AddSingleton<IDotnetAppScannerService, DotnetAppScannerService>();
        services.AddSingleton<IPythonAppScannerService, PythonAppScannerService>();
        services.AddSingleton<IDotnetPublisherService, DotnetPublisherService>();
        services.AddSingleton<IAppUpdateService, AppUpdateService>();
        services.AddSingleton<IDockerComposeImporterService, DockerComposeImporterService>();
        services.AddSingleton<IDockerComposeExporterService, DockerComposeExporterService>();

        services.AddTransient<MainViewModel>();
        services.AddTransient<MainWindow>();

        return services.BuildServiceProvider();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _restoreEvent?.Dispose();
        }
        catch (Exception ex)
        {
            AppLogger.LogWarning(nameof(App), "Error disposing single-instance restore event on exit", ex);
        }
        _restoreEvent = null;

        if (_instanceMutex != null)
        {
            try
            {
                if (_ownsMutex)
                {
                    _instanceMutex.ReleaseMutex();
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning(nameof(App), "Error releasing single-instance mutex on exit", ex);
            }

            try
            {
                _instanceMutex.Dispose();
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning(nameof(App), "Error disposing single-instance mutex on exit", ex);
            }
            _instanceMutex = null;
        }

        try
        {
            if (Services is IDisposable disposableServices)
            {
                disposableServices.Dispose();
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogWarning(nameof(App), "Error disposing service provider on exit", ex);
        }

        base.OnExit(e);
    }

    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Suppress transient clipboard lock exceptions (e.g. CLIPBRD_E_CANT_OPEN / 0x800401D0)
        if (e.Exception is System.Runtime.InteropServices.COMException comEx && (uint)comEx.ErrorCode == 0x800401D0)
        {
            e.Handled = true;
            return;
        }

        if (e.Exception is System.Runtime.InteropServices.ExternalException extEx &&
            (extEx.ErrorCode == unchecked((int)0x800401D0) || 
             (extEx.Message?.Contains("klembord", StringComparison.OrdinalIgnoreCase) ?? false) || 
             (extEx.Message?.Contains("clipboard", StringComparison.OrdinalIgnoreCase) ?? false)))
        {
            e.Handled = true;
            return;
        }

        if (e.Exception.InnerException is System.Runtime.InteropServices.COMException innerCom && (uint)innerCom.ErrorCode == 0x800401D0)
        {
            e.Handled = true;
            return;
        }

        if (e.Exception is OutOfMemoryException or StackOverflowException)
        {
            AppLogger.LogError("DispatcherUnhandledException (Fatal)", e.Exception);
            return;
        }

        if (Interlocked.CompareExchange(ref _handlingUnhandledException, 1, 0) != 0)
        {
            AppLogger.LogError("DispatcherUnhandledException (Nested)", e.Exception);
            return;
        }

        try
        {
            e.Handled = true;
            AppLogger.LogError("DispatcherUnhandledException", e.Exception);
            var message = !string.IsNullOrWhiteSpace(e.Exception.Message)
                ? e.Exception.Message
                : $"{e.Exception.GetType().Name} ({e.Exception.HResult:X8})";

            var loc = LocalizationService.Instance;
            WpfDialogService.Instance.ShowWarning(
                loc.Get("App_UnexpectedUiError", message),
                loc.Get("App_NoticeTitle"));
        }
        finally
        {
            Interlocked.Exchange(ref _handlingUnhandledException, 0);
        }
    }

    private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            AppLogger.LogError("CurrentDomain_UnhandledException", ex);
            if (!e.IsTerminating && Dispatcher != null && !Dispatcher.HasShutdownStarted)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    var loc = LocalizationService.Instance;
                    WpfDialogService.Instance.ShowWarning(
                        loc.Get("App_UnexpectedUiError", ex.Message),
                        loc.Get("App_NoticeTitle"));
                });
            }
        }
    }

    private void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        AppLogger.LogError("TaskScheduler_UnobservedTaskException", e.Exception);
        e.SetObserved();
    }
}
