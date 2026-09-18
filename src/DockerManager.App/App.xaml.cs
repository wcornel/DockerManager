using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace DockerManager.App;

public partial class App : System.Windows.Application
{
    private Mutex? _instanceMutex;
    private EventWaitHandle? _restoreEvent;
    private const string MutexName = "DockerManager_SingleInstance_Mutex";
    private const string EventName = "DockerManager_RestoreEvent";

    protected override void OnStartup(StartupEventArgs e)
    {
        Velopack.VelopackApp.Build().Run();

        _instanceMutex = new Mutex(true, MutexName, out bool isOnlyInstance);

        if (!isOnlyInstance)
        {
            // Another instance is already running! Signal it to restore/show its window and exit immediately.
            try
            {
                if (EventWaitHandle.TryOpenExisting(EventName, out var existingEvent))
                {
                    existingEvent.Set();
                }
            }
            catch { }

            Shutdown();
            return;
        }

        base.OnStartup(e);

        try
        {
            var creds = new Services.CredentialService();
            var settings = new Services.SettingsService(creds);
            Services.LocalizationService.Instance.SetLanguage(settings.Settings.Language);
        }
        catch { }

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
                    catch { }
                }
            })
            {
                IsBackground = true
            };
            listenerThread.Start();
        }
        catch { }

        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _restoreEvent?.Dispose();
            _instanceMutex?.ReleaseMutex();
            _instanceMutex?.Dispose();
        }
        catch { }

        base.OnExit(e);
    }

    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        MessageBox.Show($"Er is een onverwachte fout opgetreden in de interface:\n\n{e.Exception.Message}",
            "DockerManager — Melding", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            MessageBox.Show($"Kritieke fout opgetreden:\n\n{ex.Message}",
                "DockerManager — Fout", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        e.SetObserved();
    }
}
