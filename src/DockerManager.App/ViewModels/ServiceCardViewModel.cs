using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DockerManager.App.Models;
using DockerManager.App.Services;

namespace DockerManager.App.ViewModels;

public partial class ServiceCardViewModel : ObservableObject
{
    private readonly IDockerService _dockerService;
    private readonly ISettingsService _settingsService;
    private readonly string _profileName;

    [ObservableProperty]
    private ServiceDefinition _service;

    [ObservableProperty]
    private ContainerRuntimeInfo _runtimeInfo = new();

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private string _resourceUsageText = string.Empty;

    [ObservableProperty]
    private bool _hasResourceStats;

    [ObservableProperty]
    private bool _hasUpdateAvailable;

    [ObservableProperty]
    private string _updateFeedbackBackground = "Transparent";

    [ObservableProperty]
    private string _updateFeedbackForeground = "#6366F1";

    public string StatusText
    {
        get
        {
            var loc = LocalizationService.Instance;
            return RuntimeInfo.State switch
            {
                ContainerState.Running => loc.Get("Card_Status_Running"),
                ContainerState.Created => loc.Get("Card_Status_Created"),
                ContainerState.Paused => loc.Get("Card_Status_Paused"),
                ContainerState.Restarting => loc.Get("Card_Status_Restarting"),
                ContainerState.Updating => loc.Get("Card_Status_Updating"),
                ContainerState.Exited => loc.Get("Card_Status_Stopped"),
                ContainerState.NotCreated => loc.Get("Card_Status_NotStarted"),
                ContainerState.Dead => loc.Get("Card_Status_Dead"),
                ContainerState.Error => loc.Get("Card_Status_Error"),
                _ => loc.Get("Card_Status_Unknown")
            };
        }
    }

    public string LastUpdatedText => Service.LastUpdatedUtc.HasValue 
        ? FormatLastUpdated(Service.LastUpdatedUtc.Value) 
        : LocalizationService.Instance.Get("Card_NotYetUpdated");

    private static string FormatLastUpdated(DateTime utc)
    {
        var local = utc.ToLocalTime();
        var loc = LocalizationService.Instance;
        if (local.Date == DateTime.Today)
            return $"{loc.Get("Time_Today")} {local:HH:mm}";
        if (local.Date == DateTime.Today.AddDays(-1))
            return $"{loc.Get("Time_Yesterday")} {local:HH:mm}";
        return local.ToString("dd-MM-yyyy HH:mm");
    }

    public List<string> ActivityLogs { get; } = new();

    public string ContainerName => string.IsNullOrWhiteSpace(Service.ContainerName) ? Service.Id : Service.ContainerName;
    public string PortsSummary => Service.Ports.Count > 0 
        ? string.Join(", ", Service.Ports.Select(p => $"{p.HostPort}→{p.ContainerPort}"))
        : LocalizationService.Instance.Get("Card_NoPorts");

    public string VolumesSummary => Service.Volumes.Count > 0
        ? string.Join(", ", Service.Volumes.Select(v => $"{v.HostPath}→{v.ContainerPath}"))
        : LocalizationService.Instance.Get("Card_NoVolumes");

    public bool HasWebPort => Service.Ports.Any(p => p.HostPort > 0);

    public string WebUrl
    {
        get
        {
            var firstPort = Service.Ports.FirstOrDefault(p => p.HostPort > 0);
            if (firstPort == null) return string.Empty;

            var host = "localhost";
            var settings = _settingsService.Settings;
            if (settings.DockerHostType.Equals("Tcp", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(settings.DockerTcpUrl))
            {
                try
                {
                    var uri = new Uri(settings.DockerTcpUrl.Replace("tcp://", "http://"));
                    host = uri.Host;
                }
                catch { }
            }

            var scheme = firstPort.HostPort == 443 ? "https" : "http";
            var portPart = (firstPort.HostPort == 80 && scheme == "http") || (firstPort.HostPort == 443 && scheme == "https")
                ? ""
                : $":{firstPort.HostPort}";

            return $"{scheme}://{host}{portPart}";
        }
    }

    public bool HasCloudflareUrl => !string.IsNullOrWhiteSpace(CloudflareUrl);

    public string CloudflareUrl
    {
        get
        {
            string? raw = !string.IsNullOrWhiteSpace(Service.CloudflareHostname)
                ? Service.CloudflareHostname
                : (Service.Labels != null && Service.Labels.TryGetValue("cloudflare.tunnel.hostname", out var host) ? host : null);

            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

            var clean = raw.Trim();
            if (clean.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                clean = clean[7..];
            }
            else if (clean.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                clean = clean[8..];
            }

            clean = clean.Trim('/');

            // If only a subdomain was stored (no dot), append domain from settings if available
            var domain = _settingsService?.Settings?.CloudflareDomain?.Trim().TrimStart('.').ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(domain) && !clean.Contains('.'))
            {
                clean = $"{clean}.{domain}";
            }

            return $"https://{clean}";
        }
    }

    [RelayCommand]
    private void OpenCloudflareUrl()
    {
        if (HasCloudflareUrl)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = CloudflareUrl,
                    UseShellExecute = true
                });
                LogActivity($"Publieke Cloudflare HTTPS URL geopend: {CloudflareUrl}");
            }
            catch (Exception ex)
            {
                LogActivity($"Kon Cloudflare URL niet openen: {ex.Message}");
            }
        }
    }

    public bool IsRunning => RuntimeInfo.State == ContainerState.Running;
    public bool IsStopped => RuntimeInfo.State == ContainerState.Exited || RuntimeInfo.State == ContainerState.NotCreated || RuntimeInfo.State == ContainerState.Created;

    public event Action<ServiceCardViewModel>? RequestOpenLogs;
    public event Action<ServiceCardViewModel>? RequestEdit;
    public event Action<ServiceCardViewModel>? RequestDeleteFromProfile;
    public event Action? RequestSaveProfile;

    public ServiceCardViewModel(
        ServiceDefinition service,
        string profileName,
        IDockerService dockerService,
        ISettingsService settingsService)
    {
        _service = service;
        _profileName = profileName;
        _dockerService = dockerService;
        _settingsService = settingsService;

        RuntimeInfo = new ContainerRuntimeInfo
        {
            ContainerName = ContainerName,
            State = ContainerState.Unknown,
            StatusText = LocalizationService.Instance.Get("General_Loading")
        };

        LocalizationService.Instance.LanguageChanged += OnLanguageChanged;
    }

    private void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(LastUpdatedText));
        OnPropertyChanged(nameof(PortsSummary));
        OnPropertyChanged(nameof(VolumesSummary));
    }

    public void UpdateService(ServiceDefinition updated)
    {
        Service = updated;
        OnPropertyChanged(nameof(ContainerName));
        OnPropertyChanged(nameof(PortsSummary));
        OnPropertyChanged(nameof(VolumesSummary));
        OnPropertyChanged(nameof(HasWebPort));
        OnPropertyChanged(nameof(WebUrl));
        OnPropertyChanged(nameof(HasCloudflareUrl));
        OnPropertyChanged(nameof(CloudflareUrl));
        OnPropertyChanged(nameof(LastUpdatedText));
    }

    private void LogActivity(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        ActivityLogs.Add(line);
        if (ActivityLogs.Count > 500)
        {
            ActivityLogs.RemoveAt(0);
        }
        StatusMessage = message;
    }

    public async Task RefreshStatusAsync(CancellationToken ct = default)
    {
        if (IsBusy) return;
        try
        {
            var info = await _dockerService.GetContainerInfoAsync(ContainerName, ct);
            RuntimeInfo = info;
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(IsRunning));
            OnPropertyChanged(nameof(IsStopped));

            if (info.State == ContainerState.Running)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var stats = await _dockerService.GetContainerStatsSummaryAsync(ContainerName, CancellationToken.None);
                        if (!string.IsNullOrWhiteSpace(stats))
                        {
                            ResourceUsageText = stats;
                            HasResourceStats = true;
                        }
                    }
                    catch { }
                });
            }
            else
            {
                ResourceUsageText = string.Empty;
                HasResourceStats = false;
            }
        }
        catch (Exception ex)
        {
            RuntimeInfo = new ContainerRuntimeInfo
            {
                ContainerName = ContainerName,
                State = ContainerState.Error,
                StatusText = "Fout bij ophalen",
                ErrorMessage = ex.Message
            };
            HasResourceStats = false;
        }
    }

    [RelayCommand]
    private void OpenTerminal()
    {
        try
        {
            var hostArg = "";
            var settings = _settingsService.Settings;
            if (settings.DockerHostType.Equals("Tcp", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(settings.DockerTcpUrl))
            {
                hostArg = $"-H {settings.DockerTcpUrl} ";
            }

            var script = $"Write-Host '== Verbinding maken met {ContainerName} ==' -ForegroundColor Cyan; docker {hostArg}exec -it {ContainerName} sh";

            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoExit -Command \"{script}\"",
                UseShellExecute = true
            });

            LogActivity("Terminal sessie gestart.");
        }
        catch (Exception ex)
        {
            LogActivity($"Kon terminal niet starten: {ex.Message}");
        }
    }

    [RelayCommand]
    private void OpenInBrowser()
    {
        var url = WebUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            StatusMessage = "Geen poort geconfigureerd.";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
            LogActivity($"Browser geopend: {url}");
        }
        catch (Exception ex)
        {
            LogActivity($"Kon browser niet openen: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        LogActivity("Starten van container initialiseren...");
        try
        {
            var progress = new Progress<string>(msg => LogActivity(msg));
            await _dockerService.StartContainerAsync(Service, _profileName, progress);
            await RefreshStatusAsync();
            LogActivity("Container succesvol gestart.");
        }
        catch (Exception ex)
        {
            LogActivity($"Fout: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        LogActivity("Container stoppen...");
        try
        {
            await _dockerService.StopContainerAsync(ContainerName);
            await RefreshStatusAsync();
            LogActivity("Container gestopt.");
        }
        catch (Exception ex)
        {
            LogActivity($"Fout: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RestartAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        LogActivity("Container herstarten...");
        try
        {
            var progress = new Progress<string>(msg => LogActivity(msg));
            await _dockerService.RestartContainerAsync(Service, _profileName, progress);
            await RefreshStatusAsync();
            LogActivity("Container herstart.");
        }
        catch (Exception ex)
        {
            LogActivity($"Fout: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SafeUpdateAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        LogActivity("Update gestart: controleren op nieuwste image & container hercreëren...");
        try
        {
            var progress = new Progress<string>(msg => LogActivity(msg));
            var updateResult = await _dockerService.SafeUpdateContainerAsync(Service, _profileName, progress);
            Service.LastUpdatedUtc = DateTime.UtcNow;
            HasUpdateAvailable = false;
            OnPropertyChanged(nameof(LastUpdatedText));
            await RefreshStatusAsync();
            RequestSaveProfile?.Invoke();

            if (updateResult.WasUpdated)
            {
                StatusMessage = "✨ Update geïnstalleerd!";
                UpdateFeedbackBackground = "#DCFCE7"; // Zacht fris groen
                UpdateFeedbackForeground = "#15803D"; // Donkergroen
                LogActivity("✨ Update succesvol geïnstalleerd! (Nieuwe image-versie gedownload)");
            }
            else
            {
                StatusMessage = "✅ Reeds up-to-date";
                UpdateFeedbackBackground = "#F1F5F9"; // Zacht neutraal grijs
                UpdateFeedbackForeground = "#475569";
                LogActivity("✅ Container draait al op de nieuwste image. Geen herstart nodig, container bleef actief.");
            }

            _ = Task.Run(async () =>
            {
                await Task.Delay(5000);
                UpdateFeedbackBackground = "Transparent";
                UpdateFeedbackForeground = "#6366F1";
                if (StatusMessage.Contains("Update geïnstalleerd") || StatusMessage.Contains("Reeds up-to-date"))
                {
                    StatusMessage = string.Empty;
                }
            });
        }
        catch (Exception ex)
        {
            StatusMessage = "❌ Update mislukt";
            UpdateFeedbackBackground = "#FEE2E2"; // Zacht lichtrood
            UpdateFeedbackForeground = "#B91C1C"; // Donkerrood
            LogActivity($"Fout bij update: {ex.Message}");

            _ = Task.Run(async () =>
            {
                await Task.Delay(6000);
                UpdateFeedbackBackground = "Transparent";
                UpdateFeedbackForeground = "#6366F1";
                if (StatusMessage.Contains("Update mislukt"))
                {
                    StatusMessage = string.Empty;
                }
            });
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RemoveAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        LogActivity("Container verwijderen (volumes blijven behouden)...");
        try
        {
            await _dockerService.RemoveContainerAsync(ContainerName, removeVolumes: false);
            await RefreshStatusAsync();
            LogActivity("Container verwijderd.");
        }
        catch (Exception ex)
        {
            LogActivity($"Fout: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenLogs()
    {
        RequestOpenLogs?.Invoke(this);
    }

    [RelayCommand]
    private void Edit()
    {
        RequestEdit?.Invoke(this);
    }

    [RelayCommand]
    private void DeleteFromProfile()
    {
        RequestDeleteFromProfile?.Invoke(this);
    }
}
