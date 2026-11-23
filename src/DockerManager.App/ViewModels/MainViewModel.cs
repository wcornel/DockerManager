using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DockerManager.App.Helpers;
using DockerManager.App.Models;
using DockerManager.App.Services;

namespace DockerManager.App.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly ISettingsService _settingsService;
    private readonly ICredentialService _credentialService;
    private readonly IGitHubProfileService _gitHubProfileService;
    private readonly IDockerService _dockerService;
    private readonly ICloudflareService? _cloudflareService;
    private readonly DispatcherTimer _pollTimer;
    private readonly SemaphoreSlim _loadProfilesLock = new(1, 1);
    private bool _suppressServerChangedReload;
    private bool _suppressProfileSwitchPrompt;
    private bool _revertingProfileSelection;
    private int _isPolling;
    private bool _disposed;

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

        if (_suppressServerChangedReload) return;

        CheckDockerStatusAsync().SafeFireAndForget(nameof(CheckDockerStatusAsync));
        LoadProfilesAsync(forceRefresh: false).SafeFireAndForget(nameof(LoadProfilesAsync));
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

    public string ContainersCountText => LocalizationService.Instance.Get("Toolbar_ContainersCount", ServiceCards.Count);

    [ObservableProperty]
    private bool _hasExternalProfileChanges;

    [ObservableProperty]
    private string _updateAllButtonText = LocalizationService.Instance.Get("Tray_UpdateAll");

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
        _pollTimer.Tick += OnPollTimerTick;
    }

    private async void OnPollTimerTick(object? sender, EventArgs e)
    {
        await OnPollTimerTickAsync();
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
        _suppressServerChangedReload = true;
        try
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
        finally
        {
            _suppressServerChangedReload = false;
        }
    }

    public async Task InitializeAsync()
    {
        RefreshAuthStatus();
        LoadServersFromSettings();
        await CheckDockerStatusAsync();
        await LoadProfilesAsync(forceRefresh: false);

        if (IsDockerConnected)
        {
            await AutoStartServicesAsync();
        }

        SetupProfilesFileWatcher();
        _pollTimer.Start();
    }

    public async Task AutoStartServicesAsync()
    {
        var autoStartCandidates = ServiceCards.Where(c => c.Service.AutoStart).ToList();
        if (autoStartCandidates.Count == 0) return;

        await RefreshAllCardStatusesAsync();

        var toStart = ServiceCards
            .Where(c => c.Service.AutoStart && c.IsStopped && !c.IsBusy)
            .ToList();
        if (toStart.Count == 0) return;

        var waves = GroupCardsIntoStartWaves(toStart);
        foreach (var wave in waves)
        {
            var tasks = new List<Task>();
            foreach (var card in wave)
            {
                if (card.IsStopped && !card.IsBusy)
                {
                    tasks.Add(card.StartCommand.ExecuteAsync(null));
                }
            }

            if (tasks.Count > 0)
            {
                await Task.WhenAll(tasks);
            }
        }
    }

    public void RefreshAuthStatus()
    {
        var token = _credentialService.GetGitHubToken();
        IsGitHubLoggedIn = !string.IsNullOrWhiteSpace(token);
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
            var loc = LocalizationService.Instance;
            IsDockerConnected = false;
            DockerStatusText = loc.Get("Main_DockerError", ex.Message);
            DockerDiagnosisTip = loc.Get("Main_DockerErrorTip");
        }
    }

    [RelayCommand]
    private async Task LaunchDockerDesktopAsync()
    {
        var loc = LocalizationService.Instance;
        IsLaunchingDocker = true;
        try
        {
            StatusNotification = loc.Get("Docker_LaunchingDesktop");
            var launched = await _dockerService.TryLaunchDockerDesktopAsync();
            if (launched)
            {
                StatusNotification = loc.Get("Docker_Starting");
                // Wacht 5 seconden en check opnieuw
                await Task.Delay(5000);
                await CheckDockerStatusAsync();
            }
            else
            {
                StatusNotification = loc.Get("Docker_StartFailed");
            }
        }
        finally
        {
            IsLaunchingDocker = false;
        }
    }

    public async Task LoadProfilesAsync(bool forceRefresh)
    {
        await _loadProfilesLock.WaitAsync();
        IsSyncing = true;
        SyncStatusText = LocalizationService.Instance.Get(forceRefresh ? "Main_SyncingProfiles" : "Main_LoadingProfiles");

        try
        {
            var currentServer = SelectedServer ?? _settingsService.Settings.GetActiveServer();
            await _gitHubProfileService.EnsureSubfoldersAndMigrateAsync(_settingsService.Settings.Servers);

            var list = await _gitHubProfileService.LoadProfilesAsync(currentServer.ProfilesSubfolder, forceRefresh);

            _suppressProfileSwitchPrompt = true;
            try
            {
                SelectedProfile = null;
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
            }
            finally
            {
                _suppressProfileSwitchPrompt = false;
            }

            _lastSyncTime = DateTime.Now;
            SyncStatusText = LocalizationService.Instance.Get("Status_LastSync", _lastSyncTime.Value.ToString("HH:mm:ss"));
        }
        catch (Exception ex)
        {
            SyncStatusText = LocalizationService.Instance.Get("Main_SyncError", ex.Message);
        }
        finally
        {
            IsSyncing = false;
            _loadProfilesLock.Release();
        }
    }

    partial void OnSelectedProfileChanged(ProfileModel? oldValue, ProfileModel? newValue)
    {
        if (_revertingProfileSelection) return;

        if (newValue == null)
        {
            RebuildServiceCards();
            return;
        }

        if (!_suppressProfileSwitchPrompt &&
            oldValue != null &&
            !ReferenceEquals(oldValue, newValue) &&
            !string.Equals(oldValue.Name, newValue.Name, StringComparison.OrdinalIgnoreCase) &&
            (oldValue.AutoStopPreviousOnSwitch ||
             !_settingsService.Settings.PromptToStopPreviousProfile ||
             RequestSwitchProfileConfirmation != null))
        {
            HandleProfileSwitchAsync(oldValue, newValue).SafeFireAndForget(nameof(HandleProfileSwitchAsync));
            return;
        }

        if (SelectedServer != null)
        {
            SelectedServer.LastActiveProfile = newValue.Name;
        }
        _settingsService.Settings.LastActiveProfile = newValue.Name;
        _settingsService.Save();

        RebuildServiceCards();
        _ = RefreshAllCardStatusesAsync();
    }

    private async Task HandleProfileSwitchAsync(ProfileModel oldProfile, ProfileModel newProfile)
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
                if (res == null)
                {
                    _revertingProfileSelection = true;
                    try
                    {
                        SelectedProfile = oldProfile;
                    }
                    finally
                    {
                        _revertingProfileSelection = false;
                    }
                    return;
                }

                shouldStop = res.Value;
            }
        }

        if (SelectedServer != null)
        {
            SelectedServer.LastActiveProfile = newProfile.Name;
        }
        _settingsService.Settings.LastActiveProfile = newProfile.Name;
        _settingsService.Save();

        if (shouldStop)
        {
            StatusNotification = LocalizationService.Instance.Get("Main_StoppingPrevProfile", oldProfile.Name);
            foreach (var svc in oldProfile.Services)
            {
                var cName = svc.GetEffectiveContainerName();
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

    [RelayCommand]
    private async Task RetryDockerConnectionAsync()
    {
        await CheckDockerStatusAsync();
        if (IsDockerConnected)
        {
            await RefreshAllCardStatusesAsync();
        }
    }

    public void RebuildServiceCards()
    {
        foreach (var c in ServiceCards)
        {
            c.Dispose();
        }
        ServiceCards.Clear();
        if (SelectedProfile == null) return;

        if (SelectedProfile.Registry is { } reg && !string.IsNullOrWhiteSpace(reg.Password) && !string.IsNullOrWhiteSpace(reg.Server))
        {
            _credentialService.SaveRegistryCredential(new RegistryCredential
            {
                ServerAddress = reg.Server,
                Username = !string.IsNullOrWhiteSpace(reg.Username) ? reg.Username : (!string.IsNullOrWhiteSpace(reg.Namespace) ? reg.Namespace : "User"),
                Password = reg.Password,
                DisplayName = $"{reg.Server} ({SelectedProfile.Name})"
            });
        }

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
            var cName = svc.GetEffectiveContainerName();
            try
            {
                await _dockerService.StopContainerAsync(cName);
            }
            catch { }
        }

        await _gitHubProfileService.DeleteProfileLocallyAsync(profileToDelete.Name, subfolder);

        _suppressProfileSwitchPrompt = true;
        try
        {
            Profiles.Remove(profileToDelete);
            SelectedProfile = Profiles.FirstOrDefault();
            if (SelectedProfile == null)
            {
                RebuildServiceCards();
            }
        }
        finally
        {
            _suppressProfileSwitchPrompt = false;
        }
        StatusNotification = LocalizationService.Instance.Get("Main_ProfileDeleted", profileToDelete.Name);
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
        card.Dispose();
        _ = SaveCurrentProfileAsync();
        StatusNotification = LocalizationService.Instance.Get("Main_ContainerRemoved", card.Service.DisplayName);
    }

    private bool AskConfirmation(string message, string title)
    {
        if (ConfirmPrompt != null)
        {
            return ConfirmPrompt(message, title);
        }

        return WpfDialogService.Instance.Confirm(message, title);
    }

    private int _pollTicksCount;

    public async Task RefreshAllCardStatusesAsync()
    {
        _pollTicksCount++;
        bool shouldFetchStats = (_pollTicksCount % 3 == 0);

        List<ContainerRuntimeInfo>? allContainers = null;
        try
        {
            allContainers = await _dockerService.GetAllContainersAsync();
        }
        catch { }

        Dictionary<string, ContainerRuntimeInfo>? containerMap = null;
        if (allContainers != null)
        {
            containerMap = new Dictionary<string, ContainerRuntimeInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in allContainers)
            {
                if (!string.IsNullOrWhiteSpace(c.ContainerName))
                {
                    containerMap.TryAdd(c.ContainerName, c);
                }
            }
        }

        foreach (var card in ServiceCards.ToList())
        {
            ContainerRuntimeInfo? preFetched = null;
            containerMap?.TryGetValue(card.ContainerName, out preFetched);
            await card.RefreshStatusAsync(preFetched, shouldFetchStats);
        }
    }

    private DateTime _lastUpdateCheckRun = DateTime.MinValue;

    private async Task OnPollTimerTickAsync()
    {
        if (_disposed) return;
        if (Interlocked.CompareExchange(ref _isPolling, 1, 0) != 0) return;

        try
        {
            await CheckDockerStatusAsync();
            if (IsDockerConnected && !_disposed)
            {
                await RefreshAllCardStatusesAsync();
                await CheckPeriodicImageUpdatesAsync();
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogWarning(nameof(MainViewModel), "Background poll tick failed", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _isPolling, 0);
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

            int updatesCount = 0;
            foreach (var card in ServiceCards.ToList())
            {
                var hasUpdate = await _dockerService.CheckImageUpdateAvailableAsync(card.Service, SelectedProfile?.Registry);
                card.HasUpdateAvailable = hasUpdate;
                if (hasUpdate) updatesCount++;
            }

            if (updatesCount > 0)
            {
                StatusNotification = LocalizationService.Instance.Get("Main_UpdatesFound", updatesCount);
            }
        }
    }

    [RelayCommand]
    private async Task SyncWithGitHubAsync()
    {
        HasExternalProfileChanges = false;
        UpdateAllButtonText = LocalizationService.Instance.Get("Tray_UpdateAll");
        UpdateAllButtonBackground = "#2563EB";
        _lastHandledChange = DateTime.UtcNow.AddSeconds(2);
        await LoadProfilesAsync(forceRefresh: true);
        StatusNotification = LocalizationService.Instance.Get("Main_ProfilesSynced", Profiles.Count);
    }

    [RelayCommand]
    private void OpenSettings()
    {
        RequestOpenSettings?.Invoke();
    }

    [RelayCommand]
    private void AddNewService()
    {
        RequestAddNewService?.Invoke();
    }

    public static List<ServiceCardViewModel> SortCardsByDependencies(IEnumerable<ServiceCardViewModel> cards)
    {
        var cardList = cards.Where(c => c != null).ToList();
        var cardMap = BuildServiceCardMap(cardList);
        var result = new List<ServiceCardViewModel>();
        var visited = new HashSet<ServiceCardViewModel>(ReferenceEqualityComparer.Instance);
        var visiting = new HashSet<ServiceCardViewModel>(ReferenceEqualityComparer.Instance);

        void Visit(ServiceCardViewModel card)
        {
            if (visited.Contains(card) || visiting.Contains(card)) return;
            visiting.Add(card);

            if (card.Service.DependsOn != null)
            {
                foreach (var depId in card.Service.DependsOn)
                {
                    if (!string.IsNullOrWhiteSpace(depId) && cardMap.TryGetValue(depId, out var depCard))
                    {
                        Visit(depCard);
                    }
                }
            }

            visiting.Remove(card);
            if (visited.Add(card))
            {
                result.Add(card);
            }
        }

        // Prioritize infrastructure (cloudflared, databases) first
        foreach (var card in cardList.Where(c => IsInfrastructureService(c.Service)))
        {
            Visit(card);
        }

        foreach (var card in cardList)
        {
            Visit(card);
        }

        return result;
    }

    public static List<List<ServiceCardViewModel>> GroupCardsIntoStartWaves(IEnumerable<ServiceCardViewModel> cards)
    {
        var ordered = SortCardsByDependencies(cards);
        if (ordered.Count == 0) return new List<List<ServiceCardViewModel>>();

        var cardMap = BuildServiceCardMap(ordered);
        var hasAnyInfra = ordered.Any(c => IsInfrastructureService(c.Service));
        var memo = new Dictionary<ServiceCardViewModel, int>(ReferenceEqualityComparer.Instance);
        var visiting = new HashSet<ServiceCardViewModel>(ReferenceEqualityComparer.Instance);

        int GetWave(ServiceCardViewModel card)
        {
            if (memo.TryGetValue(card, out var cached)) return cached;
            if (!visiting.Add(card)) return 0;

            int maxDepWave = -1;
            if (card.Service.DependsOn != null)
            {
                foreach (var depId in card.Service.DependsOn)
                {
                    if (!string.IsNullOrWhiteSpace(depId) &&
                        cardMap.TryGetValue(depId, out var depCard) &&
                        !ReferenceEquals(depCard, card) &&
                        !visiting.Contains(depCard))
                    {
                        maxDepWave = Math.Max(maxDepWave, GetWave(depCard));
                    }
                }
            }

            visiting.Remove(card);

            int wave = IsInfrastructureService(card.Service)
                ? Math.Max(0, maxDepWave + 1)
                : (maxDepWave >= 0 ? maxDepWave + 1 : (hasAnyInfra ? 1 : 0));

            memo[card] = wave;
            return wave;
        }

        var waveDict = new SortedDictionary<int, List<ServiceCardViewModel>>();
        foreach (var card in ordered)
        {
            int wave = GetWave(card);
            if (!waveDict.TryGetValue(wave, out var list))
            {
                list = new List<ServiceCardViewModel>();
                waveDict[wave] = list;
            }
            list.Add(card);
        }

        return waveDict.Values.ToList();
    }

    private static Dictionary<string, ServiceCardViewModel> BuildServiceCardMap(IEnumerable<ServiceCardViewModel> cards)
    {
        var cardMap = new Dictionary<string, ServiceCardViewModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var card in cards)
        {
            if (!string.IsNullOrWhiteSpace(card.Service.Id) && !cardMap.ContainsKey(card.Service.Id))
            {
                cardMap[card.Service.Id] = card;
            }
        }
        return cardMap;
    }

    private static bool IsInfrastructureService(ServiceDefinition svc)
    {
        var id = (svc.Id ?? "").ToLowerInvariant();
        var img = (svc.Image ?? "").ToLowerInvariant();
        return id.Contains("cloudflared") || id.Contains("tunnel") || id.Contains("db") || id.Contains("postgres") ||
               id.Contains("mysql") || id.Contains("mariadb") || id.Contains("redis") || id.Contains("mongo") ||
               img.Contains("cloudflared") || img.Contains("postgres") || img.Contains("mysql") || img.Contains("redis");
    }

    [RelayCommand]
    private async Task StartAllAsync()
    {
        var waves = GroupCardsIntoStartWaves(ServiceCards);
        foreach (var wave in waves)
        {
            var tasks = new List<Task>();
            foreach (var card in wave)
            {
                if (card.IsStopped && !card.IsBusy)
                {
                    tasks.Add(card.StartCommand.ExecuteAsync(null));
                }
            }

            if (tasks.Count > 0)
            {
                await Task.WhenAll(tasks);
            }
        }
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
        var loc = LocalizationService.Instance;
        StatusNotification = loc.Get("Main_PruningDocker");
        try
        {
            var (reclaimed, summary) = await _dockerService.PruneUnusedResourcesAsync();
            var mb = reclaimed / (1024.0 * 1024.0);
            StatusNotification = loc.Get("Main_PruneDone", summary, mb);
        }
        catch (Exception ex)
        {
            StatusNotification = loc.Get("Main_PruneError", ex.Message);
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

        if (IsGitHubLoggedIn)
        {
            try
            {
                await _gitHubProfileService.PushProfileToGitHubAsync(SelectedProfile, subfolder);
            }
            catch (Exception ex)
            {
                StatusNotification = LocalizationService.Instance.Get("Main_GitHubSyncFailed", ex.Message);
            }
        }
    }

    [RelayCommand]
    private async Task ReloadProfilesAsync()
    {
        HasExternalProfileChanges = false;
        UpdateAllButtonText = LocalizationService.Instance.Get("Tray_UpdateAll");
        UpdateAllButtonBackground = "#2563EB";
        _lastHandledChange = DateTime.UtcNow.AddSeconds(2);
        await LoadProfilesAsync(forceRefresh: false);
        StatusNotification = LocalizationService.Instance.Get("Main_ProfileReloaded", SelectedProfile?.Name ?? string.Empty);
    }

    [RelayCommand]
    private void DismissProfileChangesAlert()
    {
        HasExternalProfileChanges = false;
        UpdateAllButtonText = LocalizationService.Instance.Get("Tray_UpdateAll");
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
            Application.Current?.Dispatcher?.BeginInvoke(() =>
            {
                if (_disposed) return;
                if ((DateTime.UtcNow - _lastHandledChange).TotalSeconds < 2) return;
                _lastHandledChange = DateTime.UtcNow;

                HasExternalProfileChanges = true;
                UpdateAllButtonText = LocalizationService.Instance.Get("Toolbar_UpdateAvailable");
                UpdateAllButtonBackground = "#F59E0B";
                StatusNotification = LocalizationService.Instance.Get("Main_ProfileModifiedNotice", currentProfileName);
            });
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _pollTimer.Stop();
        _pollTimer.Tick -= OnPollTimerTick;
        LocalizationService.Instance.LanguageChanged -= OnLanguageChanged;

        if (_profilesWatcher != null)
        {
            try
            {
                _profilesWatcher.EnableRaisingEvents = false;
                _profilesWatcher.Changed -= OnProfileFileWatcherEvent;
                _profilesWatcher.Created -= OnProfileFileWatcherEvent;
                _profilesWatcher.Renamed -= OnProfileFileWatcherEvent;
                _profilesWatcher.Dispose();
            }
            catch { }
            _profilesWatcher = null;
        }

        foreach (var card in ServiceCards.ToList())
        {
            card.Dispose();
        }
        ServiceCards.Clear();

        try { _loadProfilesLock.Dispose(); } catch { }
        GC.SuppressFinalize(this);
    }
}
