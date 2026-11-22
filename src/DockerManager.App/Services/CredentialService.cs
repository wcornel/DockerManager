using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DockerManager.App.Services;

public class RegistryCredential
{
    public string ServerAddress { get; set; } = string.Empty; // e.g. "ghcr.io", "docker.io", "myacr.azurecr.io", "registry.gitlab.com"
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsGitHubRegistry => ServerAddress.Equals("ghcr.io", StringComparison.OrdinalIgnoreCase);

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Password);
}

public interface ICredentialService
{
    void SaveGitHubToken(string token);
    string? GetGitHubToken();
    void ClearGitHubToken();
    bool HasToken();

    void SaveCloudflareApiToken(string token);
    string? GetCloudflareApiToken();
    void ClearCloudflareApiToken();

    void SaveCloudflareTunnelToken(string token);
    string? GetCloudflareTunnelToken();
    void ClearCloudflareTunnelToken();
    void SaveServerCloudflareApiToken(string serverId, string token);
    string? GetServerCloudflareApiToken(string serverId);
    void SaveServerCloudflareTunnelToken(string serverId, string token);
    string? GetServerCloudflareTunnelToken(string serverId);

    List<RegistryCredential> GetAllRegistryCredentials();
    void SaveRegistryCredential(RegistryCredential credential);
    void DeleteRegistryCredential(string serverAddress);
    RegistryCredential? GetCredentialForImage(string imageOrServerAddress);
}

public class CredentialService : ICredentialService
{
    private readonly string _tokenFilePath;
    private readonly string _cloudflareTokenFilePath;
    private readonly string _cloudflareSecretsFilePath;
    private readonly string _registriesFilePath;
    private readonly object _syncLock = new();
    private static readonly byte[] Entropy = "DockerManager_Entropy_Salt_9988"u8.ToArray();
    private static readonly JsonSerializerOptions WriteJsonOptions = new() { WriteIndented = true };

    public CredentialService(string? customBasePath = null)
    {
        var dir = !string.IsNullOrWhiteSpace(customBasePath)
            ? customBasePath
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DockerManager");
        Directory.CreateDirectory(dir);
        _tokenFilePath = Path.Combine(dir, "gh.dat");
        _cloudflareTokenFilePath = Path.Combine(dir, "cf.dat");
        _cloudflareSecretsFilePath = Path.Combine(dir, "cf_secrets.dat");
        _registriesFilePath = Path.Combine(dir, "registries.dat");

        MigrateLegacyGitHubTokenIfNeeded();
    }

    private void MigrateLegacyGitHubTokenIfNeeded()
    {
        try
        {
            if (!File.Exists(_registriesFilePath) && File.Exists(_tokenFilePath))
            {
                var ghToken = GetGitHubToken();
                if (!string.IsNullOrWhiteSpace(ghToken))
                {
                    SaveRegistryCredential(new RegistryCredential
                    {
                        ServerAddress = "ghcr.io",
                        Username = "GitHub User",
                        Password = ghToken,
                        DisplayName = "GitHub Container Registry (ghcr.io)"
                    });
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogWarning(nameof(CredentialService), "Legacy GitHub token migration failed", ex);
        }
    }

    public void SaveGitHubToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            ClearGitHubToken();
            return;
        }

        var trimmed = token.Trim();
        lock (_syncLock)
        {
            WriteEncryptedStringAtomic(_tokenFilePath, trimmed);

            if (!TryLoadRegistriesLocked(out var list))
            {
                AppLogger.LogWarning(nameof(CredentialService), "Failed to read existing registry credentials; aborting ghcr.io update to prevent data loss.");
                return;
            }

            var existing = list.FirstOrDefault(x =>
                NormalizeServerAddress(x.ServerAddress).Equals("ghcr.io", StringComparison.OrdinalIgnoreCase));
            var username = existing != null &&
                           !string.IsNullOrWhiteSpace(existing.Username) &&
                           !existing.Username.Equals("GitHub User", StringComparison.OrdinalIgnoreCase)
                ? existing.Username
                : "GitHub User";

            list.RemoveAll(x => NormalizeServerAddress(x.ServerAddress).Equals("ghcr.io", StringComparison.OrdinalIgnoreCase));
            list.Add(new RegistryCredential
            {
                ServerAddress = "ghcr.io",
                Username = username,
                Password = trimmed,
                DisplayName = "GitHub Container Registry (ghcr.io)"
            });

            SaveAllRegistriesLocked(list);
        }
    }

    public string? GetGitHubToken()
    {
        lock (_syncLock)
        {
            if (File.Exists(_tokenFilePath))
            {
                var token = ReadEncryptedString(_tokenFilePath, out _);
                if (!string.IsNullOrWhiteSpace(token))
                {
                    return token;
                }
            }

            if (TryLoadRegistriesLocked(out var list))
            {
                var cred = list.FirstOrDefault(x =>
                    NormalizeServerAddress(x.ServerAddress).Equals("ghcr.io", StringComparison.OrdinalIgnoreCase));
                if (cred != null && !string.IsNullOrWhiteSpace(cred.Password))
                {
                    return cred.Password;
                }
            }

            return null;
        }
    }

    public void ClearGitHubToken()
    {
        lock (_syncLock)
        {
            DeleteFileSafe(_tokenFilePath);
            if (TryLoadRegistriesLocked(out var list))
            {
                list.RemoveAll(x => NormalizeServerAddress(x.ServerAddress).Equals("ghcr.io", StringComparison.OrdinalIgnoreCase));
                SaveAllRegistriesLocked(list);
            }
            else
            {
                AppLogger.LogWarning(nameof(CredentialService), "Failed to read existing registry credentials while clearing GitHub token.");
            }
        }
    }

    public bool HasToken()
    {
        return !string.IsNullOrEmpty(GetGitHubToken());
    }

    public void SaveCloudflareApiToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            ClearCloudflareApiToken();
            return;
        }

        lock (_syncLock)
        {
            WriteEncryptedStringAtomic(_cloudflareTokenFilePath, token.Trim());
        }
    }

    public string? GetCloudflareApiToken()
    {
        lock (_syncLock)
        {
            return ReadEncryptedString(_cloudflareTokenFilePath, out _);
        }
    }

    public void ClearCloudflareApiToken()
    {
        lock (_syncLock)
        {
            DeleteFileSafe(_cloudflareTokenFilePath);
        }
    }

    public void SaveCloudflareTunnelToken(string token) => SetCloudflareSecret("global:tunnel", token);

    public string? GetCloudflareTunnelToken() => GetCloudflareSecret("global:tunnel");

    public void ClearCloudflareTunnelToken() => SetCloudflareSecret("global:tunnel", string.Empty);

    public void SaveServerCloudflareApiToken(string serverId, string token)
    {
        if (string.IsNullOrWhiteSpace(serverId)) return;
        SetCloudflareSecret($"server:{serverId.Trim()}:api", token);
    }

    public string? GetServerCloudflareApiToken(string serverId)
    {
        if (string.IsNullOrWhiteSpace(serverId)) return null;
        return GetCloudflareSecret($"server:{serverId.Trim()}:api");
    }

    public void SaveServerCloudflareTunnelToken(string serverId, string token)
    {
        if (string.IsNullOrWhiteSpace(serverId)) return;
        SetCloudflareSecret($"server:{serverId.Trim()}:tunnel", token);
    }

    public string? GetServerCloudflareTunnelToken(string serverId)
    {
        if (string.IsNullOrWhiteSpace(serverId)) return null;
        return GetCloudflareSecret($"server:{serverId.Trim()}:tunnel");
    }

    private string? GetCloudflareSecret(string key)
    {
        lock (_syncLock)
        {
            if (!TryLoadCloudflareSecretsLocked(out var dict))
            {
                return null;
            }

            return dict.TryGetValue(key, out var val) && !string.IsNullOrWhiteSpace(val) ? val : null;
        }
    }

    private void SetCloudflareSecret(string key, string value)
    {
        lock (_syncLock)
        {
            if (!TryLoadCloudflareSecretsLocked(out var dict))
            {
                AppLogger.LogWarning(nameof(CredentialService), "Failed to read existing Cloudflare secrets; aborting write to prevent data loss.");
                return;
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                if (!dict.Remove(key))
                {
                    return;
                }
            }
            else
            {
                dict[key] = value.Trim();
            }

            try
            {
                var json = JsonSerializer.Serialize(dict, WriteJsonOptions);
                WriteEncryptedStringAtomic(_cloudflareSecretsFilePath, json);
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning(nameof(CredentialService), "Failed to save Cloudflare secrets", ex);
            }
        }
    }

    private bool TryLoadCloudflareSecretsLocked(out Dictionary<string, string> secrets)
    {
        secrets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(_cloudflareSecretsFilePath))
        {
            return true;
        }

        var json = ReadEncryptedString(_cloudflareSecretsFilePath, out bool readSucceeded);
        if (!readSucceeded)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            return true;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (parsed != null)
            {
                secrets = new Dictionary<string, string>(parsed, StringComparer.OrdinalIgnoreCase);
            }
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.LogWarning(nameof(CredentialService), "Failed to deserialize Cloudflare secrets", ex);
            return false;
        }
    }

    public List<RegistryCredential> GetAllRegistryCredentials()
    {
        lock (_syncLock)
        {
            TryLoadRegistriesLocked(out var list);
            return list;
        }
    }

    public void SaveRegistryCredential(RegistryCredential credential)
    {
        if (credential == null || string.IsNullOrWhiteSpace(credential.ServerAddress))
            return;

        lock (_syncLock)
        {
            if (!TryLoadRegistriesLocked(out var list))
            {
                AppLogger.LogWarning(nameof(CredentialService), "Failed to read existing registry credentials; aborting save to prevent data loss.");
                return;
            }

            var cleanServer = NormalizeServerAddress(credential.ServerAddress);
            var copy = new RegistryCredential
            {
                ServerAddress = cleanServer,
                Username = credential.Username,
                Password = credential.Password,
                DisplayName = credential.DisplayName
            };

            list.RemoveAll(x => NormalizeServerAddress(x.ServerAddress).Equals(cleanServer, StringComparison.OrdinalIgnoreCase));
            list.Add(copy);

            SaveAllRegistriesLocked(list);
        }
    }

    public void DeleteRegistryCredential(string serverAddress)
    {
        if (string.IsNullOrWhiteSpace(serverAddress))
            return;

        lock (_syncLock)
        {
            if (!TryLoadRegistriesLocked(out var list))
            {
                AppLogger.LogWarning(nameof(CredentialService), "Failed to read existing registry credentials; aborting delete to prevent data loss.");
                return;
            }

            var cleanServer = NormalizeServerAddress(serverAddress);
            list.RemoveAll(x => NormalizeServerAddress(x.ServerAddress).Equals(cleanServer, StringComparison.OrdinalIgnoreCase));
            SaveAllRegistriesLocked(list);
        }
    }

    public RegistryCredential? GetCredentialForImage(string imageOrServerAddress)
    {
        if (string.IsNullOrWhiteSpace(imageOrServerAddress)) return null;

        lock (_syncLock)
        {
            TryLoadRegistriesLocked(out var list);
            var targetServer = ExtractServerAddress(imageOrServerAddress);

            return list.FirstOrDefault(x =>
                NormalizeServerAddress(x.ServerAddress).Equals(targetServer, StringComparison.OrdinalIgnoreCase));
        }
    }

    private bool TryLoadRegistriesLocked(out List<RegistryCredential> list)
    {
        list = new List<RegistryCredential>();
        if (!File.Exists(_registriesFilePath))
        {
            return true;
        }

        var json = ReadEncryptedString(_registriesFilePath, out bool readSucceeded);
        if (!readSucceeded)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            return true;
        }

        try
        {
            list = JsonSerializer.Deserialize<List<RegistryCredential>>(json) ?? new List<RegistryCredential>();
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.LogWarning(nameof(CredentialService), "Failed to deserialize registry credentials", ex);
            return false;
        }
    }

    private void SaveAllRegistriesLocked(List<RegistryCredential> list)
    {
        try
        {
            var json = JsonSerializer.Serialize(list, WriteJsonOptions);
            WriteEncryptedStringAtomic(_registriesFilePath, json);
        }
        catch (Exception ex)
        {
            AppLogger.LogWarning(nameof(CredentialService), "Failed to save registry credentials", ex);
        }
    }

    private static void WriteEncryptedStringAtomic(string filePath, string plainText)
    {
        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        byte[] encryptedBytes;
        try
        {
            encryptedBytes = ProtectedData.Protect(plainBytes, Entropy, DataProtectionScope.CurrentUser);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plainBytes);
        }

        var tmpPath = filePath + ".tmp";
        File.WriteAllBytes(tmpPath, encryptedBytes);
        File.Move(tmpPath, filePath, overwrite: true);
    }

    private static string? ReadEncryptedString(string filePath, out bool readSucceeded)
    {
        if (!File.Exists(filePath))
        {
            readSucceeded = true;
            return null;
        }

        byte[]? plainBytes = null;
        try
        {
            var encryptedBytes = File.ReadAllBytes(filePath);
            plainBytes = ProtectedData.Unprotect(encryptedBytes, Entropy, DataProtectionScope.CurrentUser);
            readSucceeded = true;
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch (Exception ex)
        {
            AppLogger.LogWarning(nameof(CredentialService), $"Failed to decrypt credential store '{Path.GetFileName(filePath)}'", ex);
            readSucceeded = false;
            return null;
        }
        finally
        {
            if (plainBytes != null)
            {
                CryptographicOperations.ZeroMemory(plainBytes);
            }
        }
    }

    private static void DeleteFileSafe(string filePath)
    {
        if (!File.Exists(filePath))
            return;

        try
        {
            File.Delete(filePath);
        }
        catch (Exception ex)
        {
            AppLogger.LogWarning(nameof(CredentialService), $"Failed to delete credential file '{Path.GetFileName(filePath)}'", ex);
        }
    }

    internal static string NormalizeServerAddress(string address)
    {
        var clean = address.Trim().ToLowerInvariant();
        if (clean.StartsWith("https://", StringComparison.Ordinal)) clean = clean[8..];
        if (clean.StartsWith("http://", StringComparison.Ordinal)) clean = clean[7..];
        clean = clean.TrimEnd('/');
        return clean;
    }

    internal static string ExtractServerAddress(string imageOrServer)
    {
        var clean = NormalizeServerAddress(imageOrServer);
        if (clean.Contains('/'))
        {
            var firstPart = clean[..clean.IndexOf('/')];
            if (firstPart.Contains('.') || firstPart.Contains(':') || firstPart.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            {
                return firstPart;
            }
            return "docker.io";
        }
        if (clean.Contains('.') || clean.Contains(':') || clean.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return clean;
        }
        return "docker.io";
    }
}
