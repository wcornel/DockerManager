using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DockerManager.App.Models;
using DockerManager.App.Services;

namespace DockerManager.App.ViewModels;

public partial class PublishDotnetAppViewModel : ObservableObject
{
    private readonly IDotnetAppScannerService _dotnetScanner;
    private readonly IPythonAppScannerService _pythonScanner;
    private readonly IDotnetPublisherService _publisherService;
    private readonly ISettingsService _settingsService;
    private readonly ICredentialService _credentialService;
    private readonly ICloudflareService _cloudflareService;

    [ObservableProperty]
    private bool _enableCloudflareTunnel = true;

    [ObservableProperty]
    private string _cloudflareSubdomain = string.Empty;

    [ObservableProperty]
    private string _cloudflareDomain = string.Empty;

    [ObservableProperty]
    private bool _hasCloudflareConfigured;

    [ObservableProperty]
    private string _cloudflareStatusMessage = string.Empty;

    public bool EnsureCloudflaredInProfile { get; private set; }

    public string FullCloudflareUrl =>
        !string.IsNullOrWhiteSpace(CloudflareSubdomain) && !string.IsNullOrWhiteSpace(CloudflareDomain)
            ? $"https://{CloudflareSubdomain.Trim().ToLowerInvariant()}.{CloudflareDomain.Trim().ToLowerInvariant()}"
            : string.Empty;

    [ObservableProperty]
    private string _selectedSourceFolder = string.Empty;

    [ObservableProperty]
    private AppFrameworkType _framework = AppFrameworkType.Dotnet;

    [ObservableProperty]
    private string _folderPath = string.Empty;

    [ObservableProperty]
    private string _appName = string.Empty;

    [ObservableProperty]
    private string _entrypointDll = string.Empty;

    [ObservableProperty]
    private string _dotnetVersion = "8.0";

    [ObservableProperty]
    private string _baseDockerImage = "mcr.microsoft.com/dotnet/aspnet:8.0";

    [ObservableProperty]
    private string _startCommand = "python main.py";

    [ObservableProperty]
    private bool _hasRequirementsTxt = true;

    [ObservableProperty]
    private int _containerPort = 8080;

    [ObservableProperty]
    private string _targetGitHubOwner = string.Empty;

    [ObservableProperty]
    private string _targetGitHubRepo = "DotnetContainers";

    [ObservableProperty]
    private string _gitHubBranch = "main";

    [ObservableProperty]
    private bool _addToActiveProfile = true;

    [ObservableProperty]
    private bool _enablePersistentVolume = true;

    [ObservableProperty]
    private bool _enableAmsterdamTimezone = true;

    [ObservableProperty]
    private string _versionTag = $"v{DateTime.Now:yyyy.MM.dd}";

    [ObservableProperty]
    private bool _pushToRegistry = true;

    [ObservableProperty]
    private string _selectedTargetRegistry = "ghcr.io (GitHub Container Registry)";

    public ObservableCollection<string> AvailableRegistries { get; } = new();

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private bool _isScanned;

    [ObservableProperty]
    private bool _isPublishing;

    [ObservableProperty]
    private bool _isCompleted;

    [ObservableProperty]
    private string _imageTag = string.Empty;

    [ObservableProperty]
    private string _gitHubRepoUrl = string.Empty;

    [ObservableProperty]
    private string _generatedDockerfile = string.Empty;

    [ObservableProperty]
    private bool _showDockerfilePreview;

    [ObservableProperty]
    private bool _showPermissionsHelp;

    [RelayCommand]
    private void ToggleHelp()
    {
        ShowPermissionsHelp = !ShowPermissionsHelp;
    }

    public bool IsPython => Framework == AppFrameworkType.Python;
    public bool IsDotnet => Framework == AppFrameworkType.Dotnet;
    public string WindowTitle => Framework == AppFrameworkType.Python ? "🐍 Python App omzetten naar Docker" : "🐳 .NET App omzetten naar Docker";
    public string Step1Description => Framework == AppFrameworkType.Python 
        ? "Selecteer de hoofdmap van je Python project (met main.py/app.py en requirements.txt)." 
        : "Kies de map waar je .NET applicatie naar is gepubliceerd vanuit JetBrains Rider of Visual Studio.";

    public string LiveTargetImageName
    {
        get
        {
            var reg = (SelectedTargetRegistry ?? "ghcr.io").Split(' ')[0].Trim().ToLowerInvariant();
            var owner = !string.IsNullOrWhiteSpace(TargetGitHubOwner) ? TargetGitHubOwner.Trim().ToLowerInvariant() : "jouw-account";
            var app = !string.IsNullOrWhiteSpace(AppName) ? AppName.Trim().ToLowerInvariant() : "appnaam";

            if (reg.Equals("docker.io", StringComparison.OrdinalIgnoreCase))
            {
                return $"{owner}/{app}:latest";
            }
            return $"{reg}/{owner}/{app}:latest";
        }
    }

    public string LiveReleaseImageTag
    {
        get
        {
            var reg = (SelectedTargetRegistry ?? "ghcr.io").Split(' ')[0].Trim().ToLowerInvariant();
            var owner = !string.IsNullOrWhiteSpace(TargetGitHubOwner) ? TargetGitHubOwner.Trim().ToLowerInvariant() : "jouw-account";
            var app = !string.IsNullOrWhiteSpace(AppName) ? AppName.Trim().ToLowerInvariant() : "appnaam";
            var v = !string.IsNullOrWhiteSpace(VersionTag) ? VersionTag.Trim() : $"v{DateTime.Now:yyyy.MM.dd}";
            if (!v.StartsWith("v", StringComparison.OrdinalIgnoreCase)) v = $"v{v}";

            if (reg.Equals("docker.io", StringComparison.OrdinalIgnoreCase))
            {
                return $"{owner}/{app}:{v}";
            }
            return $"{reg}/{owner}/{app}:{v}";
        }
    }

    partial void OnAppNameChanged(string value)
    {
        OnPropertyChanged(nameof(LiveTargetImageName));
        OnPropertyChanged(nameof(LiveReleaseImageTag));

        if (string.IsNullOrWhiteSpace(CloudflareSubdomain) || CloudflareSubdomain == AppName.ToLowerInvariant())
        {
            CloudflareSubdomain = value.Trim().ToLowerInvariant();
            OnPropertyChanged(nameof(FullCloudflareUrl));
        }
    }

    partial void OnCloudflareSubdomainChanged(string value)
    {
        OnPropertyChanged(nameof(FullCloudflareUrl));
    }
    partial void OnTargetGitHubOwnerChanged(string value)
    {
        OnPropertyChanged(nameof(LiveTargetImageName));
        OnPropertyChanged(nameof(LiveReleaseImageTag));
    }
    partial void OnSelectedTargetRegistryChanged(string value)
    {
        OnPropertyChanged(nameof(LiveTargetImageName));
        OnPropertyChanged(nameof(LiveReleaseImageTag));
    }
    partial void OnVersionTagChanged(string value)
    {
        OnPropertyChanged(nameof(LiveReleaseImageTag));
    }

    public ObservableCollection<ScannedConfigParam> Parameters { get; } = new();

    public event Action? RequestClose;

    public PublishResult? LastResult { get; private set; }
    public ServiceDefinition? CreatedServiceDefinition { get; private set; }

    public PublishDotnetAppViewModel(
        IDotnetAppScannerService dotnetScanner,
        IPythonAppScannerService pythonScanner,
        IDotnetPublisherService publisherService,
        ISettingsService settingsService,
        ICredentialService credentialService,
        ICloudflareService cloudflareService,
        AppFrameworkType framework = AppFrameworkType.Dotnet)
    {
        _dotnetScanner = dotnetScanner;
        _pythonScanner = pythonScanner;
        _publisherService = publisherService;
        _settingsService = settingsService;
        _credentialService = credentialService;
        _cloudflareService = cloudflareService;
        Framework = framework;
        var s = _settingsService.Settings;
        TargetGitHubOwner = !string.IsNullOrWhiteSpace(s.LoggedInUsername) ? s.LoggedInUsername : s.GitHubRepoOwner;
        TargetGitHubRepo = string.IsNullOrWhiteSpace(s.GitHubDeployRepo) ? "DotnetContainers" : s.GitHubDeployRepo;
        GitHubBranch = string.IsNullOrWhiteSpace(s.GitHubBranch) ? "main" : s.GitHubBranch;

        CloudflareDomain = s.CloudflareDomain;
        HasCloudflareConfigured = s.HasCloudflareConfigured;
        EnableCloudflareTunnel = s.HasCloudflareConfigured;

        if (IsPython)
        {
            BaseDockerImage = "python:3.12-slim";
            ContainerPort = 8000;
        }
        else
        {
            BaseDockerImage = "mcr.microsoft.com/dotnet/aspnet:8.0";
            ContainerPort = 8080;
        }

        AvailableRegistries.Add("ghcr.io (GitHub Container Registry)");
        AvailableRegistries.Add("docker.io (Docker Hub)");
        AvailableRegistries.Add("azurecr.io (Azure Container Registry)");
        AvailableRegistries.Add("registry.gitlab.com (GitLab)");

        foreach (var r in _credentialService.GetAllRegistryCredentials())
        {
            var label = $"{r.ServerAddress} ({r.Username})";
            if (!AvailableRegistries.Any(x => x.StartsWith(r.ServerAddress, StringComparison.OrdinalIgnoreCase)))
            {
                AvailableRegistries.Add(label);
            }
        }
    }

    [RelayCommand]
    private void BrowseFolder()
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = IsPython ? "Selecteer je Python projectmap" : "Selecteer de .NET 'publish' map van je applicatie",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            FolderPath = dialog.SelectedPath;
            _ = ScanFolderAsync();
        }
    }

    [RelayCommand]
    public async Task ScanFolderAsync()
    {
        if (string.IsNullOrWhiteSpace(FolderPath)) return;

        IsScanning = true;
        IsScanned = false;
        StatusMessage = $"🔍 {Framework} projectmap analyseren...";
        Parameters.Clear();

        try
        {
            if (IsPython)
            {
                var scanned = await Task.Run(() => _pythonScanner.ScanPythonFolderAsync(FolderPath));
                if (!scanned.IsValid)
                {
                    StatusMessage = $"⚠️ {scanned.ErrorMessage}";
                    return;
                }

                AppName = scanned.SuggestedAppName;
                StartCommand = scanned.StartCommand;
                BaseDockerImage = scanned.BaseDockerImage;
                ContainerPort = scanned.SuggestedPort;
                HasRequirementsTxt = scanned.HasRequirementsTxt;
                VersionTag = scanned.SuggestedVersionTag;

                foreach (var p in scanned.Parameters)
                {
                    Parameters.Add(p);
                }

                UpdateGeneratedDockerfile();
                IsScanned = true;
                StatusMessage = $"✅ Python project geanalyseerd: '{AppName}' (Release: {VersionTag}, Start: '{StartCommand}', {Parameters.Count} omgevingsvariabelen gevonden).";
            }
            else
            {
                var scanned = await Task.Run(() => _dotnetScanner.ScanFolderAsync(FolderPath));
                if (!scanned.IsValid)
                {
                    StatusMessage = $"⚠️ {scanned.ErrorMessage}";
                    return;
                }

                AppName = scanned.SuggestedAppName;
                EntrypointDll = scanned.EntrypointDll;
                DotnetVersion = scanned.DotnetVersion;
                BaseDockerImage = scanned.BaseDockerImage;
                ContainerPort = scanned.SuggestedPort;
                VersionTag = scanned.SuggestedVersionTag;

                foreach (var p in scanned.Parameters)
                {
                    Parameters.Add(p);
                }

                UpdateGeneratedDockerfile();
                IsScanned = true;
                StatusMessage = $"✅ .NET app geanalyseerd: {EntrypointDll} (Release: {VersionTag}, .NET {DotnetVersion}) met {Parameters.Count} gevonden configuratieparameters.";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"❌ Fout: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
        }
    }

    [RelayCommand]
    private void ToggleSelectAll()
    {
        var anyUnselected = Parameters.Any(p => !p.IsSelected);
        foreach (var p in Parameters)
        {
            p.IsSelected = anyUnselected;
        }
        UpdateGeneratedDockerfile();
    }

    [RelayCommand]
    private void ToggleDockerfilePreview()
    {
        UpdateGeneratedDockerfile();
        ShowDockerfilePreview = !ShowDockerfilePreview;
    }

    [RelayCommand]
    private void SaveDockerfileToProjectFolder()
    {
        if (string.IsNullOrWhiteSpace(FolderPath) || !Directory.Exists(FolderPath))
        {
            StatusMessage = "⚠️ Selecteer eerst een geldige projectmap.";
            return;
        }

        UpdateGeneratedDockerfile();
        var success = _publisherService.SaveDockerfileToProjectFolder(FolderPath, GeneratedDockerfile, Framework, LiveTargetImageName, LiveReleaseImageTag, out var err);
        if (success)
        {
            StatusMessage = $"✅ Dockerfile, .dockerignore en docker-build.bat succesvol opgeslagen in de projectmap! (Tags: {VersionTag} & latest)";
        }
        else
        {
            StatusMessage = $"❌ Fout bij opslaan: {err}";
        }
    }

    public void UpdateGeneratedDockerfile()
    {
        var req = new PublishRequest
        {
            Framework = Framework,
            SourceFolderPath = FolderPath,
            AppName = AppName,
            EntrypointDll = EntrypointDll,
            StartCommand = StartCommand,
            HasRequirementsTxt = HasRequirementsTxt,
            BaseDockerImage = BaseDockerImage,
            ContainerPort = ContainerPort,
            VersionTag = VersionTag,
            EnableAmsterdamTimezone = EnableAmsterdamTimezone,
            EnablePersistentVolume = EnablePersistentVolume,
            SelectedParameters = Parameters.Where(p => p.IsSelected).ToList()
        };
        GeneratedDockerfile = _publisherService.GenerateDockerfile(req);
    }

    [RelayCommand]
    private async Task PublishAsync()
    {
        if (string.IsNullOrWhiteSpace(FolderPath) || !IsScanned)
        {
            StatusMessage = "⚠️ Selecteer en scan eerst een geldige projectmap.";
            return;
        }

        if (string.IsNullOrWhiteSpace(AppName))
        {
            StatusMessage = "⚠️ Voer een geldige appnaam in.";
            return;
        }

        if (PushToRegistry)
        {
            var reg = SelectedTargetRegistry.Split(' ')[0].Trim().ToLowerInvariant();
            var cred = _credentialService.GetCredentialForImage(reg) ?? _credentialService.GetCredentialForImage("ghcr.io");
            if (cred == null || string.IsNullOrWhiteSpace(cred.Password))
            {
                StatusMessage = "❌ Geen inloggegevens gevonden voor deze registry. Koppel eerst je account via Instellingen, of vink 'Direct uploaden naar registry' uit om puur lokaal te bouwen.";
                return;
            }
        }

        IsPublishing = true;
        StatusMessage = "🔨 Docker build proces starten...";

        IProgress<string> progress = new Progress<string>(msg => StatusMessage = msg);

        var request = new PublishRequest
        {
            Framework = Framework,
            SourceFolderPath = FolderPath,
            AppName = AppName.Trim(),
            EntrypointDll = EntrypointDll.Trim(),
            StartCommand = StartCommand.Trim(),
            HasRequirementsTxt = HasRequirementsTxt,
            BaseDockerImage = BaseDockerImage.Trim(),
            ContainerPort = ContainerPort > 0 ? ContainerPort : (IsPython ? 8000 : 8080),
            SelectedParameters = Parameters.Where(p => p.IsSelected).ToList(),
            TargetGitHubOwner = TargetGitHubOwner.Trim(),
            TargetGitHubRepo = TargetGitHubRepo.Trim(),
            TargetRegistry = SelectedTargetRegistry.Split(' ')[0],
            VersionTag = VersionTag.Trim(),
            GitHubBranch = GitHubBranch.Trim(),
            AddToActiveProfile = AddToActiveProfile,
            EnableAmsterdamTimezone = EnableAmsterdamTimezone,
            EnablePersistentVolume = EnablePersistentVolume
        };

        try
        {
            var result = await Task.Run(() => _publisherService.BuildAndOptionallyPushAsync(request, PushToRegistry, progress));
            LastResult = result;

            if (result.Success)
            {
                ImageTag = result.ImageTag;
                IsCompleted = true;
                StatusMessage = PushToRegistry 
                    ? $"🎉 Container '{result.ReleaseTag}' succesvol gebouwd en geüpload naar registry!" 
                    : $"🎉 Container '{result.ReleaseTag}' succesvol lokaal gebouwd!";

                if (AddToActiveProfile)
                {
                    CreatedServiceDefinition = new ServiceDefinition
                    {
                        Id = AppName.ToLowerInvariant(),
                        DisplayName = char.ToUpperInvariant(AppName[0]) + AppName[1..],
                        ContainerName = AppName.ToLowerInvariant(),
                        Image = result.ImageTag,
                        Description = IsPython ? $"Python app '{AppName}' ({StartCommand})" : $".NET {DotnetVersion} app '{AppName}'",
                        RequiresAuth = true,
                        RestartPolicy = "unless-stopped",
                        Ports = new List<PortMapping>
                        {
                            new PortMapping { HostPort = ContainerPort, ContainerPort = ContainerPort, Protocol = "tcp" }
                        }
                    };

                    if (EnablePersistentVolume)
                    {
                        CreatedServiceDefinition.Volumes.Add(new VolumeMapping
                        {
                            HostPath = $"./volumes/{AppName.ToLowerInvariant()}/data",
                            ContainerPath = "/app/data",
                            IsNamedVolume = false,
                            ReadOnly = false
                        });
                    }

                    if (EnableAmsterdamTimezone)
                    {
                        CreatedServiceDefinition.Environment["TZ"] = "Europe/Amsterdam";
                    }

                    foreach (var p in request.SelectedParameters)
                    {
                        if (p.IsSelected && !string.IsNullOrWhiteSpace(p.DockerEnvKey))
                        {
                            CreatedServiceDefinition.Environment[p.DockerEnvKey] = p.CurrentValue;
                        }
                    }

                    if (EnableCloudflareTunnel && HasCloudflareConfigured && !string.IsNullOrWhiteSpace(CloudflareSubdomain))
                    {
                        progress?.Report($"☁️ Subdomein '{CloudflareSubdomain}' registreren in Cloudflare Tunnel...");
                        try
                        {
                            var (cfOk, cfMsg, fullHost) = await _cloudflareService.RegisterSubdomainAsync(CloudflareSubdomain, ContainerPort);
                            if (cfOk && !string.IsNullOrWhiteSpace(fullHost))
                            {
                                EnsureCloudflaredInProfile = true;
                                CreatedServiceDefinition.Labels["cloudflare.tunnel.hostname"] = fullHost;
                                CreatedServiceDefinition.Environment["ASPNETCORE_FORWARDEDHEADERS_ENABLED"] = "true";
                                StatusMessage += $"\n🌐 Cloudflare URL actief: https://{fullHost}";
                            }
                            else
                            {
                                StatusMessage += $"\n⚠️ Cloudflare: {cfMsg}";
                            }
                        }
                        catch (Exception cfEx)
                        {
                            StatusMessage += $"\n⚠️ Fout bij Cloudflare registratie: {cfEx.Message}";
                        }
                    }
                }
            }
            else
            {
                StatusMessage = $"❌ Fout: {result.ErrorMessage}";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"❌ Fout bij publiceren: {ex.Message}";
        }
        finally
        {
            IsPublishing = false;
        }
    }

    [RelayCommand]
    private void OpenGitHubUrl()
    {
        if (!string.IsNullOrWhiteSpace(GitHubRepoUrl))
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = GitHubRepoUrl,
                    UseShellExecute = true
                });
            }
            catch { }
        }
    }

    [RelayCommand]
    private void OpenCloudflareUrl()
    {
        if (!string.IsNullOrWhiteSpace(FullCloudflareUrl))
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = FullCloudflareUrl,
                    UseShellExecute = true
                });
            }
            catch { }
        }
    }

    [RelayCommand]
    private void Close()
    {
        RequestClose?.Invoke();
    }
}
