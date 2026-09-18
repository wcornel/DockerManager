using Velopack;
using Velopack.Sources;
using DockerManager.App.Models;

namespace DockerManager.App.Services;

public class UpdateCheckResult
{
    public bool IsUpdateAvailable { get; set; }
    public string CurrentVersion { get; set; } = string.Empty;
    public string? NewVersion { get; set; }
    public UpdateInfo? UpdateInfo { get; set; }
    public string? Message { get; set; }
    public bool IsInstalledApp { get; set; }
}

public interface IAppUpdateService
{
    Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken ct = default);
    Task<bool> DownloadAndApplyUpdateAsync(UpdateInfo updateInfo, Action<int>? progress = null, CancellationToken ct = default);
}

public class AppUpdateService : IAppUpdateService
{
    private readonly ISettingsService _settingsService;
    private readonly ICredentialService _credentialService;

    public AppUpdateService(ISettingsService settingsService, ICredentialService credentialService)
    {
        _settingsService = settingsService;
        _credentialService = credentialService;
    }

    private UpdateManager GetUpdateManager()
    {
        const string repoUrl = "https://github.com/wcornel/DockerManager";
        var token = _credentialService.GetGitHubToken();

        var source = new GithubSource(repoUrl, token, prerelease: false);
        return new UpdateManager(source);
    }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken ct = default)
    {
        var mgr = GetUpdateManager();
        var currentVer = mgr.CurrentVersion?.ToString() ?? "1.0.0";

        if (!mgr.IsInstalled)
        {
            return new UpdateCheckResult
            {
                IsInstalledApp = false,
                CurrentVersion = currentVer,
                Message = "DockerManager draait momenteel als standalone/portable versie. Installeer via Setup.exe om automatische updates in te schakelen."
            };
        }

        try
        {
            var updateInfo = await mgr.CheckForUpdatesAsync();
            if (updateInfo == null)
            {
                return new UpdateCheckResult
                {
                    IsInstalledApp = true,
                    IsUpdateAvailable = false,
                    CurrentVersion = currentVer,
                    Message = $"DockerManager is al up-to-date (versie {currentVer})."
                };
            }

            var newVer = updateInfo.TargetFullRelease.Version.ToString();
            return new UpdateCheckResult
            {
                IsInstalledApp = true,
                IsUpdateAvailable = true,
                CurrentVersion = currentVer,
                NewVersion = newVer,
                UpdateInfo = updateInfo,
                Message = $"Nieuwe versie {newVer} beschikbaar!"
            };
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult
            {
                IsInstalledApp = mgr.IsInstalled,
                CurrentVersion = currentVer,
                Message = $"Fout bij controleren op updates: {ex.Message}"
            };
        }
    }

    public async Task<bool> DownloadAndApplyUpdateAsync(UpdateInfo updateInfo, Action<int>? progress = null, CancellationToken ct = default)
    {
        var mgr = GetUpdateManager();
        try
        {
            await mgr.DownloadUpdatesAsync(updateInfo, progress);
            mgr.ApplyUpdatesAndRestart(updateInfo);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
