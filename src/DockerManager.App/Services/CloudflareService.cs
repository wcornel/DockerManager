using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using DockerManager.App.Models;

namespace DockerManager.App.Services;

public class CloudflareTunnelInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime? CreatedAt { get; set; }

    public string StatusIcon => Status.ToLowerInvariant() switch
    {
        "healthy" => "🟢",
        "inactive" => "⚪",
        "down" => "🔴",
        _ => "🟡"
    };

    public string DisplayName => $"{StatusIcon} {Name} ({(Id.Length > 8 ? Id[..8] : Id)}...)";

    public override string ToString() => DisplayName;
}

public interface ICloudflareService
{
    Task<(bool Success, string Message, List<CloudflareTunnelInfo> Tunnels)> ListTunnelsAsync(
        string? overrideToken = null,
        string? overrideAccountId = null,
        CancellationToken ct = default);

    Task<(bool Success, string Message, CloudflareTunnelInfo? CreatedTunnel)> CreateTunnelAsync(
        string tunnelName,
        string? overrideToken = null,
        string? overrideAccountId = null,
        CancellationToken ct = default);

    Task<(bool Success, string Message)> DeleteTunnelAsync(
        string tunnelId,
        string? overrideToken = null,
        string? overrideAccountId = null,
        CancellationToken ct = default);

    Task<(bool Success, string Message, string? TunnelName)> TestConnectionAsync(
        string? overrideToken = null,
        string? overrideAccountId = null,
        string? overrideTunnelId = null,
        CancellationToken ct = default);

    Task<string?> FetchTunnelTokenAsync(
        string? overrideToken = null,
        string? overrideAccountId = null,
        string? overrideTunnelId = null,
        CancellationToken ct = default);

    Task<(bool Success, string Message, string? FullHostname)> RegisterSubdomainAsync(
        string subdomain,
        int hostPort,
        ProfileCloudflareConfig? profileConfig = null,
        CancellationToken ct = default);

    Task<(bool Success, string Message)> UnregisterSubdomainAsync(
        string hostname,
        ProfileCloudflareConfig? profileConfig = null,
        CancellationToken ct = default);

    bool EnsureCloudflaredServiceInProfile(ProfileModel profile, string? customTunnelToken = null);
}

public class CloudflareService : ICloudflareService
{
    private readonly ISettingsService _settingsService;
    private readonly ICredentialService _credentialService;
    private readonly HttpClient _httpClient;

    private const string BaseApiUrl = "https://api.cloudflare.com/client/v4";

    public CloudflareService(ISettingsService settingsService, ICredentialService credentialService, HttpClient? httpClient = null)
    {
        _settingsService = settingsService;
        _credentialService = credentialService;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        if (!_httpClient.DefaultRequestHeaders.Contains("User-Agent"))
        {
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "DockerManager-App");
        }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string url, string token)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        return request;
    }

    public async Task<(bool Success, string Message, List<CloudflareTunnelInfo> Tunnels)> ListTunnelsAsync(
        string? overrideToken = null,
        string? overrideAccountId = null,
        CancellationToken ct = default)
    {
        var tunnels = new List<CloudflareTunnelInfo>();
        try
        {
            var token = overrideToken ?? _credentialService.GetCloudflareApiToken();
            var accountId = overrideAccountId ?? _settingsService.Settings.CloudflareAccountId;

            if (string.IsNullOrWhiteSpace(token))
            {
                return (false, "Cloudflare API Token ontbreekt.", tunnels);
            }
            if (string.IsNullOrWhiteSpace(accountId))
            {
                return (false, "Cloudflare Account ID ontbreekt.", tunnels);
            }

            var url = $"{BaseApiUrl}/accounts/{accountId.Trim()}/cfd_tunnel?is_deleted=false&per_page=50";
            using var req = CreateRequest(HttpMethod.Get, url, token);
            var response = await _httpClient.SendAsync(req, ct);
            var json = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                var errMsg = TryExtractErrorMessage(json) ?? $"{response.StatusCode} ({response.ReasonPhrase})";
                return (false, $"Fout bij ophalen van tunnels: {errMsg}", tunnels);
            }

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("result", out var resultArr) && resultArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in resultArr.EnumerateArray())
                {
                    var id = t.GetProperty("id").GetString() ?? string.Empty;
                    var name = t.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
                    var status = t.TryGetProperty("status", out var s) ? s.GetString() ?? string.Empty : "inactive";
                    DateTime? createdAt = null;
                    if (t.TryGetProperty("created_at", out var ca) && ca.TryGetDateTime(out var dt))
                    {
                        createdAt = dt;
                    }

                    tunnels.Add(new CloudflareTunnelInfo
                    {
                        Id = id,
                        Name = name,
                        Status = status,
                        CreatedAt = createdAt
                    });
                }
            }

            return (true, $"{tunnels.Count} tunnel(s) gevonden.", tunnels);
        }
        catch (Exception ex)
        {
            return (false, $"Uitzondering bij ophalen van tunnels: {ex.Message}", tunnels);
        }
    }

    public async Task<(bool Success, string Message, CloudflareTunnelInfo? CreatedTunnel)> CreateTunnelAsync(
        string tunnelName,
        string? overrideToken = null,
        string? overrideAccountId = null,
        CancellationToken ct = default)
    {
        try
        {
            var token = overrideToken ?? _credentialService.GetCloudflareApiToken();
            var accountId = overrideAccountId ?? _settingsService.Settings.CloudflareAccountId;

            if (string.IsNullOrWhiteSpace(token))
            {
                return (false, "Cloudflare API Token ontbreekt.", null);
            }
            if (string.IsNullOrWhiteSpace(accountId))
            {
                return (false, "Cloudflare Account ID ontbreekt.", null);
            }
            if (string.IsNullOrWhiteSpace(tunnelName))
            {
                return (false, "Tunnel naam mag niet leeg zijn.", null);
            }

            var cleanName = tunnelName.Trim();

            // Generate 32 cryptographically random bytes for tunnel_secret (base64)
            var secretBytes = new byte[32];
            System.Security.Cryptography.RandomNumberGenerator.Fill(secretBytes);
            var secretBase64 = Convert.ToBase64String(secretBytes);

            var url = $"{BaseApiUrl}/accounts/{accountId.Trim()}/cfd_tunnel";
            using var req = CreateRequest(HttpMethod.Post, url, token);

            var payload = new
            {
                name = cleanName,
                tunnel_secret = secretBase64,
                config_src = "cloudflare"
            };

            req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(req, ct);
            var json = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                var errMsg = TryExtractErrorMessage(json) ?? $"{response.StatusCode} ({response.ReasonPhrase})";
                return (false, $"Fout bij aanmaken van tunnel: {errMsg}", null);
            }

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("result", out var resElem))
            {
                return (false, "Geen resultaat ontvangen van Cloudflare API na aanmaken van tunnel.", null);
            }

            var id = resElem.GetProperty("id").GetString() ?? string.Empty;
            var name = resElem.TryGetProperty("name", out var n) ? n.GetString() ?? cleanName : cleanName;

            // Fetch and save the tunnel run token automatically
            var runToken = await FetchTunnelTokenAsync(token, accountId, id, ct);
            if (!string.IsNullOrWhiteSpace(runToken))
            {
                _settingsService.Settings.CloudflareTunnelToken = runToken;
            }

            _settingsService.Settings.CloudflareTunnelId = id;
            _settingsService.Save();

            // Initialize default empty ingress rule (404 fallback)
            try
            {
                var cfgUrl = $"{BaseApiUrl}/accounts/{accountId.Trim()}/cfd_tunnel/{id}/configurations";
                using var cfgReq = CreateRequest(HttpMethod.Put, cfgUrl, token);
                var cfgPayload = new
                {
                    config = new
                    {
                        ingress = new[]
                        {
                            new { service = "http_status:404" }
                        }
                    }
                };
                cfgReq.Content = new StringContent(JsonSerializer.Serialize(cfgPayload), Encoding.UTF8, "application/json");
                await _httpClient.SendAsync(cfgReq, ct);
            }
            catch { }

            var created = new CloudflareTunnelInfo
            {
                Id = id,
                Name = name,
                Status = "inactive",
                CreatedAt = DateTime.UtcNow
            };

            return (true, $"🎉 Tunnel '{name}' succesvol aangemaakt in Cloudflare!", created);
        }
        catch (Exception ex)
        {
            return (false, $"Uitzondering bij aanmaken van tunnel: {ex.Message}", null);
        }
    }

    public async Task<(bool Success, string Message)> DeleteTunnelAsync(
        string tunnelId,
        string? overrideToken = null,
        string? overrideAccountId = null,
        CancellationToken ct = default)
    {
        try
        {
            var token = overrideToken ?? _credentialService.GetCloudflareApiToken();
            var accountId = overrideAccountId ?? _settingsService.Settings.CloudflareAccountId;

            if (string.IsNullOrWhiteSpace(token)) return (false, "Cloudflare API Token ontbreekt.");
            if (string.IsNullOrWhiteSpace(accountId)) return (false, "Cloudflare Account ID ontbreekt.");
            if (string.IsNullOrWhiteSpace(tunnelId)) return (false, "Tunnel ID ontbreekt.");

            var url = $"{BaseApiUrl}/accounts/{accountId.Trim()}/cfd_tunnel/{tunnelId.Trim()}?cascade=true";
            using var req = CreateRequest(HttpMethod.Delete, url, token);
            var response = await _httpClient.SendAsync(req, ct);
            var json = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                var errMsg = TryExtractErrorMessage(json) ?? $"{response.StatusCode} ({response.ReasonPhrase})";
                return (false, $"Fout bij verwijderen van tunnel: {errMsg}");
            }

            if (_settingsService.Settings.CloudflareTunnelId.Equals(tunnelId, StringComparison.OrdinalIgnoreCase))
            {
                _settingsService.Settings.CloudflareTunnelId = string.Empty;
                _settingsService.Settings.CloudflareTunnelToken = string.Empty;
                _settingsService.Save();
            }

            return (true, "🗑️ Tunnel succesvol verwijderd uit Cloudflare.");
        }
        catch (Exception ex)
        {
            return (false, $"Uitzondering bij verwijderen van tunnel: {ex.Message}");
        }
    }

    public async Task<(bool Success, string Message, string? TunnelName)> TestConnectionAsync(
        string? overrideToken = null,
        string? overrideAccountId = null,
        string? overrideTunnelId = null,
        CancellationToken ct = default)
    {
        try
        {
            var token = overrideToken ?? _credentialService.GetCloudflareApiToken();
            var accountId = overrideAccountId ?? _settingsService.Settings.CloudflareAccountId;
            var tunnelId = overrideTunnelId ?? _settingsService.Settings.CloudflareTunnelId;

            if (string.IsNullOrWhiteSpace(token))
            {
                return (false, "Cloudflare API Token ontbreekt.", null);
            }
            if (string.IsNullOrWhiteSpace(accountId))
            {
                return (false, "Cloudflare Account ID ontbreekt.", null);
            }
            if (string.IsNullOrWhiteSpace(tunnelId))
            {
                return (false, "Cloudflare Tunnel ID ontbreekt.", null);
            }

            var url = $"{BaseApiUrl}/accounts/{accountId.Trim()}/cfd_tunnel/{tunnelId.Trim()}";
            using var req = CreateRequest(HttpMethod.Get, url, token);
            var response = await _httpClient.SendAsync(req, ct);
            var json = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                var errMsg = TryExtractErrorMessage(json) ?? $"{response.StatusCode} ({response.ReasonPhrase})";
                return (false, $"Cloudflare fout: {errMsg}", null);
            }

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("result", out var result))
            {
                var name = result.TryGetProperty("name", out var n) ? n.GetString() : "Onbekend";
                var status = result.TryGetProperty("status", out var s) ? s.GetString() : "onbekend";

                // Also try to automatically fetch and cache the tunnel run-token
                var fetchedToken = await FetchTunnelTokenAsync(token, accountId, tunnelId, ct);
                if (!string.IsNullOrWhiteSpace(fetchedToken))
                {
                    _settingsService.Settings.CloudflareTunnelToken = fetchedToken;
                    _settingsService.Save();
                }

                return (true, $"✅ Verbinding geslaagd! Tunnel '{name}' gevonden (Status: {status}).", name);
            }

            return (true, "✅ Verbinding geslaagd!", null);
        }
        catch (Exception ex)
        {
            return (false, $"Fout bij verbinden met Cloudflare: {ex.Message}", null);
        }
    }

    public async Task<string?> FetchTunnelTokenAsync(
        string? overrideToken = null,
        string? overrideAccountId = null,
        string? overrideTunnelId = null,
        CancellationToken ct = default)
    {
        try
        {
            var token = overrideToken ?? _credentialService.GetCloudflareApiToken();
            var accountId = overrideAccountId ?? _settingsService.Settings.CloudflareAccountId;
            var tunnelId = overrideTunnelId ?? _settingsService.Settings.CloudflareTunnelId;

            if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(accountId) || string.IsNullOrWhiteSpace(tunnelId))
            {
                return null;
            }

            var url = $"{BaseApiUrl}/accounts/{accountId.Trim()}/cfd_tunnel/{tunnelId.Trim()}/token";
            using var req = CreateRequest(HttpMethod.Get, url, token);
            var response = await _httpClient.SendAsync(req, ct);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.String)
            {
                return result.GetString();
            }
        }
        catch { }

        return null;
    }

    public async Task<string?> GetZoneIdAsync(string domain, string token, CancellationToken ct = default)
    {
        try
        {
            var cleanDomain = domain.Trim().ToLowerInvariant();
            var url = $"{BaseApiUrl}/zones?name={cleanDomain}";
            using var req = CreateRequest(HttpMethod.Get, url, token);
            var response = await _httpClient.SendAsync(req, ct);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in result.EnumerateArray())
                {
                    if (item.TryGetProperty("id", out var id))
                    {
                        return id.GetString();
                    }
                }
            }
        }
        catch { }

        return null;
    }

    public async Task<(bool Success, string Message, string? FullHostname)> RegisterSubdomainAsync(
        string subdomain,
        int hostPort,
        ProfileCloudflareConfig? profileConfig = null,
        CancellationToken ct = default)
    {
        var settings = _settingsService.Settings;
        var activeServer = settings.GetActiveServer();

        var token = !string.IsNullOrWhiteSpace(profileConfig?.ApiToken)
            ? profileConfig.ApiToken.Trim()
            : (!string.IsNullOrWhiteSpace(activeServer?.CloudflareApiToken) ? activeServer.CloudflareApiToken.Trim() : _credentialService.GetCloudflareApiToken());

        var accountId = !string.IsNullOrWhiteSpace(profileConfig?.AccountId)
            ? profileConfig.AccountId.Trim()
            : (!string.IsNullOrWhiteSpace(activeServer?.CloudflareAccountId) ? activeServer.CloudflareAccountId.Trim() : settings.CloudflareAccountId.Trim());

        var tunnelId = !string.IsNullOrWhiteSpace(profileConfig?.TunnelId)
            ? profileConfig.TunnelId.Trim()
            : (!string.IsNullOrWhiteSpace(activeServer?.CloudflareTunnelId) ? activeServer.CloudflareTunnelId.Trim() : settings.CloudflareTunnelId.Trim());

        var domain = !string.IsNullOrWhiteSpace(profileConfig?.Domain)
            ? profileConfig.Domain.Trim().TrimStart('.').ToLowerInvariant()
            : (!string.IsNullOrWhiteSpace(activeServer?.CloudflareDomain) ? activeServer.CloudflareDomain.Trim().TrimStart('.').ToLowerInvariant() : settings.CloudflareDomain.Trim().TrimStart('.').ToLowerInvariant());

        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(accountId) || string.IsNullOrWhiteSpace(tunnelId))
        {
            return (false, "Cloudflare API Token, Account ID of Tunnel ID ontbreekt in profiel- of app-instellingen.", null);
        }

        if (string.IsNullOrWhiteSpace(domain))
        {
            return (false, "Cloudflare domeinnaam ontbreekt in profiel- of app-instellingen (bijv. jouwdomein.nl).", null);
        }

        var cleanSub = subdomain.Trim().ToLowerInvariant();
        var fullHostname = cleanSub.EndsWith(domain, StringComparison.OrdinalIgnoreCase)
            ? cleanSub
            : $"{cleanSub}.{domain}";

        var serviceUrl = $"http://localhost:{hostPort}";

        try
        {
            // 1. Update Ingress configuration in Cloudflare Tunnel
            var configUrl = $"{BaseApiUrl}/accounts/{accountId}/cfd_tunnel/{tunnelId}/configurations";
            using var getReq = CreateRequest(HttpMethod.Get, configUrl, token);
            var getResp = await _httpClient.SendAsync(getReq, ct);
            var getConfigJson = await getResp.Content.ReadAsStringAsync(ct);

            if (!getResp.IsSuccessStatusCode)
            {
                var err = TryExtractErrorMessage(getConfigJson) ?? getResp.ReasonPhrase;
                return (false, $"Fout bij ophalen tunnelconfiguratie: {err}", null);
            }

            var rootNode = JsonNode.Parse(getConfigJson);
            var configNode = rootNode?["result"]?["config"];
            var ingressArray = configNode?["ingress"]?.AsArray();

            if (ingressArray == null)
            {
                ingressArray = new JsonArray();
                if (configNode is JsonObject cfgObj)
                {
                    cfgObj["ingress"] = ingressArray;
                }
            }

            // Find if hostname already exists
            JsonObject? existingRule = null;
            foreach (var node in ingressArray)
            {
                if (node is JsonObject obj &&
                    obj.TryGetPropertyValue("hostname", out var h) &&
                    string.Equals(h?.ToString(), fullHostname, StringComparison.OrdinalIgnoreCase))
                {
                    existingRule = obj;
                    break;
                }
            }

            if (existingRule != null)
            {
                existingRule["service"] = serviceUrl;
            }
            else
            {
                var newRule = new JsonObject
                {
                    ["hostname"] = fullHostname,
                    ["service"] = serviceUrl
                };

                // Insert before catch-all (which typically has no hostname or service: http_status:404)
                var lastIndex = ingressArray.Count - 1;
                if (lastIndex >= 0 && !ingressArray[lastIndex]!.AsObject().ContainsKey("hostname"))
                {
                    ingressArray.Insert(lastIndex, newRule);
                }
                else
                {
                    ingressArray.Add(newRule);
                }
            }

            // Ensure catch-all exists at end
            if (!ingressArray.Any(n => n is JsonObject obj && !obj.ContainsKey("hostname")))
            {
                ingressArray.Add(new JsonObject
                {
                    ["service"] = "http_status:404"
                });
            }

            var putPayload = new JsonObject
            {
                ["config"] = new JsonObject
                {
                    ["ingress"] = ingressArray.DeepClone()
                }
            };

            using var putReq = CreateRequest(HttpMethod.Put, configUrl, token);
            putReq.Content = new StringContent(putPayload.ToJsonString(), Encoding.UTF8, "application/json");
            var putResp = await _httpClient.SendAsync(putReq, ct);
            var putJson = await putResp.Content.ReadAsStringAsync(ct);

            if (!putResp.IsSuccessStatusCode)
            {
                var err = TryExtractErrorMessage(putJson) ?? putResp.ReasonPhrase;
                return (false, $"Fout bij bijwerken Ingress regel: {err}", null);
            }

            // 2. Ensure DNS CNAME record exists in Cloudflare Zone
            var zoneId = await GetZoneIdAsync(domain, token, ct);
            if (!string.IsNullOrWhiteSpace(zoneId))
            {
                var dnsQueryUrl = $"{BaseApiUrl}/zones/{zoneId}/dns_records?type=CNAME&name={fullHostname}";
                using var dnsGetReq = CreateRequest(HttpMethod.Get, dnsQueryUrl, token);
                var dnsGetResp = await _httpClient.SendAsync(dnsGetReq, ct);
                var dnsGetJson = await dnsGetResp.Content.ReadAsStringAsync(ct);

                bool recordExists = false;
                string? existingRecordId = null;
                string? existingContent = null;
                if (dnsGetResp.IsSuccessStatusCode)
                {
                    using var dnsDoc = JsonDocument.Parse(dnsGetJson);
                    if (dnsDoc.RootElement.TryGetProperty("result", out var arr) && arr.GetArrayLength() > 0)
                    {
                        var firstRecord = arr.EnumerateArray().FirstOrDefault();
                        recordExists = true;
                        if (firstRecord.TryGetProperty("id", out var idElem))
                        {
                            existingRecordId = idElem.GetString();
                        }
                        if (firstRecord.TryGetProperty("content", out var contentElem))
                        {
                            existingContent = contentElem.GetString();
                        }
                    }
                }

                var targetCname = $"{tunnelId}.cfargotunnel.com";
                if (!recordExists)
                {
                    var postDnsUrl = $"{BaseApiUrl}/zones/{zoneId}/dns_records";
                    var dnsPayload = new JsonObject
                    {
                        ["type"] = "CNAME",
                        ["name"] = fullHostname,
                        ["content"] = targetCname,
                        ["proxied"] = true,
                        ["comment"] = "Managed by DockerManager"
                    };

                    using var dnsPostReq = CreateRequest(HttpMethod.Post, postDnsUrl, token);
                    dnsPostReq.Content = new StringContent(dnsPayload.ToJsonString(), Encoding.UTF8, "application/json");
                    var dnsPostResp = await _httpClient.SendAsync(dnsPostReq, ct);
                    if (!dnsPostResp.IsSuccessStatusCode)
                    {
                        var postErr = await dnsPostResp.Content.ReadAsStringAsync(ct);
                        var err = TryExtractErrorMessage(postErr) ?? dnsPostResp.ReasonPhrase;
                        // Non-critical if user already created CNAME manually or wildcard
                    }
                }
                else if (!string.IsNullOrWhiteSpace(existingRecordId) && !string.Equals(existingContent, targetCname, StringComparison.OrdinalIgnoreCase))
                {
                    // Record exists but points to another tunnel: update it seamlessly to the new tunnel!
                    var putDnsUrl = $"{BaseApiUrl}/zones/{zoneId}/dns_records/{existingRecordId}";
                    var dnsPayload = new JsonObject
                    {
                        ["type"] = "CNAME",
                        ["name"] = fullHostname,
                        ["content"] = targetCname,
                        ["proxied"] = true,
                        ["comment"] = "Managed by DockerManager"
                    };

                    using var dnsPutReq = CreateRequest(HttpMethod.Put, putDnsUrl, token);
                    dnsPutReq.Content = new StringContent(dnsPayload.ToJsonString(), Encoding.UTF8, "application/json");
                    var dnsPutResp = await _httpClient.SendAsync(dnsPutReq, ct);
                    if (!dnsPutResp.IsSuccessStatusCode)
                    {
                        var putErr = await dnsPutResp.Content.ReadAsStringAsync(ct);
                        var err = TryExtractErrorMessage(putErr) ?? dnsPutResp.ReasonPhrase;
                    }
                }
            }

            return (true, $"🎉 Subdomein https://{fullHostname} is succesvol geregistreerd in Cloudflare!", fullHostname);
        }
        catch (Exception ex)
        {
            return (false, $"Fout bij configureren van Cloudflare Tunnel: {ex.Message}", null);
        }
    }

    public async Task<(bool Success, string Message)> UnregisterSubdomainAsync(
        string hostname,
        ProfileCloudflareConfig? profileConfig = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(hostname))
        {
            return (false, "Geen hostnaam opgegeven om te ontkoppelen.");
        }

        var settings = _settingsService.Settings;
        var activeServer = settings.GetActiveServer();

        var token = !string.IsNullOrWhiteSpace(profileConfig?.ApiToken)
            ? profileConfig.ApiToken.Trim()
            : (!string.IsNullOrWhiteSpace(activeServer?.CloudflareApiToken) ? activeServer.CloudflareApiToken.Trim() : _credentialService.GetCloudflareApiToken());

        var accountId = !string.IsNullOrWhiteSpace(profileConfig?.AccountId)
            ? profileConfig.AccountId.Trim()
            : (!string.IsNullOrWhiteSpace(activeServer?.CloudflareAccountId) ? activeServer.CloudflareAccountId.Trim() : settings.CloudflareAccountId.Trim());

        var tunnelId = !string.IsNullOrWhiteSpace(profileConfig?.TunnelId)
            ? profileConfig.TunnelId.Trim()
            : (!string.IsNullOrWhiteSpace(activeServer?.CloudflareTunnelId) ? activeServer.CloudflareTunnelId.Trim() : settings.CloudflareTunnelId.Trim());

        var domain = !string.IsNullOrWhiteSpace(profileConfig?.Domain)
            ? profileConfig.Domain.Trim().TrimStart('.').ToLowerInvariant()
            : (!string.IsNullOrWhiteSpace(activeServer?.CloudflareDomain) ? activeServer.CloudflareDomain.Trim().TrimStart('.').ToLowerInvariant() : settings.CloudflareDomain.Trim().TrimStart('.').ToLowerInvariant());

        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(accountId) || string.IsNullOrWhiteSpace(tunnelId))
        {
            return (false, "Cloudflare API Token, Account ID of Tunnel ID ontbreekt in profiel- of app-instellingen.");
        }

        var clean = hostname.Trim().ToLowerInvariant();
        if (clean.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) clean = clean[7..];
        if (clean.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) clean = clean[8..];
        clean = clean.Trim('/');

        var fullHostname = (!string.IsNullOrWhiteSpace(domain) && !clean.Contains('.'))
            ? $"{clean}.{domain}"
            : clean;

        bool ingressRemoved = false;
        bool dnsRemoved = false;
        var messages = new List<string>();

        // 1. Remove from Tunnel configuration (Ingress)
        try
        {
            var configUrl = $"{BaseApiUrl}/accounts/{accountId}/cfd_tunnel/{tunnelId}/configurations";
            using var getReq = CreateRequest(HttpMethod.Get, configUrl, token);
            var getResp = await _httpClient.SendAsync(getReq, ct);
            if (getResp.IsSuccessStatusCode)
            {
                var getConfigJson = await getResp.Content.ReadAsStringAsync(ct);
                var rootNode = JsonNode.Parse(getConfigJson);
                var configNode = rootNode?["result"]?["config"];
                var ingressArray = configNode?["ingress"]?.AsArray();

                if (ingressArray != null)
                {
                    var itemsToRemove = new List<JsonNode>();
                    foreach (var node in ingressArray)
                    {
                        if (node is JsonObject obj &&
                            obj.TryGetPropertyValue("hostname", out var h) &&
                            string.Equals(h?.ToString(), fullHostname, StringComparison.OrdinalIgnoreCase))
                        {
                            itemsToRemove.Add(node);
                        }
                    }

                    if (itemsToRemove.Count > 0)
                    {
                        foreach (var item in itemsToRemove)
                        {
                            ingressArray.Remove(item);
                        }

                        // Ensure catch-all exists at end
                        if (!ingressArray.Any(n => n is JsonObject obj && !obj.ContainsKey("hostname")))
                        {
                            ingressArray.Add(new JsonObject
                            {
                                ["service"] = "http_status:404"
                            });
                        }

                        var putPayload = new JsonObject
                        {
                            ["config"] = new JsonObject
                            {
                                ["ingress"] = ingressArray.DeepClone()
                            }
                        };

                        using var putReq = CreateRequest(HttpMethod.Put, configUrl, token);
                        putReq.Content = new StringContent(putPayload.ToJsonString(), Encoding.UTF8, "application/json");
                        var putResp = await _httpClient.SendAsync(putReq, ct);
                        if (putResp.IsSuccessStatusCode)
                        {
                            ingressRemoved = true;
                            messages.Add("tunnel routing");
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            messages.Add($"tunnel: {ex.Message}");
        }

        // 2. Delete DNS CNAME record from Zone
        try
        {
            var zoneId = await GetZoneIdAsync(domain, token, ct);
            if (!string.IsNullOrWhiteSpace(zoneId))
            {
                var dnsQueryUrl = $"{BaseApiUrl}/zones/{zoneId}/dns_records?type=CNAME&name={fullHostname}";
                using var dnsGetReq = CreateRequest(HttpMethod.Get, dnsQueryUrl, token);
                var dnsGetResp = await _httpClient.SendAsync(dnsGetReq, ct);
                if (dnsGetResp.IsSuccessStatusCode)
                {
                    var dnsGetJson = await dnsGetResp.Content.ReadAsStringAsync(ct);
                    using var dnsDoc = JsonDocument.Parse(dnsGetJson);
                    if (dnsDoc.RootElement.TryGetProperty("result", out var arr) && arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0)
                    {
                        foreach (var record in arr.EnumerateArray())
                        {
                            if (record.TryGetProperty("id", out var idElem))
                            {
                                var recordId = idElem.GetString();
                                if (!string.IsNullOrWhiteSpace(recordId))
                                {
                                    var deleteUrl = $"{BaseApiUrl}/zones/{zoneId}/dns_records/{recordId}";
                                    using var delReq = CreateRequest(HttpMethod.Delete, deleteUrl, token);
                                    var delResp = await _httpClient.SendAsync(delReq, ct);
                                    if (delResp.IsSuccessStatusCode)
                                    {
                                        dnsRemoved = true;
                                    }
                                }
                            }
                        }
                        if (dnsRemoved)
                        {
                            messages.Add("DNS CNAME");
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            messages.Add($"dns: {ex.Message}");
        }

        if (ingressRemoved || dnsRemoved)
        {
            return (true, $"🗑️ Cloudflare verwijzing '{fullHostname}' succesvol opgeruimd ({string.Join(", ", messages)}).");
        }

        return (true, $"Geen actieve Cloudflare regels gevonden voor '{fullHostname}' om te verwijderen.");
    }

    public bool EnsureCloudflaredServiceInProfile(ProfileModel profile, string? customTunnelToken = null)
    {
        if (profile == null) return false;

        var token = customTunnelToken;
        if (string.IsNullOrWhiteSpace(token))
        {
            token = profile.Cloudflare?.TunnelToken;
        }
        if (string.IsNullOrWhiteSpace(token))
        {
            var server = _settingsService.Settings.GetActiveServer();
            token = server?.CloudflareTunnelToken;
        }
        if (string.IsNullOrWhiteSpace(token))
        {
            token = _settingsService.Settings.CloudflareTunnelToken;
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        // Check if cloudflared is already in the profile
        var existing = profile.Services.FirstOrDefault(s =>
            s.Id.Equals("cloudflared", StringComparison.OrdinalIgnoreCase) ||
            s.Image.StartsWith("cloudflare/cloudflared", StringComparison.OrdinalIgnoreCase));

        if (existing != null)
        {
            // Update token command if needed
            existing.Command = new List<string> { "tunnel", "--no-autoupdate", "run", "--token", token.Trim() };
            return false;
        }

        var cloudflaredService = new ServiceDefinition
        {
            Id = "cloudflared",
            DisplayName = "Cloudflare Tunnel Agent",
            ContainerName = "cloudflared",
            Image = "cloudflare/cloudflared:latest",
            Description = "Cloudflare Zero Trust tunnel agent voor beveiligde publieke toegang en webhooks",
            RestartPolicy = "unless-stopped",
            NetworkMode = "host",
            Command = new List<string> { "tunnel", "--no-autoupdate", "run", "--token", token.Trim() }
        };

        profile.Services.Add(cloudflaredService);
        return true;
    }

    private static string? TryExtractErrorMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
            {
                var first = errors[0];
                if (first.TryGetProperty("message", out var msg))
                {
                    return msg.GetString();
                }
            }
        }
        catch { }

        return null;
    }
}
