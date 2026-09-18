using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using DockerManager.App.Models;

namespace DockerManager.App.Services;

public interface IGitHubProfileService
{
    Task<List<ProfileModel>> LoadProfilesAsync(bool forceRefreshFromGitHub = false, CancellationToken ct = default);
    Task<(bool Success, string Message)> TestGitHubConnectionAsync(string? overrideToken = null, CancellationToken ct = default);
    Task SaveProfileLocallyAsync(ProfileModel profile, CancellationToken ct = default);
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

    public async Task SaveProfileLocallyAsync(ProfileModel profile, CancellationToken ct = default)
    {
        var localDir = _settingsService.Settings.LocalProfilesFolder;
        if (string.IsNullOrWhiteSpace(localDir))
        {
            localDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "profiles");
        }

        Directory.CreateDirectory(localDir);
        var safeName = string.Join("_", profile.Name.Split(Path.GetInvalidFileNameChars())).ToLowerInvariant();
        var filePath = Path.Combine(localDir, $"{safeName}.json");
        var json = JsonSerializer.Serialize(profile, JsonOptions);
        await File.WriteAllTextAsync(filePath, json, ct);
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

    public async Task<List<ProfileModel>> LoadProfilesAsync(bool forceRefreshFromGitHub = false, CancellationToken ct = default)
    {
        var profiles = new List<ProfileModel>();
        var settings = _settingsService.Settings;
        var localDir = settings.LocalProfilesFolder;

        if (string.IsNullOrWhiteSpace(localDir))
        {
            localDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "profiles");
        }

        // Load strictly from local folder
        if (!string.IsNullOrWhiteSpace(localDir) && Directory.Exists(localDir))
        {
            var jsonFiles = Directory.GetFiles(localDir, "*.json");
            foreach (var file in jsonFiles)
            {
                try
                {
                    var content = await File.ReadAllTextAsync(file, ct);
                    var profile = JsonSerializer.Deserialize<ProfileModel>(content, JsonOptions);
                    if (profile != null && !string.IsNullOrWhiteSpace(profile.Name))
                    {
                        profiles.Add(profile);
                    }
                }
                catch
                {
                    // Skip invalid file
                }
            }
        }

        // If local folder doesn't exist or is completely empty (first startup), create profiles folder and default.json
        if (profiles.Count == 0)
        {
            if (!Directory.Exists(localDir))
            {
                Directory.CreateDirectory(localDir);
            }

            var defaultProfile = CreateDefaultProfile();
            profiles.Add(defaultProfile);
            try
            {
                await SaveProfileLocallyAsync(defaultProfile, ct);
            }
            catch { }
        }

        return profiles;
    }

    private async Task<List<ProfileModel>> FetchProfilesFromGitHubAsync(CancellationToken ct)
    {
        var results = new List<ProfileModel>();
        var settings = _settingsService.Settings;
        var token = _credentialService.GetGitHubToken();

        if (string.IsNullOrWhiteSpace(settings.GitHubRepoOwner) || string.IsNullOrWhiteSpace(settings.GitHubRepoName))
        {
            return results;
        }

        var url = $"https://api.github.com/repos/{settings.GitHubRepoOwner}/{settings.GitHubRepoName}/contents/{settings.GitHubProfilesFolder}?ref={settings.GitHubBranch}";

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
