using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DockerManager.App.Services;

namespace DockerManager.App.ViewModels;

public partial class LogViewerViewModel : ObservableObject
{
    private readonly IDockerService _dockerService;
    private readonly string _containerName;
    private readonly List<string> _initialLogs;
    private CancellationTokenSource? _logCts;

    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StreamingStatusText))]
    private bool _isStreaming;

    public string StreamingStatusText => LocalizationService.Instance.Get(IsStreaming ? "LogViewer_StatusStreaming" : "LogViewer_StatusStopped");

    public event Action<string>? OnLineAppended;

    public LogViewerViewModel(string containerName, IDockerService dockerService, IEnumerable<string>? initialLogs = null)
    {
        _containerName = containerName;
        _dockerService = dockerService;
        _initialLogs = initialLogs != null ? new List<string>(initialLogs) : new List<string>();
        _title = $"Logs: {_containerName}";
    }

    public async Task StartStreamingAsync()
    {
        StopStreaming();
        var cts = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _logCts, cts);
        if (previous != null)
        {
            try { previous.Cancel(); } catch (ObjectDisposedException) { }
            try { previous.Dispose(); } catch (ObjectDisposedException) { }
        }

        var loc = LocalizationService.Instance;
        var token = cts.Token;
        IsStreaming = true;

        OnLineAppended?.Invoke(loc.Get("LogViewer_ConsoleBanner", _containerName));

        if (_initialLogs.Count > 0)
        {
            OnLineAppended?.Invoke(loc.Get("LogViewer_RecentActivityHeader"));
            foreach (var log in _initialLogs)
            {
                OnLineAppended?.Invoke(log);
            }
            OnLineAppended?.Invoke("==============================================");
        }

        try
        {
            await Task.Run(async () =>
            {
                await _dockerService.StreamLogsAsync(_containerName, line =>
                {
                    OnLineAppended?.Invoke(line);
                }, token);
            }, token);
        }
        catch (OperationCanceledException)
        {
            OnLineAppended?.Invoke(loc.Get("LogViewer_StoppedByUser"));
        }
        catch (Exception ex)
        {
            OnLineAppended?.Invoke(loc.Get("LogViewer_FetchError", ex.Message));
        }
        finally
        {
            if (Interlocked.CompareExchange(ref _logCts, null, cts) == cts)
            {
                try { cts.Dispose(); } catch (ObjectDisposedException) { }
                IsStreaming = false;
            }
        }
    }

    [RelayCommand]
    public void StopStreaming()
    {
        var cts = Interlocked.Exchange(ref _logCts, null);
        if (cts != null)
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
            try { cts.Dispose(); } catch (ObjectDisposedException) { }
        }
        IsStreaming = false;
    }
}
