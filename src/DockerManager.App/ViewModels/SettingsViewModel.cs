using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
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
    private readonly IGitHubAuthService _gitHubAuthService;

    public ObservableCollection<SettingsTabViewModel> Tabs { get; } = new();

    [ObservableProperty]
    private SettingsTabViewModel? _selectedTab;

    public GeneralSettingsTabViewModel GeneralTab { get; private set; } = null!;

    public ObservableCollection<DockerServerEnvironment> Servers { get; } = new();

    [ObservableProperty]
    private DockerServerEnvironment? _editingServer;

    [ObservableProperty]
    private bool _isSaved;

    public event Action? RequestClose;
    public event Action? RequestOpenGitHubLogin;

    // Backward compatibility properties for tests and legacy bindings
    public string SelectedLanguageDisplay => GeneralTab?.SelectedLanguageDisplay ?? "Systeemtaal / System Default";
    public string[] AvailableLanguages => GeneralTab?.AvailableLanguages ?? new[] { "Systeemtaal / System Default", "Nederlands (NL)", "English (EN)" };
    public string[] AvailableFrequencies => GeneralTab?.AvailableFrequencies ?? new[] { "Dagelijks", "Wekelijks", "Handmatig" };
    public bool AutoCheckUpdatesOnStart { get => GeneralTab?.AutoCheckUpdatesOnStart ?? true; set { if (GeneralTab != null) GeneralTab.AutoCheckUpdatesOnStart = value; } }
    public bool AutoCheckImageUpdates { get => GeneralTab?.AutoCheckImageUpdates ?? true; set { if (GeneralTab != null) GeneralTab.AutoCheckImageUpdates = value; } }
    public string UpdateCheckFrequency { get => GeneralTab?.UpdateCheckFrequency ?? "Dagelijks"; set { if (GeneralTab != null) GeneralTab.UpdateCheckFrequency = value; } }
    public bool DefaultKeepRunningOnSwitch { get => GeneralTab?.DefaultKeepRunningOnSwitch ?? true; set { if (GeneralTab != null) GeneralTab.DefaultKeepRunningOnSwitch = value; } }
    public bool PromptToStopPreviousProfile { get => GeneralTab?.PromptToStopPreviousProfile ?? false; set { if (GeneralTab != null) GeneralTab.PromptToStopPreviousProfile = value; } }
    public bool IsLoggedIn => GeneralTab?.IsLoggedIn ?? false;
    public string LoggedInUsername => GeneralTab?.LoggedInUsername ?? string.Empty;
    public string GitHubRepoOwner { get => GeneralTab?.GitHubRepoOwner ?? string.Empty; set { if (GeneralTab != null) GeneralTab.GitHubRepoOwner = value; } }
    public string GitHubRepoName { get => GeneralTab?.GitHubRepoName ?? string.Empty; set { if (GeneralTab != null) GeneralTab.GitHubRepoName = value; } }
    public string GitHubTestStatus => GeneralTab?.GitHubTestStatus ?? string.Empty;

    public SettingsViewModel(
        ISettingsService settingsService,
        ICredentialService credentialService,
        IGitHubProfileService gitHubProfileService,
        IDockerService dockerService,
        ICloudflareService cloudflareService,
        IGitHubAuthService? gitHubAuthService = null,
        string? initialServerId = null,
        string? initialProfileName = null)
    {
        _settingsService = settingsService;
        _credentialService = credentialService;
        _gitHubProfileService = gitHubProfileService;
        _dockerService = dockerService;
        _cloudflareService = cloudflareService;
        _gitHubAuthService = gitHubAuthService ?? new GitHubAuthService();

        InitializeTabs(initialServerId, initialProfileName);
    }

    public void InitializeTabs(string? initialServerId = null, string? initialProfileName = null)
    {
        Tabs.Clear();
        Servers.Clear();

        var s = _settingsService.Settings;
        s.EnsureDefaultServers();
        foreach (var srv in s.Servers)
        {
            Servers.Add(srv.Clone());
        }

        // Tab 0: Algemeen
        GeneralTab = new GeneralSettingsTabViewModel(_settingsService, _credentialService, _gitHubAuthService, _gitHubProfileService);
        GeneralTab.RequestOpenGitHubLogin += () => RequestOpenGitHubLogin?.Invoke();
        Tabs.Add(GeneralTab);

        // Server tabs
        var allProfilesAcrossServers = new List<ProfileModel>();
        var serverProfilesMap = new Dictionary<string, List<ProfileModel>>();

        foreach (var server in Servers)
        {
            var subfolder = string.IsNullOrWhiteSpace(server.ProfilesSubfolder) ? "local" : server.ProfilesSubfolder;
            List<ProfileModel> profs;
            try
            {
                profs = _gitHubProfileService.LoadProfilesLocally(subfolder);
            }
            catch
            {
                profs = new List<ProfileModel>();
            }

            if (profs.Count == 0)
            {
                profs.Add(new ProfileModel
                {
                    Name = "Standaard",
                    Description = $"Standaard profiel voor {server.Name}"
                });
            }

            serverProfilesMap[server.Id] = profs;
            allProfilesAcrossServers.AddRange(profs);
        }

        foreach (var server in Servers)
        {
            var profs = serverProfilesMap.TryGetValue(server.Id, out var p) ? p : new List<ProfileModel>();
            var sTab = new ServerSettingsTabViewModel(
                server,
                profs,
                _credentialService,
                _cloudflareService,
                _dockerService,
                () => GetAllProfilesAcrossTabs());

            sTab.RequestDeleteServer += OnRequestDeleteServer;
            Tabs.Add(sTab);
        }

        // Select initial server tab and profile
        if (!string.IsNullOrEmpty(initialServerId))
        {
            var matchTab = Tabs.OfType<ServerSettingsTabViewModel>().FirstOrDefault(s => s.Server.Id.Equals(initialServerId, StringComparison.OrdinalIgnoreCase));
            if (matchTab != null)
            {
                SelectedTab = matchTab;
                if (!string.IsNullOrEmpty(initialProfileName))
                {
                    var matchProf = matchTab.Profiles.FirstOrDefault(p => p.Name.Equals(initialProfileName, StringComparison.OrdinalIgnoreCase));
                    if (matchProf != null) matchTab.SelectedProfile = matchProf;
                }
            }
        }

        if (SelectedTab == null)
        {
            var activeId = _settingsService.Settings.ActiveServerId;
            var activeTab = Tabs.OfType<ServerSettingsTabViewModel>().FirstOrDefault(s => s.Server.Id.Equals(activeId, StringComparison.OrdinalIgnoreCase));
            SelectedTab = (SettingsTabViewModel?)activeTab ?? GeneralTab;
        }

        EditingServer = Servers.FirstOrDefault(x => x.Id.Equals(_settingsService.Settings.ActiveServerId, StringComparison.OrdinalIgnoreCase)) ?? Servers.FirstOrDefault();
    }

    public IEnumerable<ProfileModel> GetAllProfilesAcrossTabs()
    {
        return Tabs.OfType<ServerSettingsTabViewModel>()
            .SelectMany(s => s.Profiles.Select(p => p.Profile));
    }

    [RelayCommand]
    public void AddServer()
    {
        int count = Tabs.OfType<ServerSettingsTabViewModel>().Count() + 1;
        var id = "srv_" + Guid.NewGuid().ToString("N")[..6];
        var srv = new DockerServerEnvironment
        {
            Id = id,
            Name = $"Server {count}",
            HostType = "Tcp",
            TcpUrl = "tcp://192.168.1.50:2375",
            ProfilesSubfolder = $"server_{count}"
        };

        Servers.Add(srv);

        var defaultProfile = new ProfileModel
        {
            Name = "Standaard",
            Description = $"Standaard profiel voor {srv.Name}"
        };

        var serverTab = new ServerSettingsTabViewModel(
            srv,
            new[] { defaultProfile },
            _credentialService,
            _cloudflareService,
            _dockerService,
            () => GetAllProfilesAcrossTabs());

        serverTab.RequestDeleteServer += OnRequestDeleteServer;
        Tabs.Add(serverTab);
        SelectedTab = serverTab;
    }

    private void OnRequestDeleteServer(ServerSettingsTabViewModel serverTab)
    {
        var serverCount = Tabs.OfType<ServerSettingsTabViewModel>().Count();
        if (serverCount <= 1)
        {
            MessageBox.Show("Er moet minimaal één Docker server geconfigureerd blijven.", "Kan niet verwijderen", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var result = MessageBox.Show(
            $"Weet je zeker dat je server '{serverTab.Name}' wilt verwijderen uit DockerManager?\n\nDe serverconfiguratie wordt verwijderd. De profielenmap op schijf blijft bewaard.",
            "Server Verwijderen",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            Tabs.Remove(serverTab);
            Servers.Remove(serverTab.Server);
            SelectedTab = Tabs.OfType<ServerSettingsTabViewModel>().FirstOrDefault() ?? (SettingsTabViewModel)GeneralTab;
        }
    }

    [RelayCommand]
    public async Task SaveAsync()
    {
        // 1. General settings
        GeneralTab.ApplyToSettings();

        // 2. Server settings & profiles
        var serverList = new List<DockerServerEnvironment>();
        var serverTabs = Tabs.OfType<ServerSettingsTabViewModel>().ToList();

        foreach (var tab in serverTabs)
        {
            tab.ApplyToServer();
            serverList.Add(tab.Server);

            var subfolder = string.IsNullOrWhiteSpace(tab.Server.ProfilesSubfolder) ? "local" : tab.Server.ProfilesSubfolder;

            // Save all profiles for this server
            foreach (var pVm in tab.Profiles)
            {
                _gitHubProfileService.SaveProfileLocally(pVm.Profile, subfolder);
            }

            // Delete removed profiles
            foreach (var delName in tab.DeletedProfileNames)
            {
                _gitHubProfileService.DeleteProfileLocally(delName, subfolder);
            }
        }

        _settingsService.Settings.Servers = serverList;

        if (!serverList.Any(s => s.Id.Equals(_settingsService.Settings.ActiveServerId, StringComparison.OrdinalIgnoreCase)))
        {
            _settingsService.Settings.ActiveServerId = serverList.FirstOrDefault()?.Id ?? "local";
        }

        _ = _gitHubProfileService.EnsureSubfoldersAndMigrateAsync(serverList);
        _settingsService.Save();

        LocalizationService.Instance.SetLanguage(_settingsService.Settings.Language);

        IsSaved = true;
        RequestClose?.Invoke();
    }

    [RelayCommand]
    public void Cancel()
    {
        IsSaved = false;
        RequestClose?.Invoke();
    }

    public void LoadFromSettings()
    {
        InitializeTabs();
    }
}
