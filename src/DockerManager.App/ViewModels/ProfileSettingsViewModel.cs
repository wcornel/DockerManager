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
    private readonly ProfileModel _profile;
    private readonly IEnumerable<ProfileModel> _allProfiles;
    private readonly ISettingsService _settingsService;
    private readonly ICredentialService _credentialService;
    private readonly ICloudflareService _cloudflareService;
    private readonly IDockerService _dockerService;

    [ObservableProperty]
    private string _name = string.Empty;

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
        IDockerService dockerService)
    {
        _profile = profile;
        _allProfiles = allProfiles ?? Enumerable.Empty<ProfileModel>();
        _settingsService = settingsService;
        _credentialService = credentialService;
        _cloudflareService = cloudflareService;
        _dockerService = dockerService;

        Name = profile.Name;
        Description = profile.Description;
        AutoStopPreviousOnSwitch = profile.AutoStopPreviousOnSwitch;

        // Initialize Cloudflare fields
        if (profile.Cloudflare != null)
        {
            CloudflareAccountId = profile.Cloudflare.AccountId ?? string.Empty;
            CloudflareTunnelId = profile.Cloudflare.TunnelId ?? string.Empty;
            CloudflareDomain = profile.Cloudflare.Domain ?? string.Empty;
            CloudflareApiToken = profile.Cloudflare.ApiToken ?? string.Empty;
            CloudflareTunnelToken = profile.Cloudflare.TunnelToken ?? string.Empty;
        }

        // Initialize Registry fields
        if (profile.Registry != null)
        {
            RegistryServer = !string.IsNullOrWhiteSpace(profile.Registry.Server) ? profile.Registry.Server : "ghcr.io";
            RegistryNamespace = profile.Registry.Namespace ?? string.Empty;
            RegistryUsername = profile.Registry.Username ?? string.Empty;
            RegistryPassword = profile.Registry.Password ?? string.Empty;
        }

        // Populate Copy Sources
        CopySources.Add(new ProfileCopySource
        {
            DisplayName = "⚙️ Globale Standaard Instellingen (App)",
            IsGlobalDefaults = true
        });

        foreach (var p in _allProfiles.Where(p => !p.Name.Equals(profile.Name, StringComparison.OrdinalIgnoreCase)))
        {
            CopySources.Add(new ProfileCopySource
            {
                DisplayName = $"📄 Profiel: {p.Name}",
                IsGlobalDefaults = false,
                SourceProfile = p
            });
        }

        SelectedCopySource = CopySources.FirstOrDefault();
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
    private void Save()
    {
        if (string.IsNullOrWhiteSpace(Name)) return;

        _profile.Name = Name.Trim();
        _profile.Description = Description.Trim();
        _profile.AutoStopPreviousOnSwitch = AutoStopPreviousOnSwitch;

        _profile.Cloudflare ??= new ProfileCloudflareConfig();
        _profile.Cloudflare.AccountId = CloudflareAccountId.Trim();
        _profile.Cloudflare.TunnelId = CloudflareTunnelId.Trim();
        _profile.Cloudflare.Domain = CloudflareDomain.Trim().TrimStart('.').ToLowerInvariant();
        _profile.Cloudflare.ApiToken = CloudflareApiToken.Trim();
        _profile.Cloudflare.TunnelToken = CloudflareTunnelToken.Trim();

        _profile.Registry ??= new ProfileRegistryConfig();
        _profile.Registry.Server = RegistryServer.Trim();
        _profile.Registry.Namespace = RegistryNamespace.Trim();
        _profile.Registry.Username = RegistryUsername.Trim();
        _profile.Registry.Password = RegistryPassword.Trim();

        IsSaved = true;
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private void Cancel()
    {
        RequestClose?.Invoke();
    }
}
