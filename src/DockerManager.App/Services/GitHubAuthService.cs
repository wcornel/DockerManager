using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace DockerManager.App.Services;

public class DeviceCodeResponse
{
    public string DeviceCode { get; set; } = string.Empty;
    public string UserCode { get; set; } = string.Empty;
    public string VerificationUri { get; set; } = "https://github.com/login/device";
    public int ExpiresIn { get; set; } = 900;
    public int Interval { get; set; } = 5;
}

public interface IGitHubAuthService
{
    Task<DeviceCodeResponse?> RequestDeviceCodeAsync(string clientId, string scopes = "repo read:packages read:user", CancellationToken ct = default);
    Task<string?> PollForTokenAsync(string clientId, string deviceCode, int intervalSeconds, int expiresInSeconds, IProgress<string>? progress = null, CancellationToken ct = default);
    Task<string?> FetchAuthenticatedUsernameAsync(string token, CancellationToken ct = default);
}

public class GitHubAuthService : IGitHubAuthService
{
    private readonly HttpClient _httpClient;

    public GitHubAuthService()
    {
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "DockerManager-App");
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<DeviceCodeResponse?> RequestDeviceCodeAsync(string clientId, string scopes = "repo read:packages read:user", CancellationToken ct = default)
    {
        try
        {
            var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("client_id", clientId),
                new KeyValuePair<string, string>("scope", scopes)
            });

            var response = await _httpClient.PostAsync("https://github.com/login/device/code", content, ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("device_code", out var dc) && root.TryGetProperty("user_code", out var uc))
            {
                return new DeviceCodeResponse
                {
                    DeviceCode = dc.GetString() ?? string.Empty,
                    UserCode = uc.GetString() ?? string.Empty,
                    VerificationUri = root.TryGetProperty("verification_uri", out var vu) ? vu.GetString() ?? "https://github.com/login/device" : "https://github.com/login/device",
                    ExpiresIn = root.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 900,
                    Interval = root.TryGetProperty("interval", out var iv) ? iv.GetInt32() : 5
                };
            }
        }
        catch { }

        return null;
    }

    public async Task<string?> PollForTokenAsync(string clientId, string deviceCode, int intervalSeconds, int expiresInSeconds, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var startTime = DateTime.UtcNow;
        var interval = Math.Max(intervalSeconds, 5);

        while (!ct.IsCancellationRequested && (DateTime.UtcNow - startTime).TotalSeconds < expiresInSeconds)
        {
            await Task.Delay(TimeSpan.FromSeconds(interval), ct);

            try
            {
                var content = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("client_id", clientId),
                    new KeyValuePair<string, string>("device_code", deviceCode),
                    new KeyValuePair<string, string>("grant_type", "urn:ietf:params:oauth:grant-type:device_code")
                });

                var response = await _httpClient.PostAsync("https://github.com/login/oauth/access_token", content, ct);
                var json = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("access_token", out var at))
                {
                    progress?.Report("Inloggen geslaagd!");
                    return at.GetString();
                }

                if (root.TryGetProperty("error", out var err))
                {
                    var errorType = err.GetString();
                    if (errorType == "authorization_pending")
                    {
                        progress?.Report("Wachten tot je de code bevestigt in je browser...");
                    }
                    else if (errorType == "slow_down")
                    {
                        interval += 5;
                    }
                    else if (errorType == "expired_token")
                    {
                        progress?.Report("De code is verlopen. Probeer het opnieuw.");
                        return null;
                    }
                    else if (errorType == "access_denied")
                    {
                        progress?.Report("Inloggen geannuleerd in de browser.");
                        return null;
                    }
                }
            }
            catch (Exception ex)
            {
                progress?.Report($"Verbindingsfout tijdens wachten: {ex.Message}");
            }
        }

        return null;
    }

    public async Task<string?> FetchAuthenticatedUsernameAsync(string token, CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var resp = await _httpClient.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("login", out var login))
                {
                    return login.GetString();
                }
            }
        }
        catch { }

        return null;
    }
}
