using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DockerManager.App.Models;
using DockerManager.App.Services;

namespace DockerManager.App.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settingsService;
    private readonly ICredentialService _credentialService;
    private readonly IGitHubProfileService _gitHubProfileService;
    private readonly IDockerService _dockerService;
    private readonly ICloudflareService _cloudflareService;

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
    private string _cloudflareTestStatus = string.Empty;

    [ObservableProperty]
    private bool _isCloudflareTesting;

    public System.Collections.ObjectModel.ObservableCollection<CloudflareTunnelInfo> AvailableTunnels { get; } = new();

    [ObservableProperty]
    private CloudflareTunnelInfo? _selectedTunnel;

    [ObservableProperty]
    private string _newTunnelName = "dockermanager";

    [ObservableProperty]
    private bool _showCreateTunnelBox;

    [ObservableProperty]
    private string _dockerHostType = "Pipe";

    [ObservableProperty]
    private string _dockerPipeName = "npipe://./pipe/docker_engine";

    [ObservableProperty]
    private string _dockerTcpUrl = "tcp://192.168.1.50:2375";

    [ObservableProperty]
    private string _gitHubRepoOwner = string.Empty;

    [ObservableProperty]
    private string _gitHubRepoName = string.Empty;

    [ObservableProperty]
    private string _gitHubDeployRepo = string.Empty;

    [ObservableProperty]
    private string _gitHubBranch = "main";

    [ObservableProperty]
    private string _gitHubProfilesFolder = "profiles";

    [ObservableProperty]
    private string _gitHubToken = string.Empty;

    [ObservableProperty]
    private string _loggedInUsername = string.Empty;

    [ObservableProperty]
    private bool _isLoggedIn;

    [ObservableProperty]
    private bool _autoCheckUpdatesOnStart = true;

    [ObservableProperty]
    private bool _autoCheckImageUpdates = true;

    [ObservableProperty]
    private string _updateCheckFrequency = "Dagelijks"; // "Dagelijks", "Wekelijks", "Handmatig"

    public string[] AvailableFrequencies { get; } = new[] { "Dagelijks", "Wekelijks", "Handmatig" };

    [ObservableProperty]
    private string _selectedLanguage = "auto";

    public string[] AvailableLanguages { get; } = new[] { "Systeemtaal / System Default", "Nederlands (NL)", "English (EN)" };

    public string SelectedLanguageDisplay
    {
        get => SelectedLanguage switch
        {
            "en" => "English (EN)",
            "nl" => "Nederlands (NL)",
            _ => "Systeemtaal / System Default"
        };
        set
        {
            SelectedLanguage = value switch
            {
                "English (EN)" => "en",
                "Nederlands (NL)" => "nl",
                _ => "auto"
            };
            LocalizationService.Instance.SetLanguage(SelectedLanguage);
            OnPropertyChanged(nameof(SelectedLanguageDisplay));
        }
    }

    [ObservableProperty]
    private bool _promptToStopPreviousProfile = true;

    [ObservableProperty]
    private bool _defaultKeepRunningOnSwitch = true;

    [ObservableProperty]
    private string _localProfilesFolder = string.Empty;

    [ObservableProperty]
    private string _dockerTestStatus = string.Empty;

    [ObservableProperty]
    private bool _isDockerTesting;

    [ObservableProperty]
    private string _gitHubTestStatus = string.Empty;

    [ObservableProperty]
    private bool _isGitHubTesting;

    [ObservableProperty]
    private string _newRegistryServer = string.Empty;

    [ObservableProperty]
    private string _newRegistryUsername = string.Empty;

    [ObservableProperty]
    private string _newRegistryPassword = string.Empty;

    public System.Collections.ObjectModel.ObservableCollection<RegistryCredential> Registries { get; } = new();
    public System.Collections.ObjectModel.ObservableCollection<DockerServerEnvironment> Servers { get; } = new();

    [ObservableProperty]
    private DockerServerEnvironment? _editingServer;

    partial void OnEditingServerChanged(DockerServerEnvironment? value)
    {
        OnPropertyChanged(nameof(IsPipeSelected));
        OnPropertyChanged(nameof(IsTcpSelected));
    }

    public bool IsPipeSelected
    {
        get => EditingServer?.HostType.Equals("Pipe", StringComparison.OrdinalIgnoreCase) ?? true;
        set
        {
            if (value && EditingServer != null)
            {
                EditingServer.HostType = "Pipe";
                OnPropertyChanged(nameof(IsPipeSelected));
                OnPropertyChanged(nameof(IsTcpSelected));
            }
        }
    }

    public bool IsTcpSelected
    {
        get => EditingServer?.HostType.Equals("Tcp", StringComparison.OrdinalIgnoreCase) ?? false;
        set
        {
            if (value && EditingServer != null)
            {
                EditingServer.HostType = "Tcp";
                OnPropertyChanged(nameof(IsPipeSelected));
                OnPropertyChanged(nameof(IsTcpSelected));
            }
        }
    }

    public event Action? RequestClose;
    public event Action? RequestOpenGitHubLogin;

    public SettingsViewModel(
        ISettingsService settingsService,
        ICredentialService credentialService,
        IGitHubProfileService gitHubProfileService,
        IDockerService dockerService,
        ICloudflareService cloudflareService)
    {
        _settingsService = settingsService;
        _credentialService = credentialService;
        _gitHubProfileService = gitHubProfileService;
        _dockerService = dockerService;
        _cloudflareService = cloudflareService;

        LoadFromSettings();
    }

    public void LoadFromSettings()
    {
        var s = _settingsService.Settings;
        s.EnsureDefaultServers();
        Servers.Clear();
        foreach (var srv in s.Servers)
        {
            Servers.Add(srv.Clone());
        }
        var active = s.GetActiveServer();
        EditingServer = Servers.FirstOrDefault(x => x.Id.Equals(active.Id, StringComparison.OrdinalIgnoreCase)) ?? Servers.FirstOrDefault();

        DockerHostType = s.DockerHostType;
        DockerPipeName = s.DockerPipeName;
        DockerTcpUrl = s.DockerTcpUrl;
        GitHubRepoOwner = s.GitHubRepoOwner;
        GitHubRepoName = s.GitHubRepoName;
        GitHubDeployRepo = s.GitHubDeployRepo;
        GitHubBranch = s.GitHubBranch;
        GitHubProfilesFolder = s.GitHubProfilesFolder;
        CloudflareAccountId = s.CloudflareAccountId;
        CloudflareTunnelId = s.CloudflareTunnelId;
        CloudflareDomain = s.CloudflareDomain;
        CloudflareTunnelToken = s.CloudflareTunnelToken;
        CloudflareApiToken = _credentialService.GetCloudflareApiToken() ?? string.Empty;
        AutoCheckUpdatesOnStart = s.AutoCheckUpdatesOnStart;
        AutoCheckImageUpdates = s.AutoCheckImageUpdates;
        UpdateCheckFrequency = string.IsNullOrWhiteSpace(s.UpdateCheckFrequency) ? "Dagelijks" : s.UpdateCheckFrequency;
        SelectedLanguage = string.IsNullOrWhiteSpace(s.Language) ? "auto" : s.Language;
        OnPropertyChanged(nameof(SelectedLanguageDisplay));
        PromptToStopPreviousProfile = s.PromptToStopPreviousProfile;
        DefaultKeepRunningOnSwitch = s.DefaultKeepRunningOnSwitch;
        LocalProfilesFolder = s.LocalProfilesFolder;
        LoggedInUsername = s.LoggedInUsername;

        var token = _credentialService.GetGitHubToken();
        GitHubToken = token ?? string.Empty;
        IsLoggedIn = !string.IsNullOrWhiteSpace(token);

        RefreshRegistries();

        OnPropertyChanged(nameof(IsPipeSelected));
        OnPropertyChanged(nameof(IsTcpSelected));
    }

    public void RefreshRegistries()
    {
        Registries.Clear();
        var all = _credentialService.GetAllRegistryCredentials();

        var ghToken = _credentialService.GetGitHubToken();
        var ghUser = !string.IsNullOrWhiteSpace(_settingsService.Settings.LoggedInUsername) 
            ? _settingsService.Settings.LoggedInUsername 
            : _settingsService.Settings.GitHubRepoOwner;

        var ghcr = all.FirstOrDefault(r => r.IsGitHubRegistry);
        if (ghcr == null)
        {
            ghcr = new RegistryCredential
            {
                ServerAddress = "ghcr.io",
                Username = ghUser,
                Password = ghToken ?? string.Empty,
                DisplayName = "GitHub Container Registry (ghcr.io)"
            };
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(ghUser))
            {
                ghcr.Username = ghUser;
            }
            if (string.IsNullOrWhiteSpace(ghcr.Password) && !string.IsNullOrWhiteSpace(ghToken))
            {
                ghcr.Password = ghToken;
            }
            ghcr.DisplayName = "GitHub Container Registry (ghcr.io)";
        }
        Registries.Add(ghcr);

        foreach (var reg in all.Where(r => !r.IsGitHubRegistry))
        {
            Registries.Add(reg);
        }
    }

    [RelayCommand]
    private void ConfigureRegistry(RegistryCredential? cred)
    {
        if (cred == null || cred.IsGitHubRegistry)
        {
            OpenGitHubLogin();
        }
    }

    [RelayCommand]
    private void AddCustomRegistry()
    {
        if (string.IsNullOrWhiteSpace(NewRegistryServer)) return;

        var cred = new RegistryCredential
        {
            ServerAddress = NewRegistryServer.Trim(),
            Username = NewRegistryUsername.Trim(),
            Password = NewRegistryPassword.Trim(),
            DisplayName = $"{NewRegistryServer.Trim()} ({NewRegistryUsername.Trim()})"
        };

        _credentialService.SaveRegistryCredential(cred);
        RefreshRegistries();

        NewRegistryServer = string.Empty;
        NewRegistryUsername = string.Empty;
        NewRegistryPassword = string.Empty;
    }

    [RelayCommand]
    private void DeleteRegistry(RegistryCredential? cred)
    {
        if (cred == null) return;
        if (cred.IsGitHubRegistry)
        {
            LogoutGitHub();
            return;
        }
        _credentialService.DeleteRegistryCredential(cred.ServerAddress);
        RefreshRegistries();
    }

    [ObservableProperty]
    private string _registryTestStatus = string.Empty;

    [ObservableProperty]
    private bool _isRegistryTesting;

    [RelayCommand]
    private async Task TestRegistryAsync(RegistryCredential? cred)
    {
        if (cred == null) return;
        if (cred.IsGitHubRegistry && !cred.IsConfigured)
        {
            RegistryTestStatus = "⚠️ ghcr.io is nog niet gekoppeld. Klik op '⚙️ Koppelen' om in te loggen via GitHub.";
            return;
        }

        IsRegistryTesting = true;
        RegistryTestStatus = $"Verbinden met {cred.ServerAddress}...";
        try
        {
            var (ok, msg) = await _dockerService.TestRegistryConnectionAsync(cred);
            RegistryTestStatus = ok ? $"✅ {cred.ServerAddress}: {msg}" : $"❌ {cred.ServerAddress}: {msg}";
        }
        catch (Exception ex)
        {
            RegistryTestStatus = $"❌ Fout: {ex.Message}";
        }
        finally
        {
            IsRegistryTesting = false;
        }
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
        LoadFromSettings();
        RefreshRegistries();
    }

    [RelayCommand]
    private void AddServer()
    {
        var count = Servers.Count + 1;
        var newServer = new DockerServerEnvironment
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Name = $"Server {count}",
            HostType = "Tcp",
            TcpUrl = "tcp://100.x.y.z:2375",
            ProfilesSubfolder = $"server{count}"
        };
        Servers.Add(newServer);
        EditingServer = newServer;
    }

    [RelayCommand]
    private void DeleteServer()
    {
        if (EditingServer == null || Servers.Count <= 1) return;
        var toRemove = EditingServer;
        var idx = Servers.IndexOf(toRemove);
        Servers.Remove(toRemove);
        EditingServer = Servers[Math.Max(0, idx - 1)];
    }

    [RelayCommand]
    private void SelectPipe()
    {
        if (EditingServer != null)
        {
            EditingServer.HostType = "Pipe";
            OnPropertyChanged(nameof(IsPipeSelected));
            OnPropertyChanged(nameof(IsTcpSelected));
        }
    }

    [RelayCommand]
    private void SelectTcp()
    {
        if (EditingServer != null)
        {
            EditingServer.HostType = "Tcp";
            if (string.IsNullOrWhiteSpace(EditingServer.TcpUrl) || EditingServer.TcpUrl.Contains("192.168.1.50"))
            {
                EditingServer.TcpUrl = "tcp://100.x.y.z:2375";
            }
            OnPropertyChanged(nameof(IsPipeSelected));
            OnPropertyChanged(nameof(IsTcpSelected));
        }
    }

    [RelayCommand]
    private async Task TestDockerConnectionAsync()
    {
        if (EditingServer == null) return;
        IsDockerTesting = true;
        DockerTestStatus = "Verbinden met Docker host...";
        try
        {
            var uri = EditingServer.GetEffectiveUri();
            using var testClient = new Docker.DotNet.DockerClientConfiguration(new Uri(uri)).CreateClient();
            var version = await testClient.System.GetVersionAsync();
            DockerTestStatus = $"✅ Verbonden met {EditingServer.Name}: {version.Version} ({version.Os})";
        }
        catch (Exception ex)
        {
            DockerTestStatus = $"❌ Fout bij verbinden: {ex.Message}";
        }
        finally
        {
            IsDockerTesting = false;
        }
    }

    [RelayCommand]
    private async Task TestGitHubConnectionAsync()
    {
        IsGitHubTesting = true;
        GitHubTestStatus = "Verbinden met GitHub API...";
        try
        {
            _settingsService.Settings.GitHubRepoOwner = GitHubRepoOwner;
            _settingsService.Settings.GitHubRepoName = GitHubRepoName;

            var (ok, msg) = await _gitHubProfileService.TestGitHubConnectionAsync(GitHubToken);
            GitHubTestStatus = ok ? $"✅ {msg}" : $"❌ {msg}";
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

    partial void OnSelectedTunnelChanged(CloudflareTunnelInfo? value)
    {
        if (value != null)
        {
            CloudflareTunnelId = value.Id;
            _ = FetchTokenForTunnelAsync(value.Id);
        }
    }

    private async Task FetchTokenForTunnelAsync(string tunnelId)
    {
        try
        {
            var token = await _cloudflareService.FetchTunnelTokenAsync(
                CloudflareApiToken,
                CloudflareAccountId,
                tunnelId);

            if (!string.IsNullOrWhiteSpace(token))
            {
                CloudflareTunnelToken = token;
            }
        }
        catch { }
    }

    [RelayCommand]
    private async Task LoadCloudflareTunnelsAsync()
    {
        if (string.IsNullOrWhiteSpace(CloudflareApiToken) || string.IsNullOrWhiteSpace(CloudflareAccountId))
        {
            CloudflareTestStatus = "⚠️ Vul eerst een API Token en Account ID in om tunnels op te halen.";
            return;
        }

        IsCloudflareTesting = true;
        CloudflareTestStatus = "Tunnels ophalen van Cloudflare...";
        try
        {
            var (ok, msg, tunnels) = await _cloudflareService.ListTunnelsAsync(CloudflareApiToken, CloudflareAccountId);
            if (ok)
            {
                AvailableTunnels.Clear();
                foreach (var t in tunnels)
                {
                    AvailableTunnels.Add(t);
                }

                if (!string.IsNullOrWhiteSpace(CloudflareTunnelId))
                {
                    SelectedTunnel = AvailableTunnels.FirstOrDefault(t => t.Id.Equals(CloudflareTunnelId, StringComparison.OrdinalIgnoreCase));
                }

                if (SelectedTunnel == null && AvailableTunnels.Count > 0)
                {
                    SelectedTunnel = AvailableTunnels[0];
                }

                CloudflareTestStatus = $"✅ {tunnels.Count} tunnel(s) opgehaald!";
            }
            else
            {
                CloudflareTestStatus = $"❌ {msg}";
            }
        }
        catch (Exception ex)
        {
            CloudflareTestStatus = $"❌ Fout: {ex.Message}";
        }
        finally
        {
            IsCloudflareTesting = false;
        }
    }

    [RelayCommand]
    private void ToggleCreateTunnelBox()
    {
        ShowCreateTunnelBox = !ShowCreateTunnelBox;
    }

    [RelayCommand]
    private async Task CreateTunnelConfirmAsync()
    {
        if (string.IsNullOrWhiteSpace(NewTunnelName))
        {
            CloudflareTestStatus = "⚠️ Geef een naam op voor de nieuwe tunnel.";
            return;
        }

        IsCloudflareTesting = true;
        CloudflareTestStatus = $"Tunnel '{NewTunnelName}' aanmaken in Cloudflare...";
        try
        {
            var (ok, msg, created) = await _cloudflareService.CreateTunnelAsync(
                NewTunnelName,
                CloudflareApiToken,
                CloudflareAccountId);

            if (ok && created != null)
            {
                AvailableTunnels.Add(created);
                SelectedTunnel = created;
                CloudflareTunnelId = created.Id;
                ShowCreateTunnelBox = false;
                CloudflareTestStatus = $"🎉 {msg}";
            }
            else
            {
                CloudflareTestStatus = $"❌ {msg}";
            }
        }
        catch (Exception ex)
        {
            CloudflareTestStatus = $"❌ Fout: {ex.Message}";
        }
        finally
        {
            IsCloudflareTesting = false;
        }
    }

    [RelayCommand]
    private async Task DeleteTunnelAsync()
    {
        if (SelectedTunnel == null && string.IsNullOrWhiteSpace(CloudflareTunnelId))
        {
            CloudflareTestStatus = "⚠️ Selecteer eerst een tunnel om te verwijderen.";
            return;
        }

        var targetId = SelectedTunnel?.Id ?? CloudflareTunnelId;
        var targetName = SelectedTunnel?.Name ?? targetId;

        var confirm = System.Windows.MessageBox.Show(
            $"Weet je zeker dat je tunnel '{targetName}' wilt verwijderen uit Cloudflare?\n\nAlle gekoppelde routes voor deze tunnel worden hierdoor beëindigd.",
            "Tunnel Verwijderen",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);

        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        IsCloudflareTesting = true;
        CloudflareTestStatus = $"Tunnel '{targetName}' verwijderen uit Cloudflare...";
        try
        {
            var (ok, msg) = await _cloudflareService.DeleteTunnelAsync(
                targetId,
                CloudflareApiToken,
                CloudflareAccountId);

            if (ok)
            {
                if (SelectedTunnel != null) AvailableTunnels.Remove(SelectedTunnel);
                SelectedTunnel = AvailableTunnels.FirstOrDefault();
                CloudflareTunnelId = SelectedTunnel?.Id ?? string.Empty;
                CloudflareTunnelToken = string.Empty;
                CloudflareTestStatus = $"✅ {msg}";
            }
            else
            {
                CloudflareTestStatus = $"❌ {msg}";
            }
        }
        catch (Exception ex)
        {
            CloudflareTestStatus = $"❌ Fout: {ex.Message}";
        }
        finally
        {
            IsCloudflareTesting = false;
        }
    }

    [RelayCommand]
    private async Task TestCloudflareConnectionAsync()
    {
        IsCloudflareTesting = true;
        CloudflareTestStatus = "Verbinden met Cloudflare API...";
        try
        {
            var (ok, msg, tunnelName) = await _cloudflareService.TestConnectionAsync(
                CloudflareApiToken,
                CloudflareAccountId,
                CloudflareTunnelId);

            CloudflareTestStatus = msg;
            if (ok)
            {
                var token = await _cloudflareService.FetchTunnelTokenAsync(
                    CloudflareApiToken,
                    CloudflareAccountId,
                    CloudflareTunnelId);
                if (!string.IsNullOrWhiteSpace(token))
                {
                    CloudflareTunnelToken = token;
                    CloudflareTestStatus += " (Tunnel Token automatisch opgehaald en bewaard)";
                }
            }
        }
        catch (Exception ex)
        {
            CloudflareTestStatus = $"❌ Fout: {ex.Message}";
        }
        finally
        {
            IsCloudflareTesting = false;
        }
    }

    [ObservableProperty]
    private bool _isSaved;

    [RelayCommand]
    private void Save()
    {
        var s = _settingsService.Settings;
        s.Servers = Servers.Select(x => x.Clone()).ToList();
        if (EditingServer != null)
        {
            s.ActiveServerId = EditingServer.Id;
            s.DockerHostType = EditingServer.HostType;
            s.DockerPipeName = EditingServer.PipeName;
            s.DockerTcpUrl = EditingServer.TcpUrl;
        }
        else
        {
            s.DockerHostType = DockerHostType;
            s.DockerPipeName = DockerPipeName;
            s.DockerTcpUrl = DockerTcpUrl;
        }

        _ = _gitHubProfileService.EnsureSubfoldersAndMigrateAsync(s.Servers);
        s.GitHubRepoOwner = GitHubRepoOwner;
        s.GitHubRepoName = GitHubRepoName;
        s.GitHubDeployRepo = GitHubDeployRepo;
        s.GitHubBranch = GitHubBranch;
        s.GitHubProfilesFolder = GitHubProfilesFolder;
        s.CloudflareAccountId = CloudflareAccountId.Trim();
        s.CloudflareTunnelId = CloudflareTunnelId.Trim();
        s.CloudflareDomain = CloudflareDomain.Trim().ToLowerInvariant();
        s.CloudflareTunnelToken = CloudflareTunnelToken.Trim();
        s.AutoCheckUpdatesOnStart = AutoCheckUpdatesOnStart;
        s.AutoCheckImageUpdates = AutoCheckImageUpdates;
        s.UpdateCheckFrequency = UpdateCheckFrequency;
        s.Language = SelectedLanguage;
        LocalizationService.Instance.SetLanguage(SelectedLanguage);
        s.PromptToStopPreviousProfile = PromptToStopPreviousProfile;
        s.DefaultKeepRunningOnSwitch = DefaultKeepRunningOnSwitch;
        s.LocalProfilesFolder = LocalProfilesFolder;

        if (!string.IsNullOrWhiteSpace(GitHubToken))
        {
            _credentialService.SaveGitHubToken(GitHubToken);
            s.HasGitHubToken = true;
        }

        if (!string.IsNullOrWhiteSpace(CloudflareApiToken))
        {
            _credentialService.SaveCloudflareApiToken(CloudflareApiToken.Trim());
            s.HasCloudflareToken = true;
        }

        _settingsService.Save();
        IsSaved = true;
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private void Cancel()
    {
        IsSaved = false;
        RequestClose?.Invoke();
    }
}
