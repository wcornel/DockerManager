using System.IO;
using System.Text.Json;
using DockerManager.App.Models;

namespace DockerManager.App.Services;

public interface ISettingsService
{
    AppSettings Settings { get; }
    void Save();
    void Load();
}

public class SettingsService : ISettingsService
{
    private readonly string _settingsFilePath;
    private readonly ICredentialService _credentialService;
    private readonly object _syncLock = new();
    private static readonly JsonSerializerOptions WriteJsonOptions = new() { WriteIndented = true };
    public AppSettings Settings { get; private set; } = new();

    public SettingsService(ICredentialService credentialService, string? customSettingsPath = null)
    {
        _credentialService = credentialService;
        if (!string.IsNullOrWhiteSpace(customSettingsPath))
        {
            _settingsFilePath = customSettingsPath;
            var dir = Path.GetDirectoryName(customSettingsPath);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
        }
        else
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var dir = Path.Combine(appData, "DockerManager");
            Directory.CreateDirectory(dir);
            _settingsFilePath = Path.Combine(dir, "settings.json");
        }
        Load();
    }

    public void Load()
    {
        lock (_syncLock)
        {
            var migratedLegacySecrets = false;

            if (File.Exists(_settingsFilePath))
            {
                try
                {
                    var json = File.ReadAllText(_settingsFilePath);
                    var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                    Settings = loaded ?? new AppSettings();
                    migratedLegacySecrets = TryMigrateLegacyCloudflareSecrets(json);
                }
                catch (Exception ex)
                {
                    AppLogger.LogWarning(nameof(SettingsService), $"Failed to load settings from '{_settingsFilePath}', falling back to defaults.", ex);
                    TryBackupCorruptSettingsFile();
                    Settings = new AppSettings();
                }
            }
            else
            {
                Settings = new AppSettings();
            }

            // Set default local profiles folder if not configured
            if (string.IsNullOrWhiteSpace(Settings.LocalProfilesFolder))
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var localProfilesDir = Path.Combine(baseDir, "profiles");
                if (!Directory.Exists(localProfilesDir))
                {
                    // check project root or relative path
                    var fallback = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..", "profiles"));
                    if (Directory.Exists(fallback))
                    {
                        localProfilesDir = fallback;
                    }
                    else
                    {
                        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                        localProfilesDir = Path.Combine(appData, "DockerManager", "profiles");
                        Directory.CreateDirectory(localProfilesDir);
                    }
                }
                Settings.LocalProfilesFolder = localProfilesDir;
            }

            // Hydrate encrypted Cloudflare secrets from DPAPI
            Settings.CloudflareTunnelToken = _credentialService.GetCloudflareTunnelToken() ?? string.Empty;
            if (Settings.Servers != null)
            {
                foreach (var srv in Settings.Servers)
                {
                    if (srv == null || string.IsNullOrWhiteSpace(srv.Id)) continue;
                    srv.CloudflareApiToken = _credentialService.GetServerCloudflareApiToken(srv.Id) ?? string.Empty;
                    srv.CloudflareTunnelToken = _credentialService.GetServerCloudflareTunnelToken(srv.Id) ?? string.Empty;
                }
            }

            Settings.HasGitHubToken = _credentialService.HasToken();
            Settings.HasCloudflareToken = !string.IsNullOrWhiteSpace(_credentialService.GetCloudflareApiToken()) ||
                (Settings.Servers?.Any(s => s != null && !string.IsNullOrWhiteSpace(s.CloudflareApiToken)) ?? false);

            if (migratedLegacySecrets)
            {
                Save();
            }
        }
    }

    public void Save()
    {
        lock (_syncLock)
        {
            try
            {
                _credentialService.SaveCloudflareTunnelToken(Settings.CloudflareTunnelToken ?? string.Empty);
                if (Settings.Servers != null)
                {
                    foreach (var srv in Settings.Servers)
                    {
                        if (srv == null || string.IsNullOrWhiteSpace(srv.Id)) continue;
                        _credentialService.SaveServerCloudflareApiToken(srv.Id, srv.CloudflareApiToken ?? string.Empty);
                        _credentialService.SaveServerCloudflareTunnelToken(srv.Id, srv.CloudflareTunnelToken ?? string.Empty);
                    }
                }

                Settings.HasGitHubToken = _credentialService.HasToken();
                Settings.HasCloudflareToken = !string.IsNullOrWhiteSpace(_credentialService.GetCloudflareApiToken()) ||
                    (Settings.Servers?.Any(s => s != null && !string.IsNullOrWhiteSpace(s.CloudflareApiToken)) ?? false);

                var json = JsonSerializer.Serialize(Settings, WriteJsonOptions);
                var tmpPath = _settingsFilePath + ".tmp";
                File.WriteAllText(tmpPath, json);
                File.Move(tmpPath, _settingsFilePath, overwrite: true);
            }
            catch (Exception ex)
            {
                AppLogger.LogError(nameof(SettingsService), ex, $"Failed to save settings to '{_settingsFilePath}'");
            }
        }
    }

    private bool TryMigrateLegacyCloudflareSecrets(string rawJson)
    {
        var migrated = false;
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;

            if (TryGetStringPropertyIgnoreCase(root, "CloudflareTunnelToken", out var globalTunnelToken) &&
                !string.IsNullOrWhiteSpace(globalTunnelToken))
            {
                _credentialService.SaveCloudflareTunnelToken(globalTunnelToken);
                migrated = true;
            }

            if (TryGetPropertyIgnoreCase(root, "Servers", out var serversElem) &&
                serversElem.ValueKind == JsonValueKind.Array)
            {
                foreach (var srvElem in serversElem.EnumerateArray())
                {
                    if (srvElem.ValueKind != JsonValueKind.Object) continue;
                    if (!TryGetStringPropertyIgnoreCase(srvElem, "Id", out var srvId) || string.IsNullOrWhiteSpace(srvId))
                        continue;

                    if (TryGetStringPropertyIgnoreCase(srvElem, "CloudflareApiToken", out var srvApiToken) &&
                        !string.IsNullOrWhiteSpace(srvApiToken))
                    {
                        _credentialService.SaveServerCloudflareApiToken(srvId, srvApiToken);
                        migrated = true;
                    }

                    if (TryGetStringPropertyIgnoreCase(srvElem, "CloudflareTunnelToken", out var srvTunnelToken) &&
                        !string.IsNullOrWhiteSpace(srvTunnelToken))
                    {
                        _credentialService.SaveServerCloudflareTunnelToken(srvId, srvTunnelToken);
                        migrated = true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogWarning(nameof(SettingsService), "Legacy Cloudflare secret migration failed", ex);
        }

        return migrated;
    }

    private void TryBackupCorruptSettingsFile()
    {
        try
        {
            if (File.Exists(_settingsFilePath))
            {
                var backupPath = $"{_settingsFilePath}.corrupt.{DateTime.UtcNow:yyyyMMddHHmmss}";
                File.Copy(_settingsFilePath, backupPath, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogWarning(nameof(SettingsService), $"Failed to backup corrupt settings file '{_settingsFilePath}'", ex);
        }
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement obj, string name, out JsonElement value)
    {
        if (obj.TryGetProperty(name, out value))
        {
            return true;
        }

        foreach (var prop in obj.EnumerateObject())
        {
            if (prop.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static bool TryGetStringPropertyIgnoreCase(JsonElement obj, string name, out string? value)
    {
        value = null;
        if (TryGetPropertyIgnoreCase(obj, name, out var elem) && elem.ValueKind == JsonValueKind.String)
        {
            value = elem.GetString();
            return true;
        }
        return false;
    }
}
