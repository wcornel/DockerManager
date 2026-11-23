using System.Threading;
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

public class AppUpdateService : IAppUpdateService, IDisposable
{
    private readonly SemaphoreSlim _updateLock = new(1, 1);
    private bool _disposed;

    public AppUpdateService(ISettingsService settingsService, ICredentialService credentialService)
    {
        _ = settingsService;
        _ = credentialService;
    }

    private static UpdateManager GetUpdateManager()
    {
        const string repoUrl = "https://github.com/wcornel/DockerManager";
        var source = new GithubSource(repoUrl, accessToken: null, prerelease: false);
        return new UpdateManager(source);
    }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken ct = default)
    {
        var loc = LocalizationService.Instance;
        await _updateLock.WaitAsync(ct);
        bool isInstalled = false;
        string currentVer = "1.0.0";
        try
        {
            ct.ThrowIfCancellationRequested();
            var mgr = GetUpdateManager();
            isInstalled = mgr.IsInstalled;
            currentVer = mgr.CurrentVersion?.ToString() ?? "1.0.0";

            if (!mgr.IsInstalled)
            {
                return new UpdateCheckResult
                {
                    IsInstalledApp = false,
                    CurrentVersion = currentVer,
                    Message = loc.Get("Update_PortableMsg")
                };
            }

            var updateInfo = await mgr.CheckForUpdatesAsync();
            ct.ThrowIfCancellationRequested();

            if (updateInfo == null)
            {
                return new UpdateCheckResult
                {
                    IsInstalledApp = true,
                    IsUpdateAvailable = false,
                    CurrentVersion = currentVer,
                    Message = loc.Get("Update_UpToDate", currentVer)
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
                Message = loc.Get("Update_Available", newVer)
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLogger.LogError(nameof(AppUpdateService), ex, "Update check failed");
            return new UpdateCheckResult
            {
                IsInstalledApp = isInstalled,
                CurrentVersion = currentVer,
                Message = loc.Get("Update_CheckError", ex.Message)
            };
        }
        finally
        {
            _updateLock.Release();
        }
    }

    public async Task<bool> DownloadAndApplyUpdateAsync(UpdateInfo updateInfo, Action<int>? progress = null, CancellationToken ct = default)
    {
        await _updateLock.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            var mgr = GetUpdateManager();
            await mgr.DownloadUpdatesAsync(updateInfo, progress, ct);
            ct.ThrowIfCancellationRequested();

            if (System.Windows.Application.Current != null)
            {
                mgr.WaitExitThenApplyUpdates(updateInfo, silent: false, restart: true);
                System.Windows.Application.Current.Dispatcher.Invoke(() => System.Windows.Application.Current.Shutdown());
            }
            else
            {
                mgr.ApplyUpdatesAndRestart(updateInfo);
            }
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLogger.LogError(nameof(AppUpdateService), ex, "Download or apply update failed");
            return false;
        }
        finally
        {
            _updateLock.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _updateLock.Dispose();
        GC.SuppressFinalize(this);
    }
}
