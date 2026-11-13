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

public partial class ProfileSettingsItemViewModel : ObservableObject
{
    private readonly ISettingsService _settingsService;
    private readonly ICredentialService _credentialService;
    private readonly ICloudflareService _cloudflareService;
    private readonly IDockerService _dockerService;
    private readonly DockerServerEnvironment? _parentServer;
    private readonly ServerSettingsTabViewModel? _parentServerVm;

    public ProfileModel Profile { get; }
    public string OriginalName { get; }

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    private bool _autoStopPreviousOnSwitch;

    [ObservableProperty]
    private string _volumesSubfolder = string.Empty;

    // Quick Copy
    public ObservableCollection<ProfileCopySource> CopySources { get; } = new();

    [ObservableProperty]
    private ProfileCopySource? _selectedCopySource;

    [ObservableProperty]
    private string _copyStatusMessage = string.Empty;

    // Cloudflare
    [ObservableProperty]
    private string _cloudflareAccountId = string.Empty;

    [ObservableProperty]
    private string _cloudflareTunnelId = string.Empty;

    [ObservableProperty]
    private string _cloudflareDomain = string.Empty;

    [ObservableProperty]
    private string _cloudflareApiToken = string.Empty;

    [ObservableProperty]
    private string _cloudflareTunnelToken = string.Empty;

    [ObservableProperty]
    private bool _isTestingCloudflare;

    [ObservableProperty]
    private string _cloudflareTestStatus = string.Empty;

    public string ServerTunnelSummary
    {
        get
        {
            var tunnelName = _parentServerVm?.CloudflareTunnelName ?? _parentServer?.CloudflareTunnelName;
            var tunnelId = _parentServerVm?.CloudflareTunnelId ?? _parentServer?.CloudflareTunnelId;

            if (!string.IsNullOrWhiteSpace(tunnelName) && !string.IsNullOrWhiteSpace(tunnelId))
                return $"🔗 {tunnelName} ({tunnelId})";
            if (!string.IsNullOrWhiteSpace(tunnelId))
                return $"🔗 {tunnelId}";
            if (!string.IsNullOrWhiteSpace(tunnelName))
                return $"🔗 {tunnelName}";

            return "⚠️ Geen tunnel gekoppeld op de server";
        }
    }

    public void RefreshServerTunnelSummary()
    {
        OnPropertyChanged(nameof(ServerTunnelSummary));
    }

    // Registry
    [ObservableProperty]
    private string _registryServer = "ghcr.io";

    [ObservableProperty]
    private string _registryNamespace = string.Empty;

    [ObservableProperty]
    private string _registryUsername = string.Empty;

    [ObservableProperty]
    private string _registryPassword = string.Empty;

    [ObservableProperty]
    private bool _isTestingRegistry;

    [ObservableProperty]
    private string _registryTestStatus = string.Empty;

    public ProfileSettingsItemViewModel(
        ProfileModel profile,
        ISettingsService settingsService,
        ICredentialService credentialService,
        ICloudflareService cloudflareService,
        IDockerService dockerService,
        IEnumerable<ProfileModel> allAvailableProfiles,
        DockerServerEnvironment? parentServer = null,
        ServerSettingsTabViewModel? parentServerVm = null)
    {
        Profile = profile;
        _settingsService = settingsService;
        _credentialService = credentialService;
        _cloudflareService = cloudflareService;
        _dockerService = dockerService;
        _parentServer = parentServer;
        _parentServerVm = parentServerVm;

        OriginalName = profile.Name;
        Name = profile.Name;
        Description = profile.Description;
        AutoStopPreviousOnSwitch = profile.AutoStopPreviousOnSwitch;
        VolumesSubfolder = profile.VolumesSubfolder;

        var globalSettings = _settingsService.Settings;

        // Cloudflare
        var cf = profile.Cloudflare ?? new ProfileCloudflareConfig();
        var srvDomain = parentServer?.CloudflareDomain;
        var srvAccountId = parentServer?.CloudflareAccountId;
        var srvTunnelId = parentServer?.CloudflareTunnelId;
        var srvTunnelToken = parentServer?.CloudflareTunnelToken;
        var srvApiToken = parentServer?.CloudflareApiToken;

        CloudflareDomain = !string.IsNullOrWhiteSpace(cf.Domain)
            ? cf.Domain
            : (!string.IsNullOrWhiteSpace(srvDomain) ? srvDomain : (globalSettings.CloudflareDomain ?? string.Empty));
        CloudflareAccountId = !string.IsNullOrWhiteSpace(cf.AccountId)
            ? cf.AccountId
            : (!string.IsNullOrWhiteSpace(srvAccountId) ? srvAccountId : (globalSettings.CloudflareAccountId ?? string.Empty));
        CloudflareTunnelId = !string.IsNullOrWhiteSpace(cf.TunnelId)
            ? cf.TunnelId
            : (!string.IsNullOrWhiteSpace(srvTunnelId) ? srvTunnelId : (globalSettings.CloudflareTunnelId ?? string.Empty));
        CloudflareTunnelToken = !string.IsNullOrWhiteSpace(cf.TunnelToken)
            ? cf.TunnelToken
            : (!string.IsNullOrWhiteSpace(srvTunnelToken) ? srvTunnelToken : (globalSettings.CloudflareTunnelToken ?? string.Empty));
        CloudflareApiToken = !string.IsNullOrWhiteSpace(cf.ApiToken)
            ? cf.ApiToken
            : (!string.IsNullOrWhiteSpace(srvApiToken) ? srvApiToken : (_credentialService.GetCloudflareApiToken() ?? string.Empty));

        // Registry
        var reg = profile.Registry ?? new ProfileRegistryConfig();
        RegistryServer = !string.IsNullOrWhiteSpace(reg.Server) ? reg.Server : "ghcr.io";
        RegistryNamespace = !string.IsNullOrWhiteSpace(reg.Namespace)
            ? reg.Namespace
            : (!string.IsNullOrWhiteSpace(globalSettings.LoggedInUsername) ? globalSettings.LoggedInUsername : (globalSettings.GitHubRepoOwner ?? string.Empty));
        RegistryUsername = !string.IsNullOrWhiteSpace(reg.Username)
            ? reg.Username
            : (globalSettings.LoggedInUsername ?? string.Empty);
        RegistryPassword = !string.IsNullOrWhiteSpace(reg.Password)
            ? reg.Password
            : (_credentialService.GetGitHubToken() ?? string.Empty);

        PopulateCopySources(allAvailableProfiles);
    }

    public void PopulateCopySources(IEnumerable<ProfileModel> allAvailableProfiles)
    {
        CopySources.Clear();

        // 1. Offer Parent Server if available
        if (_parentServer != null)
        {
            CopySources.Add(new ProfileCopySource
            {
                DisplayName = $"🖥️ Server '{_parentServer.Name}' (Aanbevolen)",
                IsServerDefaults = true,
                SourceServer = _parentServer
            });
        }

        // 2. Always offer Global Defaults
        CopySources.Add(new ProfileCopySource
        {
            DisplayName = "⚙️ Globale Standaard Instellingen (App)",
            IsGlobalDefaults = true
        });

        // 3. Offer all other profiles
        foreach (var p in allAvailableProfiles.Where(p => !p.Name.Equals(OriginalName, StringComparison.OrdinalIgnoreCase)))
        {
            var desc = string.IsNullOrWhiteSpace(p.Description) ? "" : $" ({p.Description})";
            CopySources.Add(new ProfileCopySource
            {
                DisplayName = $"📁 Profiel '{p.Name}'{desc}",
                IsGlobalDefaults = false,
                SourceProfile = p
            });
        }

        SelectedCopySource = CopySources.FirstOrDefault();
    }

    [RelayCommand]
    private void CopyCloudflareSettings()
    {
        if (SelectedCopySource == null) return;

        if (SelectedCopySource.IsServerDefaults && SelectedCopySource.SourceServer != null)
        {
            var s = SelectedCopySource.SourceServer;
            CloudflareDomain = s.CloudflareDomain ?? string.Empty;
            CloudflareAccountId = s.CloudflareAccountId ?? string.Empty;
            CloudflareTunnelId = s.CloudflareTunnelId ?? string.Empty;
            CloudflareTunnelToken = s.CloudflareTunnelToken ?? string.Empty;
            CloudflareApiToken = s.CloudflareApiToken ?? string.Empty;
            CopyStatusMessage = $"✅ Cloudflare overgenomen van server '{s.Name}'!";
        }
        else if (SelectedCopySource.IsGlobalDefaults)
        {
            var s = _settingsService.Settings;
            CloudflareDomain = s.CloudflareDomain ?? string.Empty;
            CloudflareAccountId = s.CloudflareAccountId ?? string.Empty;
            CloudflareTunnelId = s.CloudflareTunnelId ?? string.Empty;
            CloudflareTunnelToken = s.CloudflareTunnelToken ?? string.Empty;
            var token = _credentialService.GetCloudflareApiToken();
            if (!string.IsNullOrWhiteSpace(token))
            {
                CloudflareApiToken = token;
            }
            CopyStatusMessage = "✅ Cloudflare overgenomen van globale app-instellingen!";
        }
        else if (SelectedCopySource.SourceProfile?.Cloudflare is { } srcCf)
        {
            CloudflareDomain = srcCf.Domain ?? string.Empty;
            CloudflareAccountId = srcCf.AccountId ?? string.Empty;
            CloudflareTunnelId = srcCf.TunnelId ?? string.Empty;
            CloudflareApiToken = srcCf.ApiToken ?? string.Empty;
            CloudflareTunnelToken = srcCf.TunnelToken ?? string.Empty;
            CopyStatusMessage = $"✅ Cloudflare instellingen overgenomen van '{SelectedCopySource.SourceProfile.Name}'!";
        }
    }

    [RelayCommand]
    private void CopyRegistrySettings()
    {
        if (SelectedCopySource == null) return;

        if (SelectedCopySource.IsGlobalDefaults)
        {
            var s = _settingsService.Settings;
            RegistryServer = "ghcr.io";
            RegistryNamespace = !string.IsNullOrWhiteSpace(s.LoggedInUsername) ? s.LoggedInUsername : (s.GitHubRepoOwner ?? string.Empty);
            RegistryUsername = s.LoggedInUsername ?? string.Empty;
            var ghToken = _credentialService.GetGitHubToken();
            if (!string.IsNullOrWhiteSpace(ghToken))
            {
                RegistryPassword = ghToken;
            }
            CopyStatusMessage = "✅ Container Registry overgenomen van globale app-instellingen!";
        }
        else if (SelectedCopySource.SourceProfile?.Registry is { } srcReg)
        {
            RegistryServer = !string.IsNullOrWhiteSpace(srcReg.Server) ? srcReg.Server : "ghcr.io";
            RegistryNamespace = srcReg.Namespace ?? string.Empty;
            RegistryUsername = srcReg.Username ?? string.Empty;
            RegistryPassword = srcReg.Password ?? string.Empty;
            CopyStatusMessage = $"✅ Container Registry overgenomen van '{SelectedCopySource.SourceProfile.Name}'!";
        }
    }

    [RelayCommand]
    private void CopyAllSettings()
    {
        CopyCloudflareSettings();
        CopyRegistrySettings();
        if (SelectedCopySource?.SourceProfile is { } src)
        {
            VolumesSubfolder = src.VolumesSubfolder;
        }
        CopyStatusMessage = $"✅ Alle instellingen overgenomen van '{SelectedCopySource?.DisplayName}'!";
    }

    [RelayCommand]
    private async Task TestRegistryAsync()
    {
        if (string.IsNullOrWhiteSpace(RegistryServer))
        {
            RegistryTestStatus = "⚠️ Vul een Registry server in (bijv. ghcr.io).";
            return;
        }

        IsTestingRegistry = true;
        RegistryTestStatus = $"Verbinden met {RegistryServer}...";

        try
        {
            var cred = new RegistryCredential
            {
                ServerAddress = RegistryServer.Trim(),
                Username = RegistryUsername.Trim(),
                Password = RegistryPassword.Trim(),
                DisplayName = RegistryServer.Trim()
            };

            var (ok, msg) = await _dockerService.TestRegistryConnectionAsync(cred);
            RegistryTestStatus = ok ? $"✅ {RegistryServer}: {msg}" : $"❌ {RegistryServer}: {msg}";
        }
        catch (Exception ex)
        {
            RegistryTestStatus = $"❌ Fout: {ex.Message}";
        }
        finally
        {
            IsTestingRegistry = false;
        }
    }

    public void ApplyToProfile()
    {
        Profile.Name = string.IsNullOrWhiteSpace(Name) ? OriginalName : Name.Trim();
        Profile.Description = Description.Trim();
        Profile.AutoStopPreviousOnSwitch = AutoStopPreviousOnSwitch;
        Profile.VolumesSubfolder = VolumesSubfolder.Trim();

        // Cloudflare (domain override only; tunnel is inherited dynamically from server)
        Profile.Cloudflare ??= new ProfileCloudflareConfig();
        Profile.Cloudflare.Domain = CloudflareDomain.Trim();
        Profile.Cloudflare.TunnelId = string.Empty;
        Profile.Cloudflare.TunnelToken = string.Empty;
        Profile.Cloudflare.AccountId = string.Empty;
        Profile.Cloudflare.ApiToken = string.Empty;

        // Registry
        Profile.Registry ??= new ProfileRegistryConfig();
        Profile.Registry.Server = RegistryServer.Trim();
        Profile.Registry.Namespace = RegistryNamespace.Trim();
        Profile.Registry.Username = RegistryUsername.Trim();
        Profile.Registry.Password = RegistryPassword.Trim();

        if (!string.IsNullOrWhiteSpace(Profile.Registry.Password) && !string.IsNullOrWhiteSpace(Profile.Registry.Server))
        {
            _credentialService.SaveRegistryCredential(new RegistryCredential
            {
                ServerAddress = Profile.Registry.Server,
                Username = !string.IsNullOrWhiteSpace(Profile.Registry.Username) 
                    ? Profile.Registry.Username 
                    : (!string.IsNullOrWhiteSpace(Profile.Registry.Namespace) ? Profile.Registry.Namespace : _settingsService.Settings.GitHubRepoOwner),
                Password = Profile.Registry.Password,
                DisplayName = $"{Profile.Registry.Server} ({Profile.Name})"
            });
        }
    }
}
