using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using DockerManager.App.Models;

namespace DockerManager.App.Services;

public interface IGitHubProfileService
{
    Task<List<ProfileModel>> LoadProfilesAsync(bool forceRefreshFromGitHub = false, CancellationToken ct = default);
    Task<List<ProfileModel>> LoadProfilesAsync(string? subfolder, bool forceRefreshFromGitHub = false, CancellationToken ct = default);
    List<ProfileModel> LoadProfilesLocally(string? subfolder = null);
    void SaveProfileLocally(ProfileModel profile, string? subfolder = null);
    void DeleteProfileLocally(string profileName, string? subfolder = null);
    Task<(bool Success, string Message)> TestGitHubConnectionAsync(string? overrideToken = null, CancellationToken ct = default);
    Task SaveProfileLocallyAsync(ProfileModel profile, CancellationToken ct = default);
    Task SaveProfileLocallyAsync(ProfileModel profile, string? subfolder, CancellationToken ct = default);
    Task DeleteProfileLocallyAsync(string profileName, string? subfolder = null, CancellationToken ct = default);
    Task EnsureSubfoldersAndMigrateAsync(IEnumerable<DockerServerEnvironment> servers, CancellationToken ct = default);
    Task<bool> PushProfileToGitHubAsync(ProfileModel profile, string? subfolder = null, CancellationToken ct = default);
}

public class GitHubProfileService : IGitHubProfileService
{
    private readonly ISettingsService _settingsService;
    private readonly ICredentialService _credentialService;
    private readonly HttpClient _httpClient;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true
    };

    public GitHubProfileService(ISettingsService settingsService, ICredentialService credentialService)
    {
        _settingsService = settingsService;
        _credentialService = credentialService;
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(8)
        };
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "DockerManager-App");
    }

    public async Task EnsureSubfoldersAndMigrateAsync(IEnumerable<DockerServerEnvironment> servers, CancellationToken ct = default)
    {
        var localDir = _settingsService.Settings.LocalProfilesFolder;
        if (string.IsNullOrWhiteSpace(localDir))
        {
            localDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "profiles");
        }

        if (!Directory.Exists(localDir))
        {
            Directory.CreateDirectory(localDir);
        }

        // 1. Ensure subdirectories exist for each server
        foreach (var server in servers)
        {
            var subfolder = string.IsNullOrWhiteSpace(server.ProfilesSubfolder) ? "local" : server.ProfilesSubfolder;
            var subDirPath = Path.Combine(localDir, subfolder);
            if (!Directory.Exists(subDirPath))
            {
                Directory.CreateDirectory(subDirPath);
            }
        }

        // 2. Migration: check for legacy root .json files (e.g. oracle.json)
        var rootJsonFiles = Directory.GetFiles(localDir, "*.json");
        if (rootJsonFiles.Length > 0)
        {
            var targetSubfolder = Path.Combine(localDir, "local");
            if (!Directory.Exists(targetSubfolder))
            {
                Directory.CreateDirectory(targetSubfolder);
            }

            foreach (var file in rootJsonFiles)
            {
                var fileName = Path.GetFileName(file);
                var targetFile = Path.Combine(targetSubfolder, fileName);

                // If target doesn't exist yet, copy it to profiles/local/
                if (!File.Exists(targetFile))
                {
                    try
                    {
                        File.Copy(file, targetFile);
                    }
                    catch { }
                }

                // If not example.json, remove from root so root stays clean
                if (!fileName.Equals("example.json", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch { }
                }
            }
        }
    }

    public Task SaveProfileLocallyAsync(ProfileModel profile, CancellationToken ct = default)
    {
        return SaveProfileLocallyAsync(profile, null, ct);
    }

    public async Task SaveProfileLocallyAsync(ProfileModel profile, string? subfolder, CancellationToken ct = default)
    {
        var localDir = _settingsService.Settings.LocalProfilesFolder;
        if (string.IsNullOrWhiteSpace(localDir))
        {
            localDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "profiles");
        }

        if (string.IsNullOrWhiteSpace(subfolder))
        {
            subfolder = _settingsService.Settings.GetActiveServer().ProfilesSubfolder;
        }

        if (profile.Registry != null && !string.IsNullOrWhiteSpace(profile.Registry.Password) && !string.IsNullOrWhiteSpace(profile.Registry.Server))
        {
            _credentialService.SaveRegistryCredential(new RegistryCredential
            {
                ServerAddress = profile.Registry.Server,
                Username = !string.IsNullOrWhiteSpace(profile.Registry.Username) ? profile.Registry.Username : "User",
                Password = profile.Registry.Password,
                DisplayName = $"{profile.Registry.Server} ({profile.Name})"
            });
        }

        var targetDir = Path.Combine(localDir, subfolder);
        Directory.CreateDirectory(targetDir);

        var safeName = string.Join("_", profile.Name.Split(Path.GetInvalidFileNameChars())).ToLowerInvariant();
        var filePath = Path.Combine(targetDir, $"{safeName}.json");
        var json = JsonSerializer.Serialize(profile, JsonOptions);
        await File.WriteAllTextAsync(filePath, json, ct);
    }

    public void SaveProfileLocally(ProfileModel profile, string? subfolder = null)
    {
        var localDir = _settingsService.Settings.LocalProfilesFolder;
        if (string.IsNullOrWhiteSpace(localDir))
        {
            localDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "profiles");
        }

        if (string.IsNullOrWhiteSpace(subfolder))
        {
            subfolder = _settingsService.Settings.GetActiveServer().ProfilesSubfolder;
        }

        if (profile.Registry != null && !string.IsNullOrWhiteSpace(profile.Registry.Password) && !string.IsNullOrWhiteSpace(profile.Registry.Server))
        {
            _credentialService.SaveRegistryCredential(new RegistryCredential
            {
                ServerAddress = profile.Registry.Server,
                Username = !string.IsNullOrWhiteSpace(profile.Registry.Username) ? profile.Registry.Username : "User",
                Password = profile.Registry.Password,
                DisplayName = $"{profile.Registry.Server} ({profile.Name})"
            });
        }

        var targetDir = Path.Combine(localDir, subfolder);
        Directory.CreateDirectory(targetDir);

        var safeName = string.Join("_", profile.Name.Split(Path.GetInvalidFileNameChars())).ToLowerInvariant();
        var filePath = Path.Combine(targetDir, $"{safeName}.json");
        var json = JsonSerializer.Serialize(profile, JsonOptions);
        File.WriteAllText(filePath, json);
    }

    public void DeleteProfileLocally(string profileName, string? subfolder = null)
    {
        if (string.IsNullOrWhiteSpace(profileName)) return;

        var localDir = _settingsService.Settings.LocalProfilesFolder;
        if (string.IsNullOrWhiteSpace(localDir))
        {
            localDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "profiles");
        }

        if (string.IsNullOrWhiteSpace(subfolder))
        {
            subfolder = _settingsService.Settings.GetActiveServer().ProfilesSubfolder;
        }

        var targetDir = Path.Combine(localDir, subfolder);
        if (!Directory.Exists(targetDir)) return;

        var safeName = string.Join("_", profileName.Split(Path.GetInvalidFileNameChars())).ToLowerInvariant();
        var candidates = new[]
        {
            Path.Combine(targetDir, $"{safeName}.json"),
            Path.Combine(targetDir, $"{profileName}.json")
        };

        foreach (var file in candidates.Distinct())
        {
            if (File.Exists(file))
            {
                try
                {
                    File.Delete(file);
                }
                catch { }
            }
        }
    }

    public List<ProfileModel> LoadProfilesLocally(string? subfolder = null)
    {
        var profiles = new List<ProfileModel>();
        var settings = _settingsService.Settings;
        var localDir = settings.LocalProfilesFolder;

        if (string.IsNullOrWhiteSpace(localDir))
        {
            localDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "profiles");
        }

        if (string.IsNullOrWhiteSpace(subfolder))
        {
            subfolder = settings.GetActiveServer().ProfilesSubfolder;
        }

        var targetDir = Path.Combine(localDir, subfolder);

        if (!Directory.Exists(targetDir))
        {
            Directory.CreateDirectory(targetDir);
        }

        if (Directory.Exists(targetDir))
        {
            var jsonFiles = Directory.GetFiles(targetDir, "*.json");
            foreach (var file in jsonFiles)
            {
                try
                {
                    var content = File.ReadAllText(file);
                    var profile = JsonSerializer.Deserialize<ProfileModel>(content, JsonOptions);
                    if (profile != null && !string.IsNullOrWhiteSpace(profile.Name))
                    {
                        HydrateProfileCredentials(profile);
                        profiles.Add(profile);
                    }
                }
                catch
                {
                    // Skip invalid file
                }
            }
        }

        if (profiles.Count == 0)
        {
            var rootExample = Path.Combine(localDir, "example.json");
            if (File.Exists(rootExample))
            {
                try
                {
                    var content = File.ReadAllText(rootExample);
                    var profile = JsonSerializer.Deserialize<ProfileModel>(content, JsonOptions);
                    if (profile != null && !string.IsNullOrWhiteSpace(profile.Name))
                    {
                        profiles.Add(profile);
                        SaveProfileLocally(profile, subfolder);
                    }
                }
                catch { }
            }

            if (profiles.Count == 0)
            {
                var defaultProfile = CreateDefaultProfile();
                profiles.Add(defaultProfile);
                try
                {
                    SaveProfileLocally(defaultProfile, subfolder);
                }
                catch { }
            }
        }

        return profiles;
    }

    public Task DeleteProfileLocallyAsync(string profileName, string? subfolder = null, CancellationToken ct = default)
    {
        DeleteProfileLocally(profileName, subfolder);
        return Task.CompletedTask;
    }

    public async Task<(bool Success, string Message)> TestGitHubConnectionAsync(string? overrideToken = null, CancellationToken ct = default)
    {
        try
        {
            var token = overrideToken ?? _credentialService.GetGitHubToken();
            var settings = _settingsService.Settings;

            if (string.IsNullOrWhiteSpace(settings.GitHubRepoOwner) || string.IsNullOrWhiteSpace(settings.GitHubRepoName))
            {
                return (false, "Vul een GitHub eigenaar en repository naam in.");
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{settings.GitHubRepoOwner}/{settings.GitHubRepoName}");
            if (!string.IsNullOrWhiteSpace(token))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(6));

            var response = await _httpClient.SendAsync(request, cts.Token);
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync(cts.Token);
                using var doc = JsonDocument.Parse(content);
                var isPrivate = doc.RootElement.TryGetProperty("private", out var priv) && priv.GetBoolean();
                return (true, $"Verbinding geslaagd! Repository '{settings.GitHubRepoName}' gevonden ({(isPrivate ? "Private" : "Public")}).");
            }
            else
            {
                var error = await response.Content.ReadAsStringAsync(cts.Token);
                return (false, $"GitHub HTTP fout: {response.StatusCode} ({response.ReasonPhrase})");
            }
        }
        catch (TaskCanceledException)
        {
            return (false, "Time-out bij verbinden met GitHub (controleer internetverbinding).");
        }
        catch (Exception ex)
        {
            return (false, $"Fout bij verbinden met GitHub: {ex.Message}");
        }
    }

    public Task<List<ProfileModel>> LoadProfilesAsync(bool forceRefreshFromGitHub = false, CancellationToken ct = default)
    {
        return LoadProfilesAsync(null, forceRefreshFromGitHub, ct);
    }

    public async Task<List<ProfileModel>> LoadProfilesAsync(string? subfolder, bool forceRefreshFromGitHub = false, CancellationToken ct = default)
    {
        var profiles = new List<ProfileModel>();
        var settings = _settingsService.Settings;
        var localDir = settings.LocalProfilesFolder;

        if (string.IsNullOrWhiteSpace(localDir))
        {
            localDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "profiles");
        }

        if (string.IsNullOrWhiteSpace(subfolder))
        {
            subfolder = settings.GetActiveServer().ProfilesSubfolder;
        }

        var targetDir = Path.Combine(localDir, subfolder);

        if (!Directory.Exists(targetDir))
        {
            Directory.CreateDirectory(targetDir);
        }

        if (Directory.Exists(targetDir))
        {
            var jsonFiles = Directory.GetFiles(targetDir, "*.json");
            foreach (var file in jsonFiles)
            {
                try
                {
                    var content = await File.ReadAllTextAsync(file, ct);
                    var profile = JsonSerializer.Deserialize<ProfileModel>(content, JsonOptions);
                    if (profile != null && !string.IsNullOrWhiteSpace(profile.Name))
                    {
                        HydrateProfileCredentials(profile);
                        profiles.Add(profile);
                    }
                }
                catch
                {
                    // Skip invalid file
                }
            }
        }

        // 2. If force refresh requested from GitHub, fetch and sync
        if (forceRefreshFromGitHub)
        {
            try
            {
                var remoteProfiles = await FetchProfilesFromGitHubAsync(subfolder, ct);
                if (remoteProfiles.Count > 0)
                {
                    foreach (var remote in remoteProfiles)
                    {
                        HydrateProfileCredentials(remote);
                        await SaveProfileLocallyAsync(remote, subfolder, ct);

                        var existingIdx = profiles.FindIndex(p => p.Name.Equals(remote.Name, StringComparison.OrdinalIgnoreCase));
                        if (existingIdx >= 0)
                        {
                            profiles[existingIdx] = remote;
                        }
                        else
                        {
                            profiles.Add(remote);
                        }
                    }
                }
            }
            catch { }
        }

        // If target folder is empty, check if example.json exists in root to copy as starter, or create default
        if (profiles.Count == 0)
        {
            var rootExample = Path.Combine(localDir, "example.json");
            if (File.Exists(rootExample))
            {
                try
                {
                    var content = await File.ReadAllTextAsync(rootExample, ct);
                    var profile = JsonSerializer.Deserialize<ProfileModel>(content, JsonOptions);
                    if (profile != null && !string.IsNullOrWhiteSpace(profile.Name))
                    {
                        HydrateProfileCredentials(profile);
                        profiles.Add(profile);
                        await SaveProfileLocallyAsync(profile, subfolder, ct);
                    }
                }
                catch { }
            }

            if (profiles.Count == 0)
            {
                var defaultProfile = CreateDefaultProfile();
                profiles.Add(defaultProfile);
                try
                {
                    await SaveProfileLocallyAsync(defaultProfile, subfolder, ct);
                }
                catch { }
            }
        }

        return profiles;
    }

    private void HydrateProfileCredentials(ProfileModel profile)
    {
        if (profile.Registry != null && !string.IsNullOrWhiteSpace(profile.Registry.Server) && string.IsNullOrWhiteSpace(profile.Registry.Password))
        {
            var cred = _credentialService.GetCredentialForImage(profile.Registry.Server);
            if (cred != null && !string.IsNullOrWhiteSpace(cred.Password))
            {
                profile.Registry.Password = cred.Password;
            }
        }
    }

    public async Task<bool> PushProfileToGitHubAsync(ProfileModel profile, string? subfolder = null, CancellationToken ct = default)
    {
        try
        {
            var settings = _settingsService.Settings;
            var token = _credentialService.GetGitHubToken();
            if (string.IsNullOrWhiteSpace(settings.GitHubRepoOwner) || string.IsNullOrWhiteSpace(settings.GitHubRepoName) || string.IsNullOrWhiteSpace(token))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(subfolder))
            {
                subfolder = settings.GetActiveServer().ProfilesSubfolder;
            }

            var folder = string.IsNullOrWhiteSpace(subfolder) ? settings.GitHubProfilesFolder : $"{settings.GitHubProfilesFolder}/{subfolder}";
            var safeName = string.Join("_", profile.Name.Split(Path.GetInvalidFileNameChars())).ToLowerInvariant();
            var path = $"{folder}/{safeName}.json";
            var json = JsonSerializer.Serialize(profile, JsonOptions);
            var base64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json));

            string? sha = null;
            var getReq = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{settings.GitHubRepoOwner}/{settings.GitHubRepoName}/contents/{path}?ref={settings.GitHubBranch}");
            getReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            getReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            var getResp = await _httpClient.SendAsync(getReq, ct);
            if (getResp.IsSuccessStatusCode)
            {
                var content = await getResp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(content);
                if (doc.RootElement.TryGetProperty("sha", out var shaElem))
                {
                    sha = shaElem.GetString();
                }
            }

            var putReq = new HttpRequestMessage(HttpMethod.Put, $"https://api.github.com/repos/{settings.GitHubRepoOwner}/{settings.GitHubRepoName}/contents/{path}");
            putReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            putReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            var payload = new Dictionary<string, object>
            {
                ["message"] = $"Update profile '{profile.Name}' via DockerManager",
                ["content"] = base64,
                ["branch"] = settings.GitHubBranch
            };
            if (!string.IsNullOrWhiteSpace(sha))
            {
                payload["sha"] = sha;
            }

            putReq.Content = new StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json");
            var putResp = await _httpClient.SendAsync(putReq, ct);
            return putResp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private async Task<List<ProfileModel>> FetchProfilesFromGitHubAsync(string? subfolder, CancellationToken ct)
    {
        var settings = _settingsService.Settings;
        var token = _credentialService.GetGitHubToken();

        if (string.IsNullOrWhiteSpace(settings.GitHubRepoOwner) || string.IsNullOrWhiteSpace(settings.GitHubRepoName))
        {
            return new List<ProfileModel>();
        }

        var folderPath = string.IsNullOrWhiteSpace(subfolder)
            ? settings.GitHubProfilesFolder
            : $"{settings.GitHubProfilesFolder}/{subfolder}";

        var results = await FetchProfilesFromFolderAsync(folderPath, token, ct);
        if (results.Count == 0 && !string.IsNullOrWhiteSpace(subfolder) && !subfolder.Equals(settings.GitHubProfilesFolder, StringComparison.OrdinalIgnoreCase))
        {
            // Fallback to base profiles folder if subfolder doesn't exist on GitHub yet
            results = await FetchProfilesFromFolderAsync(settings.GitHubProfilesFolder, token, ct);
        }

        return results;
    }

    private async Task<List<ProfileModel>> FetchProfilesFromFolderAsync(string folderPath, string? token, CancellationToken ct)
    {
        var results = new List<ProfileModel>();
        var settings = _settingsService.Settings;
        var url = $"https://api.github.com/repos/{settings.GitHubRepoOwner}/{settings.GitHubRepoName}/contents/{folderPath}?ref={settings.GitHubBranch}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(6));

        var response = await _httpClient.SendAsync(request, cts.Token);
        if (!response.IsSuccessStatusCode)
        {
            return results;
        }

        var json = await response.Content.ReadAsStringAsync(cts.Token);
        using var doc = JsonDocument.Parse(json);

        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return results;
        }

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var type = item.GetProperty("type").GetString();
            var name = item.GetProperty("name").GetString();
            var downloadUrl = item.TryGetProperty("download_url", out var dl) ? dl.GetString() : null;

            if (type == "file" && name != null && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(downloadUrl))
            {
                try
                {
                    using var fileRequest = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
                    if (!string.IsNullOrWhiteSpace(token))
                    {
                        fileRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    }

                    var fileResponse = await _httpClient.SendAsync(fileRequest, cts.Token);
                    if (fileResponse.IsSuccessStatusCode)
                    {
                        var fileContent = await fileResponse.Content.ReadAsStringAsync(cts.Token);
                        var profile = JsonSerializer.Deserialize<ProfileModel>(fileContent, JsonOptions);
                        if (profile != null && !string.IsNullOrWhiteSpace(profile.Name))
                        {
                            HydrateProfileCredentials(profile);
                            results.Add(profile);
                        }
                    }
                }
                catch
                {
                    // Skip file on failure
                }
            }
        }

        return results;
    }

    private ProfileModel CreateDefaultProfile()
    {
        return new ProfileModel
        {
            Name = "Default",
            Description = "Standaard container profiel",
            AutoStopPreviousOnSwitch = false,
            Services = new List<ServiceDefinition>()
        };
    }
}
