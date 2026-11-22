using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DockerManager.App.Models;
using DockerManager.App.Services;

namespace DockerManager.App.ViewModels;

public partial class ServerSettingsTabViewModel : SettingsTabViewModel
{
    private readonly ISettingsService _settingsService;
    private readonly ICredentialService _credentialService;
    private readonly ICloudflareService _cloudflareService;
    private readonly IDockerService _dockerService;
    private readonly Func<IEnumerable<ProfileModel>> _getAllProfilesAcrossServers;

    public DockerServerEnvironment Server { get; }

    [ObservableProperty]
    private string _name = string.Empty;

    partial void OnNameChanged(string value)
    {
        UpdateHeader(value);
    }

    private void UpdateHeader(string serverName)
    {
        var clean = AppSettings.StripLeadingEmojis(serverName);
        Header = $"🖥️ {(string.IsNullOrWhiteSpace(clean) ? "Server" : clean)}";
    }

    [ObservableProperty]
    private string _profilesSubfolder = string.Empty;

    [ObservableProperty]
    private string _volumesRootPath = string.Empty;

    [RelayCommand]
    private void BrowseVolumesRoot()
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Kies Volumes Hoofdmap",
                InitialDirectory = !string.IsNullOrWhiteSpace(VolumesRootPath) && System.IO.Directory.Exists(VolumesRootPath)
                    ? VolumesRootPath
                    : System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData)
            };

            if (dialog.ShowDialog() == true)
            {
                VolumesRootPath = dialog.FolderName;
            }
        }
        catch { }
    }

    [ObservableProperty]
    private string _hostType = "Pipe";

    partial void OnHostTypeChanged(string value)
    {
        OnPropertyChanged(nameof(IsPipeSelected));
        OnPropertyChanged(nameof(IsTcpSelected));
    }

    public bool IsPipeSelected
    {
        get => HostType.Equals("Pipe", StringComparison.OrdinalIgnoreCase);
        set
        {
            if (value)
            {
                HostType = "Pipe";
            }
        }
    }

    public bool IsTcpSelected
    {
        get => HostType.Equals("Tcp", StringComparison.OrdinalIgnoreCase);
        set
        {
            if (value)
            {
                HostType = "Tcp";
            }
        }
    }

    [ObservableProperty]
    private bool _isActiveServer;

    public event Action<ServerSettingsTabViewModel>? RequestMakeActiveServer;

    [RelayCommand]
    private void MakeActiveServer()
    {
        RequestMakeActiveServer?.Invoke(this);
    }

    [ObservableProperty]
    private string _pipeName = "npipe://./pipe/docker_engine";

    [ObservableProperty]
    private string _tcpUrl = "tcp://192.168.1.50:2375";

    [ObservableProperty]
    private string _dockerTestStatus = string.Empty;

    [ObservableProperty]
    private bool _isTestingDocker;

    // Cloudflare Tunnel (1 tunnel per server)
    [ObservableProperty]
    private string _cloudflareDomain = string.Empty;

    [ObservableProperty]
    private string _cloudflareAccountId = string.Empty;

    [ObservableProperty]
    private string _cloudflareApiToken = string.Empty;

    [ObservableProperty]
    private string _cloudflareTunnelId = string.Empty;

    partial void OnCloudflareTunnelIdChanged(string value)
    {
        NotifyProfilesTunnelChanged();
    }

    [ObservableProperty]
    private string _cloudflareTunnelName = string.Empty;

    partial void OnCloudflareTunnelNameChanged(string value)
    {
        NotifyProfilesTunnelChanged();
    }

    public void NotifyProfilesTunnelChanged()
    {
        foreach (var p in Profiles)
        {
            p.RefreshServerTunnelSummary();
        }
    }

    [ObservableProperty]
    private string _cloudflareTunnelToken = string.Empty;

    [ObservableProperty]
    private string _newTunnelName = string.Empty;

    [ObservableProperty]
    private string _cloudflareStatusText = string.Empty;

    [ObservableProperty]
    private bool _isLoadingTunnels;

    [ObservableProperty]
    private bool _isCreatingTunnel;

    [ObservableProperty]
    private bool _isTestingCloudflare;

    public ObservableCollection<CloudflareTunnelInfo> AvailableTunnels { get; } = new();

    [ObservableProperty]
    private CloudflareTunnelInfo? _selectedTunnel;

    partial void OnSelectedTunnelChanged(CloudflareTunnelInfo? value)
    {
        if (value != null)
        {
            CloudflareTunnelId = value.Id;
            CloudflareTunnelName = value.Name;
            _ = FetchTunnelTokenForSelectionAsync(value.Id);
        }
    }

    public ObservableCollection<ProfileSettingsItemViewModel> Profiles { get; } = new();

    [ObservableProperty]
    private ProfileSettingsItemViewModel? _selectedProfile;

    partial void OnSelectedProfileChanged(ProfileSettingsItemViewModel? value)
    {
        if (value != null)
        {
            value.PopulateCopySources(_getAllProfilesAcrossServers());
        }
    }

    public List<string> DeletedProfileNames { get; } = new();

    public event Action<ServerSettingsTabViewModel>? RequestDeleteServer;

    public ServerSettingsTabViewModel(
        DockerServerEnvironment server,
        IEnumerable<ProfileModel> serverProfiles,
        ISettingsService settingsService,
        ICredentialService credentialService,
        ICloudflareService cloudflareService,
        IDockerService dockerService,
        Func<IEnumerable<ProfileModel>> getAllProfilesAcrossServers)
    {
        Server = server;
        _settingsService = settingsService;
        _credentialService = credentialService;
        _cloudflareService = cloudflareService;
        _dockerService = dockerService;
        _getAllProfilesAcrossServers = getAllProfilesAcrossServers;

        Icon = "🖥️";
        Name = server.Name;
        UpdateHeader(server.Name);
        ProfilesSubfolder = server.ProfilesSubfolder;
        VolumesRootPath = server.VolumesRootPath;

        HostType = server.HostType.Equals("Tcp", StringComparison.OrdinalIgnoreCase) ? "Tcp" : "Pipe";

        PipeName = string.IsNullOrWhiteSpace(server.PipeName) ? "npipe://./pipe/docker_engine" : server.PipeName;
        TcpUrl = string.IsNullOrWhiteSpace(server.TcpUrl) ? "tcp://192.168.1.50:2375" : server.TcpUrl;

        // Cloudflare initialization
        CloudflareDomain = server.CloudflareDomain ?? string.Empty;
        CloudflareAccountId = server.CloudflareAccountId ?? string.Empty;
        CloudflareTunnelId = server.CloudflareTunnelId ?? string.Empty;
        CloudflareTunnelName = server.CloudflareTunnelName ?? string.Empty;
        CloudflareTunnelToken = server.CloudflareTunnelToken ?? string.Empty;
        CloudflareApiToken = server.CloudflareApiToken ?? string.Empty;

        // Fallbacks from global settings if server-level is empty
        if (string.IsNullOrWhiteSpace(CloudflareDomain))
            CloudflareDomain = _settingsService.Settings.CloudflareDomain ?? string.Empty;
        if (string.IsNullOrWhiteSpace(CloudflareAccountId))
            CloudflareAccountId = _settingsService.Settings.CloudflareAccountId ?? string.Empty;
        if (string.IsNullOrWhiteSpace(CloudflareApiToken))
            CloudflareApiToken = _credentialService.GetCloudflareApiToken() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(CloudflareTunnelId) && !string.IsNullOrWhiteSpace(_settingsService.Settings.CloudflareTunnelId))
        {
            CloudflareTunnelId = _settingsService.Settings.CloudflareTunnelId;
            CloudflareTunnelToken = _settingsService.Settings.CloudflareTunnelToken ?? string.Empty;
        }

        var cleanServer = AppSettings.StripLeadingEmojis(server.Name).Trim().ToLowerInvariant().Replace(" ", "-");
        NewTunnelName = string.IsNullOrWhiteSpace(cleanServer) ? "dockermanager-tunnel" : $"dockermanager-{cleanServer}";

        if (!string.IsNullOrWhiteSpace(CloudflareTunnelId))
        {
            var existingTunnel = new CloudflareTunnelInfo
            {
                Id = CloudflareTunnelId,
                Name = string.IsNullOrWhiteSpace(CloudflareTunnelName) ? "Gekoppelde Tunnel" : CloudflareTunnelName,
                Status = "active"
            };
            AvailableTunnels.Add(existingTunnel);
            _selectedTunnel = existingTunnel;
            CloudflareStatusText = $"Gekoppeld: {existingTunnel.DisplayName}";
        }

        foreach (var p in serverProfiles)
        {
            var pVm = new ProfileSettingsItemViewModel(p, _settingsService, _credentialService, _cloudflareService, _dockerService, _getAllProfilesAcrossServers(), Server, this);
            Profiles.Add(pVm);
        }

        SelectedProfile = Profiles.FirstOrDefault();
    }

    [RelayCommand]
    private void SelectPipe()
    {
        HostType = "Pipe";
    }

    [RelayCommand]
    private void SelectTcp()
    {
        HostType = "Tcp";
    }

    [RelayCommand]
    private void CopyGlobalCloudflare()
    {
        var s = _settingsService.Settings;
        if (!string.IsNullOrWhiteSpace(s.CloudflareDomain)) CloudflareDomain = s.CloudflareDomain;
        if (!string.IsNullOrWhiteSpace(s.CloudflareAccountId)) CloudflareAccountId = s.CloudflareAccountId;
        var token = _credentialService.GetCloudflareApiToken();
        if (!string.IsNullOrWhiteSpace(token)) CloudflareApiToken = token;
        CloudflareStatusText = "Cloudflare instellingen overgenomen van globale app-instellingen.";
    }

    [RelayCommand]
    private async Task RefreshTunnelsAsync()
    {
        if (IsLoadingTunnels) return;
        IsLoadingTunnels = true;
        CloudflareStatusText = "Tunnels ophalen uit Cloudflare...";

        try
        {
            var token = !string.IsNullOrWhiteSpace(CloudflareApiToken) ? CloudflareApiToken : _credentialService.GetCloudflareApiToken();
            var accountId = !string.IsNullOrWhiteSpace(CloudflareAccountId) ? CloudflareAccountId : _settingsService.Settings.CloudflareAccountId;

            if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(accountId))
            {
                CloudflareStatusText = "❌ Vul eerst Cloudflare Account ID en API Token in.";
                return;
            }

            var (success, message, tunnels) = await _cloudflareService.ListTunnelsAsync(token, accountId);
            if (!success)
            {
                CloudflareStatusText = $"❌ {message}";
                return;
            }

            AvailableTunnels.Clear();
            foreach (var t in tunnels)
            {
                AvailableTunnels.Add(t);
            }

            if (!string.IsNullOrWhiteSpace(CloudflareTunnelId))
            {
                var match = AvailableTunnels.FirstOrDefault(t => t.Id.Equals(CloudflareTunnelId, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    SelectedTunnel = match;
                }
            }

            CloudflareStatusText = $"✅ {AvailableTunnels.Count} tunnel(s) opgehaald.";
        }
        catch (Exception ex)
        {
            CloudflareStatusText = $"❌ Fout bij ophalen tunnels: {ex.Message}";
        }
        finally
        {
            IsLoadingTunnels = false;
        }
    }

    [RelayCommand]
    private async Task CreateNewTunnelAsync()
    {
        if (IsCreatingTunnel) return;
        IsCreatingTunnel = true;
        CloudflareStatusText = "Nieuwe tunnel aanmaken in Cloudflare...";

        try
        {
            var token = !string.IsNullOrWhiteSpace(CloudflareApiToken) ? CloudflareApiToken : _credentialService.GetCloudflareApiToken();
            var accountId = !string.IsNullOrWhiteSpace(CloudflareAccountId) ? CloudflareAccountId : _settingsService.Settings.CloudflareAccountId;

            if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(accountId))
            {
                CloudflareStatusText = "❌ Vul eerst Cloudflare Account ID en API Token in.";
                return;
            }

            var cleanName = !string.IsNullOrWhiteSpace(NewTunnelName)
                ? NewTunnelName.Trim()
                : $"dockermanager-{AppSettings.StripLeadingEmojis(Server.Name).Trim().ToLowerInvariant().Replace(" ", "-")}";

            var (success, message, created) = await _cloudflareService.CreateTunnelAsync(cleanName, token, accountId);
            if (!success || created == null)
            {
                CloudflareStatusText = $"❌ {message}";
                return;
            }

            AvailableTunnels.Insert(0, created);
            CloudflareTunnelId = created.Id;
            CloudflareTunnelName = created.Name;
            SelectedTunnel = created;

            var runToken = await _cloudflareService.FetchTunnelTokenAsync(token, accountId, created.Id);
            if (!string.IsNullOrWhiteSpace(runToken))
            {
                CloudflareTunnelToken = runToken;
            }

            NotifyProfilesTunnelChanged();
            CloudflareStatusText = $"✅ Nieuwe tunnel '{created.Name}' aangemaakt en gekoppeld aan de server.";
        }
        catch (Exception ex)
        {
            CloudflareStatusText = $"❌ Fout bij aanmaken tunnel: {ex.Message}";
        }
        finally
        {
            IsCreatingTunnel = false;
        }
    }

    private async Task FetchTunnelTokenForSelectionAsync(string tunnelId)
    {
        if (string.IsNullOrWhiteSpace(tunnelId)) return;

        var token = !string.IsNullOrWhiteSpace(CloudflareApiToken) ? CloudflareApiToken : _credentialService.GetCloudflareApiToken();
        var accountId = !string.IsNullOrWhiteSpace(CloudflareAccountId) ? CloudflareAccountId : _settingsService.Settings.CloudflareAccountId;

        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(accountId)) return;

        try
        {
            var runToken = await _cloudflareService.FetchTunnelTokenAsync(token, accountId, tunnelId);
            if (!string.IsNullOrWhiteSpace(runToken))
            {
                CloudflareTunnelToken = runToken;
                CloudflareStatusText = $"✅ Tunnel '{CloudflareTunnelName}' geselecteerd (token geladen)";
            }
        }
        catch (Exception ex)
        {
            CloudflareStatusText = $"⚠️ Token kon niet worden opgehaald: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task TestCloudflareConnectionAsync()
    {
        if (IsTestingCloudflare) return;
        IsTestingCloudflare = true;
        CloudflareStatusText = "Cloudflare verbinding testen...";

        try
        {
            var token = !string.IsNullOrWhiteSpace(CloudflareApiToken) ? CloudflareApiToken : _credentialService.GetCloudflareApiToken();
            var accountId = !string.IsNullOrWhiteSpace(CloudflareAccountId) ? CloudflareAccountId : _settingsService.Settings.CloudflareAccountId;
            var tunnelId = CloudflareTunnelId;

            if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(accountId))
            {
                CloudflareStatusText = "❌ Vul eerst Cloudflare Account ID en API Token in.";
                return;
            }

            if (string.IsNullOrWhiteSpace(tunnelId))
            {
                CloudflareStatusText = "⚠️ Geen tunnel geselecteerd. Selecteer of maak eerst een tunnel aan.";
                return;
            }

            var (success, message, _) = await _cloudflareService.TestConnectionAsync(token, accountId, tunnelId);
            CloudflareStatusText = success ? $"✅ {message}" : $"❌ {message}";

            if (success)
            {
                var runToken = await _cloudflareService.FetchTunnelTokenAsync(token, accountId, tunnelId);
                if (!string.IsNullOrWhiteSpace(runToken))
                {
                    CloudflareTunnelToken = runToken;
                }
            }
        }
        catch (Exception ex)
        {
            CloudflareStatusText = $"❌ Fout: {ex.Message}";
        }
        finally
        {
            IsTestingCloudflare = false;
        }
    }

    [RelayCommand]
    private void AddProfile()
    {
        int counter = Profiles.Count + 1;
        while (Profiles.Any(p => p.Name.Equals($"Profiel {counter}", StringComparison.OrdinalIgnoreCase)))
        {
            counter++;
        }

        var newProfile = new ProfileModel
        {
            Name = $"Profiel {counter}",
            Description = $"Nieuw profiel op {Name}"
        };

        var pVm = new ProfileSettingsItemViewModel(newProfile, _settingsService, _credentialService, _cloudflareService, _dockerService, _getAllProfilesAcrossServers(), Server, this);
        Profiles.Add(pVm);
        SelectedProfile = pVm;
    }

    [RelayCommand]
    private void DeleteProfile()
    {
        if (SelectedProfile == null) return;
        if (Profiles.Count <= 1)
        {
            WpfDialogService.Instance.ShowWarning("Er moet minimaal één profiel per server blijven bestaan.", "Kan niet verwijderen");
            return;
        }

        var toDelete = SelectedProfile;
        DeletedProfileNames.Add(toDelete.OriginalName);
        Profiles.Remove(toDelete);
        SelectedProfile = Profiles.FirstOrDefault();
    }

    [RelayCommand]
    private void DeleteServer()
    {
        RequestDeleteServer?.Invoke(this);
    }

    [RelayCommand]
    private async Task TestDockerConnectionAsync()
    {
        if (IsTestingDocker) return;
        IsTestingDocker = true;
        DockerTestStatus = "Verbinding testen...";

        try
        {
            var hostType = IsTcpSelected ? "Tcp" : "Pipe";
            var uri = hostType == "Tcp" ? TcpUrl : PipeName;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var client = new Docker.DotNet.DockerClientConfiguration(new Uri(uri)).CreateClient();
            await client.System.PingAsync(cts.Token);
            var version = await client.System.GetVersionAsync(cts.Token);
            DockerTestStatus = $"✅ Verbonden! Docker {version.Version} (OS: {version.Os})";
        }
        catch (Exception ex)
        {
            DockerTestStatus = $"❌ Kan geen verbinding maken: {ex.Message}";
        }
        finally
        {
            IsTestingDocker = false;
        }
    }

    public void ApplyToServer()
    {
        Server.Name = string.IsNullOrWhiteSpace(Name) ? "Docker Server" : Name.Trim();
        Server.HostType = HostType;
        Server.PipeName = PipeName.Trim();
        Server.TcpUrl = TcpUrl.Trim();
        Server.ProfilesSubfolder = string.IsNullOrWhiteSpace(ProfilesSubfolder) ? "local" : ProfilesSubfolder.Trim();
        Server.VolumesRootPath = VolumesRootPath.Trim();

        Server.CloudflareDomain = CloudflareDomain.Trim();
        Server.CloudflareAccountId = CloudflareAccountId.Trim();
        Server.CloudflareTunnelId = CloudflareTunnelId.Trim();
        Server.CloudflareTunnelName = CloudflareTunnelName.Trim();
        Server.CloudflareTunnelToken = CloudflareTunnelToken.Trim();
        Server.CloudflareApiToken = CloudflareApiToken.Trim();

        if (!string.IsNullOrWhiteSpace(CloudflareApiToken))
        {
            _credentialService.SaveCloudflareApiToken(CloudflareApiToken.Trim());
        }

        foreach (var pVm in Profiles)
        {
            pVm.ApplyToProfile();
        }
    }
}
