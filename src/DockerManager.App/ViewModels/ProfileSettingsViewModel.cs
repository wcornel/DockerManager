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

public class ProfileCopySource
{
    public string DisplayName { get; set; } = string.Empty;
    public bool IsGlobalDefaults { get; set; }
    public ProfileModel? SourceProfile { get; set; }
    public override string ToString() => DisplayName;
}

public partial class ProfileSettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settingsService;
    private readonly ICredentialService _credentialService;
    private readonly ICloudflareService _cloudflareService;
    private readonly IDockerService _dockerService;
    private readonly IGitHubProfileService? _gitHubProfileService;

    public ObservableCollection<ProfileModel> Profiles { get; } = new();

    private readonly Dictionary<ProfileModel, string> _originalNames = new();
    private readonly List<ProfileModel> _deletedProfiles = new();

    [ObservableProperty]
    private ProfileModel? _editingProfile;

    [ObservableProperty]
    private string _name = string.Empty;

    public string OriginalName => EditingProfile != null && _originalNames.TryGetValue(EditingProfile, out var orig) ? orig : Name;

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    private bool _autoStopPreviousOnSwitch;

    // Quick Copy / Inheritance
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

    public bool IsSaved { get; private set; }
    public event Action? RequestClose;

    public ProfileSettingsViewModel(
        ProfileModel profile,
        IEnumerable<ProfileModel> allProfiles,
        ISettingsService settingsService,
        ICredentialService credentialService,
        ICloudflareService cloudflareService,
        IDockerService dockerService,
        IGitHubProfileService? gitHubProfileService = null)
    {
        _settingsService = settingsService;
        _credentialService = credentialService;
        _cloudflareService = cloudflareService;
        _dockerService = dockerService;
        _gitHubProfileService = gitHubProfileService;

        Profiles.Clear();
        if (allProfiles != null)
        {
            foreach (var p in allProfiles)
            {
                if (!Profiles.Any(x => x.Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    Profiles.Add(p);
                    _originalNames[p] = p.Name;
                }
            }
        }

        if (profile != null && !Profiles.Any(x => x.Name.Equals(profile.Name, StringComparison.OrdinalIgnoreCase)))
        {
            Profiles.Insert(0, profile);
            _originalNames[profile] = profile.Name;
        }

        var initial = Profiles.FirstOrDefault(p => p.Name.Equals(profile?.Name, StringComparison.OrdinalIgnoreCase)) ?? Profiles.FirstOrDefault();
        EditingProfile = initial;
        if (initial != null)
        {
            LoadFromProfile(initial);
        }
    }

    partial void OnEditingProfileChanged(ProfileModel? oldValue, ProfileModel? newValue)
    {
        if (oldValue != null)
        {
            CommitToProfile(oldValue);
        }

        if (newValue != null)
        {
            LoadFromProfile(newValue);
        }
    }

    private void CommitToProfile(ProfileModel p)
    {
        p.Name = Name.Trim();
        p.Description = Description.Trim();
        p.AutoStopPreviousOnSwitch = AutoStopPreviousOnSwitch;

        p.Cloudflare ??= new ProfileCloudflareConfig();
        p.Cloudflare.AccountId = CloudflareAccountId.Trim();
        p.Cloudflare.TunnelId = CloudflareTunnelId.Trim();
        p.Cloudflare.Domain = CloudflareDomain.Trim().TrimStart('.').ToLowerInvariant();
        p.Cloudflare.ApiToken = CloudflareApiToken.Trim();
        p.Cloudflare.TunnelToken = CloudflareTunnelToken.Trim();

        p.Registry ??= new ProfileRegistryConfig();
        p.Registry.Server = RegistryServer.Trim();
        p.Registry.Namespace = RegistryNamespace.Trim();
        p.Registry.Username = RegistryUsername.Trim();
        p.Registry.Password = RegistryPassword.Trim();
    }

    private void LoadFromProfile(ProfileModel p)
    {
        Name = p.Name;
        Description = p.Description;
        AutoStopPreviousOnSwitch = p.AutoStopPreviousOnSwitch;

        if (p.Cloudflare != null)
        {
            CloudflareAccountId = p.Cloudflare.AccountId ?? string.Empty;
            CloudflareTunnelId = p.Cloudflare.TunnelId ?? string.Empty;
            CloudflareDomain = p.Cloudflare.Domain ?? string.Empty;
            CloudflareApiToken = p.Cloudflare.ApiToken ?? string.Empty;
            CloudflareTunnelToken = p.Cloudflare.TunnelToken ?? string.Empty;
        }
        else
        {
            CloudflareAccountId = string.Empty;
            CloudflareTunnelId = string.Empty;
            CloudflareDomain = string.Empty;
            CloudflareApiToken = string.Empty;
            CloudflareTunnelToken = string.Empty;
        }

        if (p.Registry != null)
        {
            RegistryServer = !string.IsNullOrWhiteSpace(p.Registry.Server) ? p.Registry.Server : "ghcr.io";
            RegistryNamespace = p.Registry.Namespace ?? string.Empty;
            RegistryUsername = p.Registry.Username ?? string.Empty;
            RegistryPassword = p.Registry.Password ?? string.Empty;
        }
        else
        {
            RegistryServer = "ghcr.io";
            RegistryNamespace = string.Empty;
            RegistryUsername = string.Empty;
            RegistryPassword = string.Empty;
        }

        RefreshCopySources();
    }

    private void RefreshCopySources()
    {
        CopySources.Clear();
        CopySources.Add(new ProfileCopySource
        {
            DisplayName = "⚙️ Globale Standaard Instellingen (App)",
            IsGlobalDefaults = true
        });

        if (EditingProfile != null)
        {
            foreach (var p in Profiles.Where(p => !p.Name.Equals(EditingProfile.Name, StringComparison.OrdinalIgnoreCase)))
            {
                CopySources.Add(new ProfileCopySource
                {
                    DisplayName = $"📄 Profiel: {p.Name}",
                    IsGlobalDefaults = false,
                    SourceProfile = p
                });
            }
        }

        SelectedCopySource = CopySources.FirstOrDefault();
        CopyStatusMessage = string.Empty;
    }

    [RelayCommand]
    private void AddProfile()
    {
        if (EditingProfile != null)
        {
            CommitToProfile(EditingProfile);
        }

        var count = Profiles.Count + 1;
        var newName = $"Profiel {count}";
        while (Profiles.Any(p => p.Name.Equals(newName, StringComparison.OrdinalIgnoreCase)))
        {
            count++;
            newName = $"Profiel {count}";
        }

        var newProfile = new ProfileModel
        {
            Name = newName,
            Description = string.Empty,
            Cloudflare = EditingProfile?.Cloudflare?.Clone() ?? new ProfileCloudflareConfig(),
            Registry = EditingProfile?.Registry?.Clone() ?? new ProfileRegistryConfig()
        };

        _originalNames[newProfile] = newName;
        Profiles.Add(newProfile);
        EditingProfile = newProfile;
    }

    [RelayCommand]
    private void DeleteProfile()
    {
        if (EditingProfile == null || Profiles.Count <= 1)
        {
            System.Windows.MessageBox.Show(
                "Het actieve profiel kan niet worden verwijderd omdat er minimaal één profiel aanwezig moet zijn.",
                "Profiel Verwijderen",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
            return;
        }

        var toDelete = EditingProfile;
        var confirm = System.Windows.MessageBox.Show(
            $"Weet je zeker dat je profiel '{toDelete.Name}' wilt verwijderen?\n\nDe actieve containers van dit profiel worden gestopt en het configuratiebestand wordt verwijderd bij het opslaan.",
            "Profiel Verwijderen",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);

        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        var idx = Profiles.IndexOf(toDelete);
        Profiles.Remove(toDelete);
        _deletedProfiles.Add(toDelete);

        EditingProfile = Profiles[Math.Max(0, idx - 1)];
    }

    [RelayCommand]
    private void CopyAllSettings()
    {
        if (SelectedCopySource == null) return;
        CopyCloudflare();
        CopyRegistry();
        CopyStatusMessage = $"✅ Alle instellingen overgenomen van '{SelectedCopySource.DisplayName}'!";
    }

    [RelayCommand]
    private void CopyCloudflareSettings()
    {
        if (SelectedCopySource == null) return;
        CopyCloudflare();
        CopyStatusMessage = $"✅ Cloudflare instellingen overgenomen van '{SelectedCopySource.DisplayName}'!";
    }

    [RelayCommand]
    private void CopyRegistrySettings()
    {
        if (SelectedCopySource == null) return;
        CopyRegistry();
        CopyStatusMessage = $"✅ Container Registry overgenomen van '{SelectedCopySource.DisplayName}'!";
    }

    private void CopyCloudflare()
    {
        if (SelectedCopySource == null) return;

        if (SelectedCopySource.IsGlobalDefaults)
        {
            var s = _settingsService.Settings;
            CloudflareAccountId = s.CloudflareAccountId;
            CloudflareTunnelId = s.CloudflareTunnelId;
            CloudflareDomain = s.CloudflareDomain;
            CloudflareApiToken = _credentialService.GetCloudflareApiToken() ?? string.Empty;
            CloudflareTunnelToken = s.CloudflareTunnelToken;
        }
        else if (SelectedCopySource.SourceProfile?.Cloudflare != null)
        {
            var cf = SelectedCopySource.SourceProfile.Cloudflare;
            CloudflareAccountId = cf.AccountId;
            CloudflareTunnelId = cf.TunnelId;
            CloudflareDomain = cf.Domain;
            CloudflareApiToken = cf.ApiToken;
            CloudflareTunnelToken = cf.TunnelToken;
        }
    }

    private void CopyRegistry()
    {
        if (SelectedCopySource == null) return;

        if (SelectedCopySource.IsGlobalDefaults)
        {
            var s = _settingsService.Settings;
            RegistryServer = "ghcr.io";
            RegistryNamespace = !string.IsNullOrWhiteSpace(s.LoggedInUsername) ? s.LoggedInUsername : s.GitHubRepoOwner;
            RegistryUsername = s.LoggedInUsername;
            var ghToken = _credentialService.GetGitHubToken();
            if (!string.IsNullOrWhiteSpace(ghToken))
            {
                RegistryPassword = ghToken;
            }
        }
        else if (SelectedCopySource.SourceProfile?.Registry != null)
        {
            var reg = SelectedCopySource.SourceProfile.Registry;
            RegistryServer = !string.IsNullOrWhiteSpace(reg.Server) ? reg.Server : "ghcr.io";
            RegistryNamespace = reg.Namespace;
            RegistryUsername = reg.Username;
            RegistryPassword = reg.Password;
        }
    }

    [RelayCommand]
    private async Task TestCloudflareAsync()
    {
        if (string.IsNullOrWhiteSpace(CloudflareApiToken) || string.IsNullOrWhiteSpace(CloudflareAccountId))
        {
            CloudflareTestStatus = "⚠️ Vul minimaal API Token en Account ID in om te testen.";
            return;
        }

        IsTestingCloudflare = true;
        CloudflareTestStatus = "Verbinding testen...";

        try
        {
            var (ok, msg, name) = await _cloudflareService.TestConnectionAsync(
                overrideToken: CloudflareApiToken.Trim(),
                overrideAccountId: CloudflareAccountId.Trim(),
                overrideTunnelId: !string.IsNullOrWhiteSpace(CloudflareTunnelId) ? CloudflareTunnelId.Trim() : null);

            CloudflareTestStatus = ok
                ? $"✅ Geslaagd! {(name != null ? $"Tunnel '{name}' actief" : msg)}"
                : $"❌ Mislukt: {msg}";
        }
        catch (Exception ex)
        {
            CloudflareTestStatus = $"❌ Fout: {ex.Message}";
        }
        finally
        {
            IsTestingCloudflare = false;
        }
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

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (EditingProfile != null)
        {
            CommitToProfile(EditingProfile);
        }

        var subfolder = _settingsService.Settings.GetActiveServer().ProfilesSubfolder;

        // 1. Process deleted profiles
        foreach (var dp in _deletedProfiles)
        {
            foreach (var svc in dp.Services)
            {
                var cName = string.IsNullOrWhiteSpace(svc.ContainerName) ? $"{dp.Name.ToLowerInvariant()}_{svc.Id}" : svc.ContainerName;
                try
                {
                    await _dockerService.StopContainerAsync(cName);
                }
                catch { }
            }

            if (_gitHubProfileService != null)
            {
                await _gitHubProfileService.DeleteProfileLocallyAsync(dp.Name, subfolder);
                if (_originalNames.TryGetValue(dp, out var oldName) && !string.Equals(oldName, dp.Name, StringComparison.OrdinalIgnoreCase))
                {
                    await _gitHubProfileService.DeleteProfileLocallyAsync(oldName, subfolder);
                }
            }
        }

        // 2. Process all current profiles
        foreach (var p in Profiles)
        {
            if (string.IsNullOrWhiteSpace(p.Name)) continue;

            if (_originalNames.TryGetValue(p, out var oldName) && !string.Equals(oldName, p.Name, StringComparison.OrdinalIgnoreCase))
            {
                if (_gitHubProfileService != null)
                {
                    await _gitHubProfileService.DeleteProfileLocallyAsync(oldName, subfolder);
                }
            }

            if (_gitHubProfileService != null)
            {
                await _gitHubProfileService.SaveProfileLocallyAsync(p, subfolder);
            }
        }

        if (EditingProfile != null)
        {
            _settingsService.Settings.LastActiveProfile = EditingProfile.Name;
            var activeServer = _settingsService.Settings.GetActiveServer();
            activeServer.LastActiveProfile = EditingProfile.Name;
            _settingsService.Save();
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
}
