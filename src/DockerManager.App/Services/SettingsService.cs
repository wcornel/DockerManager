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
    public AppSettings Settings { get; private set; } = new();

    public SettingsService(ICredentialService credentialService)
    {
        _credentialService = credentialService;
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dir = Path.Combine(appData, "DockerManager");
        Directory.CreateDirectory(dir);
        _settingsFilePath = Path.Combine(dir, "settings.json");
        Load();
    }

    public void Load()
    {
        if (File.Exists(_settingsFilePath))
        {
            try
            {
                var json = File.ReadAllText(_settingsFilePath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                if (loaded != null)
                {
                    Settings = loaded;
                }
            }
            catch
            {
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

        Settings.HasGitHubToken = _credentialService.HasToken();
    }

    public void Save()
    {
        try
        {
            Settings.HasGitHubToken = _credentialService.HasToken();
            var json = JsonSerializer.Serialize(Settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_settingsFilePath, json);
        }
        catch { }
    }
}
