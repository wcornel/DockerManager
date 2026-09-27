using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DockerManager.App.Models;
using DockerManager.App.Services;

namespace DockerManager.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly ISettingsService _settingsService;
    private readonly ICredentialService _credentialService;
    private readonly IGitHubProfileService _gitHubProfileService;
    private readonly IDockerService _dockerService;
    private readonly ICloudflareService? _cloudflareService;
    private readonly DispatcherTimer _pollTimer;

    public Func<string, string, bool>? ConfirmPrompt { get; set; }

    public ObservableCollection<DockerServerEnvironment> Servers { get; } = new();

    [ObservableProperty]
    private DockerServerEnvironment? _selectedServer;

    partial void OnSelectedServerChanged(DockerServerEnvironment? value)
    {
        if (value == null) return;
        _settingsService.Settings.ActiveServerId = value.Id;
        _settingsService.Settings.DockerHostType = value.HostType;
        _settingsService.Settings.DockerPipeName = value.PipeName;
        _settingsService.Settings.DockerTcpUrl = value.TcpUrl;
        _settingsService.Save();

        _ = CheckDockerStatusAsync();
        _ = LoadProfilesAsync(forceRefresh: false);
    }

    [ObservableProperty]
    private ProfileModel? _selectedProfile;

    [ObservableProperty]
    private bool _isDockerConnected;

    [ObservableProperty]
    private string _dockerStatusText = LocalizationService.Instance.Get("Docker_Checking");

    [ObservableProperty]
    private string _dockerDiagnosisTip = string.Empty;

    [ObservableProperty]
    private bool _isDockerDesktopInstalled;

    [ObservableProperty]
    private bool _isLaunchingDocker;

    [ObservableProperty]
    private bool _isSyncing;

    private DateTime? _lastSyncTime;

    [ObservableProperty]
    private string _syncStatusText = LocalizationService.Instance.Get("General_Ready");

    [ObservableProperty]
    private string _statusNotification = string.Empty;

    [ObservableProperty]
    private bool _isGitHubLoggedIn;

    [ObservableProperty]
    private string _gitHubButtonText = LocalizationService.Instance.Get("GitHub_SignIn");

    public string ContainersCountText => LocalizationService.Instance.Get("Toolbar_ContainersCount", ServiceCards.Count);

    [ObservableProperty]
    private bool _hasExternalProfileChanges;

    [ObservableProperty]
    private string _updateAllButtonText = "🚀 Update Alles";

    [ObservableProperty]
    private string _updateAllButtonBackground = "#2563EB";

    private FileSystemWatcher? _profilesWatcher;

    public ObservableCollection<ProfileModel> Profiles { get; } = new();
    public ObservableCollection<ServiceCardViewModel> ServiceCards { get; } = new();
    public ICollectionView FilteredServiceCards { get; }

    [ObservableProperty]
    private string _searchText = string.Empty;

    partial void OnSearchTextChanged(string value)
    {
        FilteredServiceCards.Refresh();
    }

    private bool FilterServiceCards(object item)
    {
        if (item is not ServiceCardViewModel card) return false;
        if (string.IsNullOrWhiteSpace(SearchText)) return true;

        var term = SearchText.Trim();
        if (card.Service.DisplayName?.Contains(term, StringComparison.OrdinalIgnoreCase) == true) return true;
        if (card.ContainerName?.Contains(term, StringComparison.OrdinalIgnoreCase) == true) return true;
        if (card.Service.Image?.Contains(term, StringComparison.OrdinalIgnoreCase) == true) return true;
        if (card.PortsSummary?.Contains(term, StringComparison.OrdinalIgnoreCase) == true) return true;

        return false;
    }

    [RelayCommand]
    private void ClearSearch()
    {
        SearchText = string.Empty;
    }

    public event Action<ServiceCardViewModel>? RequestOpenLogs;
    public event Action<ServiceCardViewModel>? RequestEditService;
    public event Action? RequestAddNewService;
    public event Action? RequestOpenSettings;
    public event Action? RequestOpenGitHubLogin;
    public event Action<ProfileModel>? RequestOpenProfileSettings;
    public event Func<ProfileModel, Task<bool>>? RequestConfirmDeleteProfile;
    public event Func<ProfileModel, Task>? RequestBackupVolumes;
    public event Func<Task>? RequestRestoreBackup;
    public event Action? RequestCheckPortConflicts;
    public event Func<string, Task<bool?>>? RequestSwitchProfileConfirmation;

    public MainViewModel(
        ISettingsService settingsService,
        ICredentialService credentialService,
        IGitHubProfileService gitHubProfileService,
        IDockerService dockerService,
        ICloudflareService? cloudflareService = null)
    {
        _settingsService = settingsService;
        _credentialService = credentialService;
        _gitHubProfileService = gitHubProfileService;
        _dockerService = dockerService;
        _cloudflareService = cloudflareService;

        FilteredServiceCards = CollectionViewSource.GetDefaultView(ServiceCards);
        FilteredServiceCards.Filter = FilterServiceCards;

        ServiceCards.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ContainersCountText));
        LocalizationService.Instance.LanguageChanged += OnLanguageChanged;
        RefreshAuthStatus();

        _pollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(4)
        };
        _pollTimer.Tick += async (_, _) => await OnPollTimerTickAsync();
    }

    private void OnLanguageChanged()
    {
        RefreshAuthStatus();
        OnPropertyChanged(nameof(ContainersCountText));
        if (_lastSyncTime.HasValue)
        {
            SyncStatusText = LocalizationService.Instance.Get("Status_LastSync", _lastSyncTime.Value.ToString("HH:mm:ss"));
        }
        else
        {
            SyncStatusText = LocalizationService.Instance.Get("General_Ready");
        }
        _ = CheckDockerStatusAsync();
    }

    public void LoadServersFromSettings()
    {
        _settingsService.Settings.EnsureDefaultServers();
        Servers.Clear();
        foreach (var s in _settingsService.Settings.Servers)
        {
            Servers.Add(s);
        }

        var active = _settingsService.Settings.GetActiveServer();
        SelectedServer = Servers.FirstOrDefault(s => s.Id.Equals(active.Id, StringComparison.OrdinalIgnoreCase)) ?? Servers.FirstOrDefault();
    }

    public async Task InitializeAsync()
    {
        RefreshAuthStatus();
        LoadServersFromSettings();
        await CheckDockerStatusAsync();
        await LoadProfilesAsync(forceRefresh: false);

        SetupProfilesFileWatcher();
        _pollTimer.Start();
    }

    public void RefreshAuthStatus()
    {
        var token = _credentialService.GetGitHubToken();
        var username = _settingsService.Settings.LoggedInUsername;
        IsGitHubLoggedIn = !string.IsNullOrWhiteSpace(token);
        var loc = LocalizationService.Instance;
        GitHubButtonText = IsGitHubLoggedIn 
            ? (!string.IsNullOrWhiteSpace(username) ? $"@{username} ✅" : loc.Get("GitHub_LoggedIn")) 
            : loc.Get("GitHub_SignIn");
    }

    public async Task CheckDockerStatusAsync()
    {
        try
        {
            var diagnosis = await _dockerService.DiagnoseDockerStartupAsync();
            IsDockerConnected = diagnosis.IsConnected;
            IsDockerDesktopInstalled = diagnosis.IsDockerDesktopInstalled;
            DockerDiagnosisTip = diagnosis.DetailedTip;
            DockerStatusText = diagnosis.DiagnosisSummary;
        }
        catch (Exception ex)
        {
            IsDockerConnected = false;
            DockerStatusText = $"Docker fout: {ex.Message}";
            DockerDiagnosisTip = "Controleer of de Docker service draait en bereikbaar is.";
        }
    }

    [RelayCommand]
    private async Task LaunchDockerDesktopAsync()
    {
        IsLaunchingDocker = true;
        StatusNotification = "🚀 Bezig met opstarten van Docker Desktop...";
        var launched = await _dockerService.TryLaunchDockerDesktopAsync();
        if (launched)
        {
            StatusNotification = "Docker Desktop wordt opgestart. Even geduld...";
            // Wacht 5 seconden en check opnieuw
            await Task.Delay(5000);
            await CheckDockerStatusAsync();
        }
        else
        {
            StatusNotification = "Kon Docker Desktop niet automatisch starten. Start de applicatie handmatig via het Windows Startmenu.";
        }
        IsLaunchingDocker = false;
    }

    public async Task LoadProfilesAsync(bool forceRefresh)
    {
        IsSyncing = true;
        SyncStatusText = forceRefresh ? "Synchroniseren met GitHub..." : "Profielen laden...";

        try
        {
            var currentServer = SelectedServer ?? _settingsService.Settings.GetActiveServer();
            await _gitHubProfileService.EnsureSubfoldersAndMigrateAsync(_settingsService.Settings.Servers);

            var list = await _gitHubProfileService.LoadProfilesAsync(currentServer.ProfilesSubfolder, forceRefresh);
            Profiles.Clear();
            foreach (var p in list)
            {
                Profiles.Add(p);
            }

            // Restore last active profile for this server or select first
            var last = !string.IsNullOrWhiteSpace(currentServer.LastActiveProfile)
                ? currentServer.LastActiveProfile
                : _settingsService.Settings.LastActiveProfile;

            var match = Profiles.FirstOrDefault(p => p.Name.Equals(last, StringComparison.OrdinalIgnoreCase)) ?? Profiles.FirstOrDefault();
            
            if (match != null)
            {
                SelectedProfile = match;
            }

            _lastSyncTime = DateTime.Now;
            SyncStatusText = LocalizationService.Instance.Get("Status_LastSync", _lastSyncTime.Value.ToString("HH:mm:ss"));
        }
        catch (Exception ex)
        {
            SyncStatusText = $"Sync fout: {ex.Message}";
        }
        finally
        {
            IsSyncing = false;
        }
    }

    partial void OnSelectedProfileChanged(ProfileModel? oldValue, ProfileModel? newValue)
    {
        if (newValue == null) return;

        if (SelectedServer != null)
        {
            SelectedServer.LastActiveProfile = newValue.Name;
        }
        _settingsService.Settings.LastActiveProfile = newValue.Name;
        _settingsService.Save();

        if (oldValue != null && oldValue.Name != newValue.Name && RequestSwitchProfileConfirmation != null)
        {
            _ = HandleProfileSwitchAsync(oldValue);
            return;
        }

        RebuildServiceCards();
        _ = RefreshAllCardStatusesAsync();
    }

    private async Task HandleProfileSwitchAsync(ProfileModel oldProfile)
    {
        bool shouldStop = oldProfile.AutoStopPreviousOnSwitch;
        if (!shouldStop)
        {
            if (!_settingsService.Settings.PromptToStopPreviousProfile)
            {
                shouldStop = !_settingsService.Settings.DefaultKeepRunningOnSwitch;
            }
            else if (RequestSwitchProfileConfirmation != null)
            {
                var res = await RequestSwitchProfileConfirmation.Invoke(oldProfile.Name);
                if (res == true)
                {
                    shouldStop = true;
                }
            }
        }

        if (shouldStop)
        {
            StatusNotification = $"⏹ Containers van vorig profiel '{oldProfile.Name}' stoppen...";
            foreach (var svc in oldProfile.Services)
            {
                var cName = string.IsNullOrWhiteSpace(svc.ContainerName) ? $"{oldProfile.Name.ToLowerInvariant()}_{svc.Id}" : svc.ContainerName;
                try
                {
                    await _dockerService.StopContainerAsync(cName);
                }
                catch { }
            }
        }

        RebuildServiceCards();
        await RefreshAllCardStatusesAsync();
    }

    public void RebuildServiceCards()
    {
        ServiceCards.Clear();
        if (SelectedProfile == null) return;

        foreach (var svc in SelectedProfile.Services)
        {
            var card = new ServiceCardViewModel(svc, SelectedProfile.Name, _dockerService, _settingsService, SelectedProfile);
            card.RequestOpenLogs += s => RequestOpenLogs?.Invoke(s);
            card.RequestEdit += s => RequestEditService?.Invoke(s);
            card.RequestDeleteFromProfile += OnDeleteServiceCard;
            card.RequestSaveProfile += () => _ = SaveCurrentProfileAsync();
            ServiceCards.Add(card);
        }
    }

    [RelayCommand]
    private void OpenProfileSettings()
    {
        if (SelectedProfile == null) return;
        RequestOpenProfileSettings?.Invoke(SelectedProfile);
    }

    [RelayCommand]
    private async Task DeleteCurrentProfileAsync()
    {
        if (SelectedProfile == null) return;
        if (RequestConfirmDeleteProfile != null)
        {
            var confirmed = await RequestConfirmDeleteProfile(SelectedProfile);
            if (!confirmed) return;
        }

        var profileToDelete = SelectedProfile;
        var subfolder = SelectedServer?.ProfilesSubfolder ?? _settingsService.Settings.GetActiveServer().ProfilesSubfolder;

        foreach (var svc in profileToDelete.Services)
        {
            var cName = string.IsNullOrWhiteSpace(svc.ContainerName) ? svc.Id : svc.ContainerName;
            try
            {
                await _dockerService.StopContainerAsync(cName);
            }
            catch { }
        }

        await _gitHubProfileService.DeleteProfileLocallyAsync(profileToDelete.Name, subfolder);

        Profiles.Remove(profileToDelete);
        SelectedProfile = Profiles.FirstOrDefault();
        StatusNotification = $"🗑️ Profiel '{profileToDelete.Name}' is verwijderd.";
    }

    private void OnDeleteServiceCard(ServiceCardViewModel card)
    {
        if (SelectedProfile == null) return;
        if (!AskConfirmation(
            $"Weet je zeker dat je container '{card.Service.DisplayName}' wilt verwijderen uit profiel '{SelectedProfile.Name}'?\n\nDe container wordt gestopt en verwijderd uit Docker. Volume-bestanden op schijf blijven behouden.",
            "Container Verwijderen"))
        {
            return;
        }

        var cfHost = card.Service.CloudflareHostname;
        if (string.IsNullOrWhiteSpace(cfHost) && card.Service.Labels != null && card.Service.Labels.TryGetValue("cloudflare.tunnel.hostname", out var lblHost))
        {
            cfHost = lblHost;
        }

        if (!string.IsNullOrWhiteSpace(cfHost) && _cloudflareService != null)
        {
            var cleanHost = cfHost.Replace("https://", "").Replace("http://", "").Trim('/');
            if (AskConfirmation(
                $"Deze container heeft een actieve Cloudflare koppeling ({cleanHost}).\n\nWil je de Cloudflare verwijzing (DNS-record en tunnelkoppeling) ook direct verwijderen uit Cloudflare?",
                "Cloudflare Verwijzing Verwijderen"))
            {
                var cfConfig = SelectedProfile.Cloudflare;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _cloudflareService.UnregisterSubdomainAsync(cleanHost, cfConfig);
                    }
                    catch { }
                });
            }
        }

        _ = _dockerService.RemoveContainerAsync(card.ContainerName, removeVolumes: false);
        SelectedProfile.Services.Remove(card.Service);
        ServiceCards.Remove(card);
        _ = SaveCurrentProfileAsync();
        StatusNotification = $"🗑️ Container '{card.Service.DisplayName}' verwijderd uit profiel.";
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

    public async Task RefreshAllCardStatusesAsync()
    {
        foreach (var card in ServiceCards.ToList())
        {
            await card.RefreshStatusAsync();
        }
    }

    private DateTime _lastUpdateCheckRun = DateTime.MinValue;

    private async Task OnPollTimerTickAsync()
    {
        await CheckDockerStatusAsync();
        if (IsDockerConnected)
        {
            await RefreshAllCardStatusesAsync();
            await CheckPeriodicImageUpdatesAsync();
        }
    }

    private async Task CheckPeriodicImageUpdatesAsync()
    {
        var settings = _settingsService.Settings;
        if (!settings.AutoCheckImageUpdates) return;
        if (settings.UpdateCheckFrequency.Equals("Handmatig", StringComparison.OrdinalIgnoreCase)) return;

        var interval = settings.UpdateCheckFrequency.Equals("Wekelijks", StringComparison.OrdinalIgnoreCase) 
            ? TimeSpan.FromDays(7) 
            : TimeSpan.FromDays(1);

        var lastCheck = settings.LastUpdateCheckUtc ?? DateTime.MinValue;
        if (DateTime.UtcNow - lastCheck >= interval && DateTime.UtcNow - _lastUpdateCheckRun >= TimeSpan.FromMinutes(30))
        {
            _lastUpdateCheckRun = DateTime.UtcNow;
            settings.LastUpdateCheckUtc = DateTime.UtcNow;
            _settingsService.Save();

            StatusNotification = $"🔔 Periodieke controle voltooid ({settings.UpdateCheckFrequency.ToLowerInvariant()}). Klik op 'Update Alles' om de nieuwste versies te laden.";
        }
        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task SyncWithGitHubAsync()
    {
        await LoadProfilesAsync(forceRefresh: true);
    }

    [RelayCommand]
    private void OpenSettings()
    {
        RequestOpenSettings?.Invoke();
    }

    [RelayCommand]
    private void OpenGitHubLogin()
    {
        RequestOpenGitHubLogin?.Invoke();
    }

    [RelayCommand]
    private void AddNewService()
    {
        RequestAddNewService?.Invoke();
    }

    [RelayCommand]
    private async Task StartAllAsync()
    {
        foreach (var card in ServiceCards.ToList())
        {
            if (card.IsStopped && !card.IsBusy)
            {
                _ = card.StartCommand.ExecuteAsync(null);
            }
        }
        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task StopAllAsync()
    {
        foreach (var card in ServiceCards.ToList())
        {
            if (card.IsRunning && !card.IsBusy)
            {
                _ = card.StopCommand.ExecuteAsync(null);
            }
        }
        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task RestartAllAsync()
    {
        foreach (var card in ServiceCards.ToList())
        {
            if (!card.IsBusy)
            {
                _ = card.RestartCommand.ExecuteAsync(null);
            }
        }
        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task UpdateAllAsync()
    {
        foreach (var card in ServiceCards.ToList())
        {
            if (!card.IsBusy)
            {
                _ = card.SafeUpdateCommand.ExecuteAsync(null);
            }
        }
        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task BackupVolumesAsync()
    {
        if (SelectedProfile == null) return;
        if (RequestBackupVolumes != null)
        {
            await RequestBackupVolumes.Invoke(SelectedProfile);
        }
    }

    [RelayCommand]
    private async Task RestoreBackupAsync()
    {
        if (RequestRestoreBackup != null)
        {
            await RequestRestoreBackup.Invoke();
        }
    }

    [RelayCommand]
    private async Task PruneDockerAsync()
    {
        StatusNotification = "🧹 Docker schijfruimte opschonen (dangling images & cache)...";
        try
        {
            var (reclaimed, summary) = await _dockerService.PruneUnusedResourcesAsync();
            var mb = reclaimed / (1024.0 * 1024.0);
            StatusNotification = $"✅ Opschonen voltooid: {summary} ({mb:F1} MB vrijgemaakt)";
        }
        catch (Exception ex)
        {
            StatusNotification = $"❌ Fout bij opschonen: {ex.Message}";
        }
    }

    [RelayCommand]
    private void CheckPortConflicts()
    {
        RequestCheckPortConflicts?.Invoke();
    }

    private DateTime _lastHandledChange = DateTime.MinValue;

    public async Task SaveCurrentProfileAsync()
    {
        if (SelectedProfile == null) return;
        _lastHandledChange = DateTime.UtcNow.AddSeconds(2);
        var subfolder = SelectedServer?.ProfilesSubfolder ?? _settingsService.Settings.GetActiveServer().ProfilesSubfolder;
        await _gitHubProfileService.SaveProfileLocallyAsync(SelectedProfile, subfolder);
    }

    [RelayCommand]
    private async Task ReloadProfilesAsync()
    {
        HasExternalProfileChanges = false;
        UpdateAllButtonText = "🚀 Update Alles";
        UpdateAllButtonBackground = "#2563EB";
        _lastHandledChange = DateTime.UtcNow.AddSeconds(2);
        await LoadProfilesAsync(forceRefresh: false);
        StatusNotification = $"✅ Profiel '{SelectedProfile?.Name}' succesvol herladen!";
    }

    [RelayCommand]
    private void DismissProfileChangesAlert()
    {
        HasExternalProfileChanges = false;
        UpdateAllButtonText = "🚀 Update Alles";
        UpdateAllButtonBackground = "#2563EB";
        _lastHandledChange = DateTime.UtcNow.AddSeconds(2);
    }

    private void SetupProfilesFileWatcher()
    {
        try
        {
            var folder = _settingsService.Settings.LocalProfilesFolder;
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "profiles");
            }

            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
            }

            _profilesWatcher?.Dispose();
            _profilesWatcher = new FileSystemWatcher(folder)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                Filter = "*.*",
                IncludeSubdirectories = true,
                EnableRaisingEvents = true
            };

            _profilesWatcher.Changed += OnProfileFileWatcherEvent;
            _profilesWatcher.Created += OnProfileFileWatcherEvent;
            _profilesWatcher.Renamed += OnProfileFileWatcherEvent;
        }
        catch { }
    }

    private void OnProfileFileWatcherEvent(object sender, FileSystemEventArgs e)
    {
        var ext = Path.GetExtension(e.Name)?.ToLowerInvariant();
        if (ext != ".json" && ext != ".yaml" && ext != ".yml") return;

        if ((DateTime.UtcNow - _lastHandledChange).TotalSeconds < 2) return;

        var fileNameWithoutExt = Path.GetFileNameWithoutExtension(e.Name);
        var currentProfileName = SelectedProfile?.Name ?? "";

        if (string.Equals(fileNameWithoutExt, currentProfileName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(e.Name, $"{currentProfileName}.json", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(e.Name, $"{currentProfileName.ToLowerInvariant()}.json", StringComparison.OrdinalIgnoreCase))
        {
            Application.Current?.Dispatcher?.Invoke(() =>
            {
                if ((DateTime.UtcNow - _lastHandledChange).TotalSeconds < 2) return;
                _lastHandledChange = DateTime.UtcNow;

                HasExternalProfileChanges = true;
                UpdateAllButtonText = "🔔 Update Beschikbaar";
                UpdateAllButtonBackground = "#F59E0B";
                StatusNotification = $"🔔 Profiel '{currentProfileName}' is gewijzigd op schijf! Klik op 'Nu Herladen & Toepassen'.";
            });
        }
    }
}
