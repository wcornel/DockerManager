using System.IO;
using Docker.DotNet;
using Docker.DotNet.Models;
using DockerManager.App.Models;

namespace DockerManager.App.Services;

public partial class DockerService
{
    public async Task<DockerStartupDiagnosis> DiagnoseDockerStartupAsync(CancellationToken ct = default)
    {
        var diagnosis = new DockerStartupDiagnosis();
        var (connected, info) = await PingDockerAsync(ct);
        diagnosis.IsConnected = connected;

        if (connected)
        {
            diagnosis.DiagnosisSummary = LocalizationService.Instance.Get("Docker_Active", info);
            return diagnosis;
        }

        var loc = LocalizationService.Instance;
        var activeServer = _settingsService.Settings.GetActiveServer();
        if (activeServer.HostType.Equals("Tcp", StringComparison.OrdinalIgnoreCase))
        {
            diagnosis.IsDockerDesktopInstalled = false;
            diagnosis.DiagnosisSummary = loc.Get("Docker_Diag_RemoteUnreachable", activeServer.Name, activeServer.TcpUrl);
            diagnosis.DetailedTip = loc.Get("Docker_Diag_RemoteUnreachableTip");
            return diagnosis;
        }

        // 1. Check Docker Desktop installation path
        var commonPaths = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Docker", "Docker", "Docker Desktop.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Docker", "Docker", "Docker Desktop.exe"),
            @"C:\Program Files\Docker\Docker\Docker Desktop.exe"
        };

        foreach (var p in commonPaths)
        {
            if (File.Exists(p))
            {
                diagnosis.IsDockerDesktopInstalled = true;
                diagnosis.DockerDesktopExecutablePath = p;
                break;
            }
        }

        // 2. Check Docker Desktop settings.json (%APPDATA%\Docker\settings.json)
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dockerSettingsFile = Path.Combine(appData, "Docker", "settings.json");
        if (File.Exists(dockerSettingsFile))
        {
            try
            {
                var json = File.ReadAllText(dockerSettingsFile);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("openWithWindows", out var prop))
                {
                    diagnosis.IsDockerDesktopAutoStartEnabled = prop.GetBoolean();
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning(nameof(DockerService), "Failed to read Docker Desktop settings.json", ex);
            }
        }

        // 3. Check Windows Service (com.docker.service or docker)
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\com.docker.service")
                         ?? Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\docker");
            if (key != null)
            {
                var startVal = key.GetValue("Start");
                if (startVal is int val)
                {
                    diagnosis.WindowsServiceStartMode = val switch
                    {
                        2 => "Automatic",
                        3 => "Manual",
                        4 => "Disabled",
                        _ => $"StartMode_{val}"
                    };
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogWarning(nameof(DockerService), "Failed to inspect Docker Windows service registry key", ex);
        }

        // Formulate user-friendly explanation & actionable advice
        if (diagnosis.WindowsServiceStartMode == "Disabled")
        {
            diagnosis.DiagnosisSummary = loc.Get("Docker_Diag_ServiceDisabled");
            diagnosis.DetailedTip = loc.Get("Docker_Diag_ServiceDisabledTip");
        }
        else if (diagnosis.IsDockerDesktopAutoStartEnabled == false)
        {
            diagnosis.DiagnosisSummary = loc.Get("Docker_Diag_NotAutoStart");
            diagnosis.DetailedTip = loc.Get("Docker_Diag_NotAutoStartTip");
        }
        else if (diagnosis.WindowsServiceStartMode == "Manual")
        {
            diagnosis.DiagnosisSummary = loc.Get("Docker_Diag_ServiceManual");
            diagnosis.DetailedTip = loc.Get("Docker_Diag_ServiceManualTip");
        }
        else if (diagnosis.IsDockerDesktopInstalled)
        {
            diagnosis.DiagnosisSummary = loc.Get("Docker_Diag_StartingUp");
            diagnosis.DetailedTip = loc.Get("Docker_Diag_StartingUpTip");
        }
        else
        {
            diagnosis.DiagnosisSummary = loc.Get("Docker_NotConnected", _settingsService.Settings.GetEffectiveDockerUri());
            diagnosis.DetailedTip = loc.Get("Docker_Diag_UnreachableTip");
        }

        return diagnosis;
    }

    public Task<bool> TryLaunchDockerDesktopAsync()
    {
        try
        {
            var commonPaths = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Docker", "Docker", "Docker Desktop.exe"),
                @"C:\Program Files\Docker\Docker\Docker Desktop.exe"
            };

            foreach (var p in commonPaths)
            {
                if (File.Exists(p))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(p) { UseShellExecute = true });
                    return Task.FromResult(true);
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogWarning(nameof(DockerService), "Failed to launch Docker Desktop", ex);
        }

        return Task.FromResult(false);
    }
}
