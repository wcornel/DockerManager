using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DockerManager.App.Models;

namespace DockerManager.App.ViewModels;

public class KeyValueItem : ObservableObject
{
    private string _key = string.Empty;
    public string Key
    {
        get => _key;
        set => SetProperty(ref _key, value);
    }

    private string _value = string.Empty;
    public string Value
    {
        get => _value;
        set => SetProperty(ref _value, value);
    }

    public KeyValueItem() { }
    public KeyValueItem(string key, string value)
    {
        _key = key;
        _value = value;
    }
}

public class EditablePortItem : ObservableObject
{
    private string _hostIp = "0.0.0.0";
    public string HostIp
    {
        get => _hostIp;
        set => SetProperty(ref _hostIp, value);
    }

    private string _hostPortDisplay = "8080";
    public string HostPortDisplay
    {
        get => _hostPortDisplay;
        set
        {
            if (SetProperty(ref _hostPortDisplay, value))
            {
                ParseInput(value);
            }
        }
    }

    private int _hostPort = 8080;
    public int HostPort
    {
        get => _hostPort;
        set
        {
            if (SetProperty(ref _hostPort, value))
            {
                UpdateDisplay();
            }
        }
    }

    private int _containerPort = 8080;
    public int ContainerPort
    {
        get => _containerPort;
        set => SetProperty(ref _containerPort, value);
    }

    private string _protocol = "tcp";
    public string Protocol
    {
        get => _protocol;
        set => SetProperty(ref _protocol, value);
    }

    private void UpdateDisplay()
    {
        if (string.IsNullOrWhiteSpace(_hostIp) || _hostIp == "0.0.0.0")
        {
            _hostPortDisplay = _hostPort.ToString();
        }
        else
        {
            _hostPortDisplay = $"{_hostIp}:{_hostPort}";
        }
        OnPropertyChanged(nameof(HostPortDisplay));
    }

    private void ParseInput(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        value = value.Trim();
        if (value.Contains(':'))
        {
            var parts = value.Split(':');
            _hostIp = parts[0].Trim();
            if (int.TryParse(parts[1].Trim(), out var p))
            {
                _hostPort = p;
                OnPropertyChanged(nameof(HostPort));
            }
        }
        else if (int.TryParse(value, out var p))
        {
            _hostPort = p;
            _hostIp = "0.0.0.0";
            OnPropertyChanged(nameof(HostPort));
        }
        OnPropertyChanged(nameof(HostIp));
    }
}

public class EditableVolumeItem : ObservableObject
{
    private string _hostPath = string.Empty;
    public string HostPath
    {
        get => _hostPath;
        set => SetProperty(ref _hostPath, value);
    }

    private string _containerPath = string.Empty;
    public string ContainerPath
    {
        get => _containerPath;
        set => SetProperty(ref _containerPath, value);
    }

    private bool _isNamedVolume = true;
    public bool IsNamedVolume
    {
        get => _isNamedVolume;
        set => SetProperty(ref _isNamedVolume, value);
    }
}

public partial class EditServiceViewModel : ObservableObject
{
    [ObservableProperty]
    private string _id = string.Empty;

    [ObservableProperty]
    private string _displayName = string.Empty;

    [ObservableProperty]
    private string _containerName = string.Empty;

    [ObservableProperty]
    private string _image = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    private string _restartPolicy = "unless-stopped";

    [ObservableProperty]
    private string _workingDir = string.Empty;

    [ObservableProperty]
    private string _command = string.Empty;

    [ObservableProperty]
    private bool _autoStart;

    [ObservableProperty]
    private bool _requiresAuth = true;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    private string _inspectStatusMessage = string.Empty;

    [ObservableProperty]
    private bool _isInspecting;

    [ObservableProperty]
    private bool _isNew;

    [ObservableProperty]
    private bool _enableCloudflareTunnel;

    [ObservableProperty]
    private string _cloudflareSubdomain = string.Empty;

    [ObservableProperty]
    private string _cloudflareDomain = string.Empty;

    [ObservableProperty]
    private bool _hasCloudflareConfigured;

    [ObservableProperty]
    private int _selectedCloudflarePort = 80;

    [ObservableProperty]
    private string _cloudflareStatusMessage = string.Empty;

    public bool EnsureCloudflaredInProfile { get; private set; }

    public string FullCloudflareUrl =>
        !string.IsNullOrWhiteSpace(CloudflareSubdomain) && !string.IsNullOrWhiteSpace(CloudflareDomain)
            ? $"https://{CloudflareSubdomain.Trim().ToLowerInvariant()}.{CloudflareDomain.Trim().ToLowerInvariant()}"
            : string.Empty;

    private string? _registeredCloudflareHost;
    private readonly string? _originalCloudflareHostname;

    public Func<string, string, bool>? ConfirmPrompt { get; set; }

    partial void OnCloudflareSubdomainChanged(string value)
    {
        _registeredCloudflareHost = null;
        OnPropertyChanged(nameof(FullCloudflareUrl));
    }

    public ObservableCollection<EditablePortItem> Ports { get; } = new();
    public ObservableCollection<EditableVolumeItem> Volumes { get; } = new();
    public ObservableCollection<KeyValueItem> EnvironmentVariables { get; } = new();

    public List<string> AvailableRestartPolicies { get; } = new()
    {
        "unless-stopped",
        "always",
        "on-failure",
        "no"
    };

    public event Action? RequestClose;
    public bool IsSaved { get; private set; }

    private readonly Services.IDockerService? _dockerService;
    private readonly Services.ICloudflareService? _cloudflareService;
    private readonly Services.ISettingsService? _settingsService;

    public EditServiceViewModel(
        ServiceDefinition? existingService = null,
        Services.IDockerService? dockerService = null,
        Services.ICloudflareService? cloudflareService = null,
        Services.ISettingsService? settingsService = null)
    {
        _dockerService = dockerService;
        _cloudflareService = cloudflareService;
        _settingsService = settingsService;

        _cloudflareDomain = _settingsService?.Settings.CloudflareDomain ?? string.Empty;
        _hasCloudflareConfigured = _settingsService?.Settings.HasCloudflareConfigured ?? false;

        if (existingService != null)
        {
            _isNew = false;
            _id = existingService.Id;
            _displayName = existingService.DisplayName;
            _containerName = existingService.ContainerName;
            _image = existingService.Image;
            _description = existingService.Description;
            _restartPolicy = existingService.RestartPolicy;
            _workingDir = existingService.WorkingDir ?? string.Empty;
            _command = existingService.Command != null ? string.Join(" ", existingService.Command) : string.Empty;
            _autoStart = existingService.AutoStart;
            _requiresAuth = existingService.RequiresAuth;

            var existingHost = existingService.CloudflareHostname;
            if (string.IsNullOrWhiteSpace(existingHost) && existingService.Labels != null && existingService.Labels.TryGetValue("cloudflare.tunnel.hostname", out var lblHost))
            {
                existingHost = lblHost;
            }
            _originalCloudflareHostname = existingHost;

            if (!string.IsNullOrWhiteSpace(existingHost))
            {
                _enableCloudflareTunnel = true;
                _registeredCloudflareHost = existingHost;
                var hostOnly = existingHost.Replace("https://", "").Replace("http://", "").Trim('/');
                if (!string.IsNullOrWhiteSpace(_cloudflareDomain) && hostOnly.EndsWith(_cloudflareDomain, StringComparison.OrdinalIgnoreCase))
                {
                    var sub = hostOnly[..^Math.Min(hostOnly.Length, _cloudflareDomain.Length)].TrimEnd('.');
                    _cloudflareSubdomain = sub;
                }
                else
                {
                    _cloudflareSubdomain = hostOnly.Split('.')[0];
                }
            }
            else
            {
                _cloudflareSubdomain = (!string.IsNullOrWhiteSpace(_containerName) ? _containerName : _id).ToLowerInvariant();
            }

            if (existingService.Ports.Count > 0)
            {
                _selectedCloudflarePort = existingService.Ports[0].HostPort;
            }

            foreach (var p in existingService.Ports)
            {
                var hostIp = string.IsNullOrWhiteSpace(p.HostIp) ? "0.0.0.0" : p.HostIp;
                Ports.Add(new EditablePortItem
                {
                    HostIp = hostIp,
                    HostPort = p.HostPort,
                    ContainerPort = p.ContainerPort,
                    Protocol = p.Protocol,
                    HostPortDisplay = (hostIp == "0.0.0.0" || string.IsNullOrWhiteSpace(hostIp)) ? p.HostPort.ToString() : $"{hostIp}:{p.HostPort}"
                });
            }

            foreach (var v in existingService.Volumes)
            {
                Volumes.Add(new EditableVolumeItem
                {
                    HostPath = v.HostPath,
                    ContainerPath = v.ContainerPath,
                    IsNamedVolume = v.IsNamedVolume
                });
            }

            foreach (var kvp in existingService.Environment)
            {
                EnvironmentVariables.Add(new KeyValueItem(kvp.Key, kvp.Value));
            }
        }
        else
        {
            _isNew = true;
            _displayName = "Nieuwe Container";
            _id = "nieuwe-container";
            _containerName = "nieuwe-container";
            _image = "nginx:alpine";
            _restartPolicy = "unless-stopped";
            _requiresAuth = false;

            Ports.Add(new EditablePortItem { HostPort = 8080, ContainerPort = 8080, Protocol = "tcp" });
        }
    }

    [RelayCommand]
    private void AddPort()
    {
        Ports.Add(new EditablePortItem { HostPort = 8080, ContainerPort = 8080, Protocol = "tcp" });
    }

    [RelayCommand]
    private void RemovePort(EditablePortItem item)
    {
        Ports.Remove(item);
    }

    [RelayCommand]
    private void AddVolume()
    {
        Volumes.Add(new EditableVolumeItem { HostPath = "container_data", ContainerPath = "/app/data", IsNamedVolume = true });
    }

    [RelayCommand]
    private void RemoveVolume(EditableVolumeItem item)
    {
        Volumes.Remove(item);
    }

    [RelayCommand]
    private void AddEnvVar()
    {
        EnvironmentVariables.Add(new KeyValueItem("ENV_KEY", "value"));
    }

    [RelayCommand]
    private void RemoveEnvVar(KeyValueItem item)
    {
        EnvironmentVariables.Remove(item);
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(DisplayName))
        {
            ErrorMessage = "Voer een weergavenaam in.";
            return;
        }

        if (string.IsNullOrWhiteSpace(Image))
        {
            ErrorMessage = "Voer een Docker image in.";
            return;
        }

        if (string.IsNullOrWhiteSpace(ContainerName))
        {
            ContainerName = DisplayName.ToLowerInvariant().Replace(" ", "-");
        }

        if (string.IsNullOrWhiteSpace(Id))
        {
            Id = ContainerName;
        }

        // Register with Cloudflare if tunnel is enabled
        if (EnableCloudflareTunnel && HasCloudflareConfigured && _cloudflareService != null && !string.IsNullOrWhiteSpace(CloudflareSubdomain))
        {
            var targetPort = SelectedCloudflarePort > 0 ? SelectedCloudflarePort : (Ports.FirstOrDefault()?.HostPort ?? 80);
            try
            {
                var (ok, msg, fullHost) = await _cloudflareService.RegisterSubdomainAsync(CloudflareSubdomain.Trim(), targetPort);
                if (ok && !string.IsNullOrWhiteSpace(fullHost))
                {
                    EnsureCloudflaredInProfile = true;
                    _registeredCloudflareHost = fullHost;

                    // If hostname changed, optionally ask to remove the old one from Cloudflare
                    if (!string.IsNullOrWhiteSpace(_originalCloudflareHostname))
                    {
                        var cleanOriginal = _originalCloudflareHostname.Replace("https://", "").Replace("http://", "").Trim('/');
                        if (!cleanOriginal.Equals(fullHost, StringComparison.OrdinalIgnoreCase))
                        {
                            if (AskConfirmation(
                                $"De Cloudflare hostnaam is gewijzigd van '{cleanOriginal}' naar '{fullHost}'.\n\nWil je de oude Cloudflare verwijzing voor '{cleanOriginal}' direct verwijderen uit Cloudflare (DNS en tunnel)?",
                                "Oude Cloudflare Verwijzing Verwijderen"))
                            {
                                _ = await _cloudflareService.UnregisterSubdomainAsync(cleanOriginal);
                            }
                        }
                    }
                }
                else
                {
                    ErrorMessage = $"⚠️ Cloudflare fout: {msg}";
                    return;
                }
            }
            catch (Exception ex)
            {
                ErrorMessage = $"⚠️ Fout bij Cloudflare registratie: {ex.Message}";
                return;
            }
        }
        else if (!EnableCloudflareTunnel)
        {
            // If it was previously enabled with Cloudflare, ask the user if they also want to remove it from Cloudflare
            if (!string.IsNullOrWhiteSpace(_originalCloudflareHostname) && _cloudflareService != null)
            {
                var cleanOriginal = _originalCloudflareHostname.Replace("https://", "").Replace("http://", "").Trim('/');
                if (AskConfirmation(
                    $"Je hebt de Cloudflare koppeling uitgeschakeld voor deze container.\n\nWil je de Cloudflare verwijzing (DNS-record en tunnelkoppeling) voor '{cleanOriginal}' ook direct verwijderen uit Cloudflare?",
                    "Cloudflare Verwijzing Verwijderen"))
                {
                    try
                    {
                        var (delOk, delMsg) = await _cloudflareService.UnregisterSubdomainAsync(cleanOriginal);
                        if (!delOk)
                        {
                            CloudflareStatusMessage = $"⚠️ {delMsg}";
                        }
                    }
                    catch (Exception ex)
                    {
                        CloudflareStatusMessage = $"⚠️ Fout bij verwijderen uit Cloudflare: {ex.Message}";
                    }
                }
            }

            _registeredCloudflareHost = null;
        }

        IsSaved = true;
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private void Cancel()
    {
        IsSaved = false;
        RequestClose?.Invoke();
    }

    private bool AskConfirmation(string message, string title)
    {
        if (ConfirmPrompt != null)
        {
            return ConfirmPrompt(message, title);
        }

        try
        {
            return System.Windows.MessageBox.Show(
                message,
                title,
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question) == System.Windows.MessageBoxResult.Yes;
        }
        catch
        {
            return false;
        }
    }

    [RelayCommand]
    private async Task InspectAndDownloadImageAsync()
    {
        if (string.IsNullOrWhiteSpace(Image))
        {
            InspectStatusMessage = "⚠️ Vul eerst een Docker Image naam in.";
            return;
        }

        if (_dockerService == null)
        {
            InspectStatusMessage = "⚠️ Docker service is momenteel niet beschikbaar.";
            return;
        }

        IsInspecting = true;
        InspectStatusMessage = "⏳ Image downloaden en inspecteren...";

        try
        {
            var progress = new Progress<string>(msg => InspectStatusMessage = msg);
            var result = await _dockerService.InspectAndPullImageConfigAsync(Image.Trim(), RequiresAuth, progress);

            if (result.Success)
            {
                if (string.IsNullOrWhiteSpace(DisplayName) || DisplayName == "Nieuwe Container")
                {
                    DisplayName = result.SuggestedDisplayName ?? DisplayName;
                }
                if (string.IsNullOrWhiteSpace(ContainerName) || ContainerName == "nieuwe-container")
                {
                    ContainerName = result.SuggestedContainerName ?? ContainerName;
                }
                if (string.IsNullOrWhiteSpace(Id) || Id == "nieuwe-container")
                {
                    Id = result.SuggestedContainerName ?? Id;
                }

                if (!string.IsNullOrWhiteSpace(result.WorkingDir) && string.IsNullOrWhiteSpace(WorkingDir))
                {
                    WorkingDir = result.WorkingDir;
                }

                if (!string.IsNullOrWhiteSpace(result.Command) && string.IsNullOrWhiteSpace(Command))
                {
                    Command = result.Command;
                }

                // Add exposed ports
                foreach (var port in result.ExposedPorts)
                {
                    if (!Ports.Any(p => p.ContainerPort == port))
                    {
                        Ports.Add(new EditablePortItem
                        {
                            HostPort = port,
                            ContainerPort = port,
                            Protocol = "tcp"
                        });
                    }
                }

                // Add environment variables
                foreach (var kvp in result.EnvironmentVariables)
                {
                    if (!EnvironmentVariables.Any(e => e.Key.Equals(kvp.Key, StringComparison.OrdinalIgnoreCase)))
                    {
                        EnvironmentVariables.Add(new KeyValueItem(kvp.Key, kvp.Value));
                    }
                }

                InspectStatusMessage = $"✅ Image succesvol geconfigureerd! ({result.ExposedPorts.Count} poorten, {result.EnvironmentVariables.Count} variabelen gevonden)";
            }
            else
            {
                InspectStatusMessage = $"❌ {result.ErrorMessage}";
            }
        }
        catch (Exception ex)
        {
            InspectStatusMessage = $"❌ Fout: {ex.Message}";
        }
        finally
        {
            IsInspecting = false;
        }
    }

    public ServiceDefinition ToServiceDefinition()
    {
        var service = new ServiceDefinition
        {
            Id = Id,
            DisplayName = DisplayName,
            ContainerName = ContainerName,
            Image = Image,
            Description = Description,
            RestartPolicy = RestartPolicy,
            WorkingDir = string.IsNullOrWhiteSpace(WorkingDir) ? null : WorkingDir.Trim(),
            Command = string.IsNullOrWhiteSpace(Command) ? null : Command.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
            AutoStart = AutoStart,
            RequiresAuth = RequiresAuth
        };

        foreach (var p in Ports)
        {
            if (p.ContainerPort > 0)
            {
                service.Ports.Add(new PortMapping
                {
                    HostIp = string.IsNullOrWhiteSpace(p.HostIp) ? "0.0.0.0" : p.HostIp,
                    HostPort = p.HostPort > 0 ? p.HostPort : p.ContainerPort,
                    ContainerPort = p.ContainerPort,
                    Protocol = string.IsNullOrWhiteSpace(p.Protocol) ? "tcp" : p.Protocol
                });
            }
        }

        foreach (var v in Volumes)
        {
            if (!string.IsNullOrWhiteSpace(v.ContainerPath))
            {
                service.Volumes.Add(new VolumeMapping
                {
                    HostPath = v.HostPath,
                    ContainerPath = v.ContainerPath,
                    IsNamedVolume = v.IsNamedVolume
                });
            }
        }

        foreach (var env in EnvironmentVariables)
        {
            if (!string.IsNullOrWhiteSpace(env.Key))
            {
                service.Environment[env.Key] = env.Value;
            }
        }

        var cfHost = EnableCloudflareTunnel && !string.IsNullOrWhiteSpace(_registeredCloudflareHost)
            ? _registeredCloudflareHost
            : (EnableCloudflareTunnel && !string.IsNullOrWhiteSpace(FullCloudflareUrl) ? FullCloudflareUrl : null);

        if (!string.IsNullOrWhiteSpace(cfHost))
        {
            var clean = cfHost.Trim();
            if (clean.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) clean = clean[7..];
            if (clean.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) clean = clean[8..];
            clean = clean.Trim('/');

            if (!string.IsNullOrWhiteSpace(CloudflareDomain) && !clean.Contains('.'))
            {
                clean = $"{clean}.{CloudflareDomain.Trim().TrimStart('.')}";
            }

            service.CloudflareHostname = clean;
            service.Labels["cloudflare.tunnel.hostname"] = clean;
            service.Environment["ASPNETCORE_FORWARDEDHEADERS_ENABLED"] = "true";
        }
        else
        {
            service.CloudflareHostname = null;
            service.Labels.Remove("cloudflare.tunnel.hostname");
        }

        return service;
    }
}
