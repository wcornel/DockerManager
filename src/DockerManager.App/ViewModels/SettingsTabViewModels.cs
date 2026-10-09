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

public abstract partial class SettingsTabViewModel : ObservableObject
{
    [ObservableProperty]
    private string _header = string.Empty;

    [ObservableProperty]
    private string _icon = string.Empty;
}

public partial class GeneralSettingsTabViewModel : SettingsTabViewModel
{
    private readonly ISettingsService _settingsService;
    private readonly ICredentialService _credentialService;
    private readonly IGitHubAuthService _gitHubAuthService;
    private readonly IGitHubProfileService _gitHubProfileService;

    [ObservableProperty]
    private string _selectedLanguage = "auto";

    public string[] AvailableLanguages { get; } = new[] { "Systeemtaal / System Default", "Nederlands (NL)", "English (EN)" };

    public string SelectedLanguageDisplay
    {
        get => SelectedLanguage switch
        {
            "nl" => "Nederlands (NL)",
            "en" => "English (EN)",
            _ => "Systeemtaal / System Default"
        };
        set
        {
            SelectedLanguage = value switch
            {
                "Nederlands (NL)" => "nl",
                "English (EN)" => "en",
                _ => "auto"
            };
            OnPropertyChanged();
        }
    }

    [ObservableProperty]
    private bool _autoCheckUpdatesOnStart = true;

    [ObservableProperty]
    private bool _autoCheckImageUpdates = true;

    [ObservableProperty]
    private string _updateCheckFrequency = "Dagelijks";

    public string[] AvailableFrequencies { get; } = new[] { "Dagelijks", "Wekelijks", "Handmatig" };

    [ObservableProperty]
    private bool _defaultKeepRunningOnSwitch = true;

    [ObservableProperty]
    private bool _promptToStopPreviousProfile;

    // GitHub
    [ObservableProperty]
    private string _loggedInUsername = string.Empty;

    [ObservableProperty]
    private bool _isLoggedIn;

    [ObservableProperty]
    private string _gitHubRepoOwner = string.Empty;

    [ObservableProperty]
    private string _gitHubRepoName = string.Empty;

    [ObservableProperty]
    private string _gitHubTestStatus = string.Empty;

    [ObservableProperty]
    private bool _isGitHubTesting;

    public event Action? RequestOpenGitHubLogin;

    public GeneralSettingsTabViewModel(
        ISettingsService settingsService,
        ICredentialService credentialService,
        IGitHubAuthService gitHubAuthService,
        IGitHubProfileService gitHubProfileService)
    {
        _settingsService = settingsService;
        _credentialService = credentialService;
        _gitHubAuthService = gitHubAuthService;
        _gitHubProfileService = gitHubProfileService;

        Header = "⚙️ Algemeen";
        Icon = "⚙️";

        LoadFromSettings();
    }

    public void LoadFromSettings()
    {
        var s = _settingsService.Settings;
        SelectedLanguage = s.Language;
        AutoCheckUpdatesOnStart = s.AutoCheckUpdatesOnStart;
        AutoCheckImageUpdates = s.AutoCheckImageUpdates;
        UpdateCheckFrequency = s.UpdateCheckFrequency;
        DefaultKeepRunningOnSwitch = s.DefaultKeepRunningOnSwitch;
        PromptToStopPreviousProfile = s.PromptToStopPreviousProfile;

        GitHubRepoOwner = s.GitHubRepoOwner;
        GitHubRepoName = s.GitHubRepoName;

        RefreshLoginStatus();
    }

    public void RefreshLoginStatus()
    {
        var token = _credentialService.GetGitHubToken();
        IsLoggedIn = !string.IsNullOrEmpty(token);
        LoggedInUsername = !string.IsNullOrWhiteSpace(_settingsService.Settings.LoggedInUsername)
            ? _settingsService.Settings.LoggedInUsername
            : (IsLoggedIn ? "GitHub Gebruiker" : string.Empty);
    }

    [RelayCommand]
    private void OpenGitHubLogin()
    {
        RequestOpenGitHubLogin?.Invoke();
    }

    [RelayCommand]
    private void LogoutGitHub()
    {
        _credentialService.SaveGitHubToken(string.Empty);
        _settingsService.Settings.HasGitHubToken = false;
        _settingsService.Settings.LoggedInUsername = string.Empty;
        _settingsService.Save();
        RefreshLoginStatus();
        GitHubTestStatus = "Uitgelogd van GitHub.";
    }

    [RelayCommand]
    private async Task TestGitHubConnectionAsync()
    {
        if (IsGitHubTesting) return;
        IsGitHubTesting = true;
        GitHubTestStatus = "Verbinding testen...";

        try
        {
            var (success, message) = await _gitHubProfileService.TestGitHubConnectionAsync();
            GitHubTestStatus = success ? $"✅ {message}" : $"❌ {message}";
        }
        catch (Exception ex)
        {
            GitHubTestStatus = $"❌ Fout: {ex.Message}";
        }
        finally
        {
            IsGitHubTesting = false;
        }
    }

    public void ApplyToSettings()
    {
        var s = _settingsService.Settings;
        s.Language = SelectedLanguage;
        s.AutoCheckUpdatesOnStart = AutoCheckUpdatesOnStart;
        s.AutoCheckImageUpdates = AutoCheckImageUpdates;
        s.UpdateCheckFrequency = UpdateCheckFrequency;
        s.DefaultKeepRunningOnSwitch = DefaultKeepRunningOnSwitch;
        s.PromptToStopPreviousProfile = PromptToStopPreviousProfile;

        s.GitHubRepoOwner = GitHubRepoOwner.Trim();
        s.GitHubRepoName = GitHubRepoName.Trim();
    }
}

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
            SelectedTunnel = existingTunnel;
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
            System.Windows.MessageBox.Show("Er moet minimaal één profiel per server blijven bestaan.", "Kan niet verwijderen", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
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
            var client = new Docker.DotNet.DockerClientConfiguration(new Uri(uri)).CreateClient();
            await client.System.PingAsync();
            var version = await client.System.GetVersionAsync();
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
