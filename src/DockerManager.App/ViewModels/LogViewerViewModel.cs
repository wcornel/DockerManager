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
    private bool _isStreaming;

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
        _logCts = new CancellationTokenSource();
        IsStreaming = true;

        OnLineAppended?.Invoke($"--- Log Console voor container: {_containerName} ---");

        if (_initialLogs.Count > 0)
        {
            OnLineAppended?.Invoke("=== Recente Download & Lifecycle Activiteit ===");
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
                }, _logCts.Token);
            }, _logCts.Token);
        }
        catch (OperationCanceledException)
        {
            OnLineAppended?.Invoke("--- Log streaming gestopt door gebruiker ---");
        }
        catch (Exception ex)
        {
            OnLineAppended?.Invoke($"--- Fout bij ophalen van logs: {ex.Message} ---");
        }
        finally
        {
            IsStreaming = false;
        }
    }

    [RelayCommand]
    public void StopStreaming()
    {
        _logCts?.Cancel();
        _logCts?.Dispose();
        _logCts = null;
        IsStreaming = false;
    }
}
