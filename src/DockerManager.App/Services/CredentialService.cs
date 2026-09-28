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

    List<RegistryCredential> GetAllRegistryCredentials();
    void SaveRegistryCredential(RegistryCredential credential);
    void DeleteRegistryCredential(string serverAddress);
    RegistryCredential? GetCredentialForImage(string imageOrServerAddress);
}

public class CredentialService : ICredentialService
{
    private readonly string _tokenFilePath;
    private readonly string _cloudflareTokenFilePath;
    private readonly string _registriesFilePath;
    private static readonly byte[] Entropy = "DockerManager_Entropy_Salt_9988"u8.ToArray();

    public CredentialService(string? customBasePath = null)
    {
        var dir = !string.IsNullOrWhiteSpace(customBasePath)
            ? customBasePath
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DockerManager");
        Directory.CreateDirectory(dir);
        _tokenFilePath = Path.Combine(dir, "gh.dat");
        _cloudflareTokenFilePath = Path.Combine(dir, "cf.dat");
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
        catch { }
    }

    public void SaveGitHubToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            ClearGitHubToken();
            DeleteRegistryCredential("ghcr.io");
            return;
        }

        var plainBytes = Encoding.UTF8.GetBytes(token.Trim());
        var encryptedBytes = ProtectedData.Protect(plainBytes, Entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(_tokenFilePath, encryptedBytes);

        SaveRegistryCredential(new RegistryCredential
        {
            ServerAddress = "ghcr.io",
            Username = "GitHub User",
            Password = token.Trim(),
            DisplayName = "GitHub Container Registry (ghcr.io)"
        });
    }

    public string? GetGitHubToken()
    {
        var cred = GetCredentialForImage("ghcr.io");
        if (cred != null && !string.IsNullOrWhiteSpace(cred.Password))
        {
            return cred.Password;
        }

        if (!File.Exists(_tokenFilePath))
            return null;

        try
        {
            var encryptedBytes = File.ReadAllBytes(_tokenFilePath);
            var plainBytes = ProtectedData.Unprotect(encryptedBytes, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch
        {
            return null;
        }
    }

    public void ClearGitHubToken()
    {
        if (File.Exists(_tokenFilePath))
        {
            try { File.Delete(_tokenFilePath); } catch { }
        }
        DeleteRegistryCredential("ghcr.io");
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

        var plainBytes = Encoding.UTF8.GetBytes(token.Trim());
        var encryptedBytes = ProtectedData.Protect(plainBytes, Entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(_cloudflareTokenFilePath, encryptedBytes);
    }

    public string? GetCloudflareApiToken()
    {
        if (!File.Exists(_cloudflareTokenFilePath))
            return null;

        try
        {
            var encryptedBytes = File.ReadAllBytes(_cloudflareTokenFilePath);
            var plainBytes = ProtectedData.Unprotect(encryptedBytes, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch
        {
            return null;
        }
    }

    public void ClearCloudflareApiToken()
    {
        if (File.Exists(_cloudflareTokenFilePath))
        {
            try { File.Delete(_cloudflareTokenFilePath); } catch { }
        }
    }

    public List<RegistryCredential> GetAllRegistryCredentials()
    {
        if (!File.Exists(_registriesFilePath))
            return new List<RegistryCredential>();

        try
        {
            var encryptedBytes = File.ReadAllBytes(_registriesFilePath);
            var plainBytes = ProtectedData.Unprotect(encryptedBytes, Entropy, DataProtectionScope.CurrentUser);
            var json = Encoding.UTF8.GetString(plainBytes);
            return JsonSerializer.Deserialize<List<RegistryCredential>>(json) ?? new List<RegistryCredential>();
        }
        catch
        {
            return new List<RegistryCredential>();
        }
    }

    public void SaveRegistryCredential(RegistryCredential credential)
    {
        if (string.IsNullOrWhiteSpace(credential.ServerAddress))
            return;

        var list = GetAllRegistryCredentials();
        var cleanServer = NormalizeServerAddress(credential.ServerAddress);
        credential.ServerAddress = cleanServer;

        list.RemoveAll(x => NormalizeServerAddress(x.ServerAddress).Equals(cleanServer, StringComparison.OrdinalIgnoreCase));
        list.Add(credential);

        SaveAllRegistries(list);
    }

    public void DeleteRegistryCredential(string serverAddress)
    {
        var list = GetAllRegistryCredentials();
        var cleanServer = NormalizeServerAddress(serverAddress);
        list.RemoveAll(x => NormalizeServerAddress(x.ServerAddress).Equals(cleanServer, StringComparison.OrdinalIgnoreCase));
        SaveAllRegistries(list);
    }

    public RegistryCredential? GetCredentialForImage(string imageOrServerAddress)
    {
        if (string.IsNullOrWhiteSpace(imageOrServerAddress)) return null;

        var list = GetAllRegistryCredentials();
        var targetServer = ExtractServerAddress(imageOrServerAddress);

        return list.FirstOrDefault(x => 
            NormalizeServerAddress(x.ServerAddress).Equals(targetServer, StringComparison.OrdinalIgnoreCase) ||
            imageOrServerAddress.StartsWith(x.ServerAddress, StringComparison.OrdinalIgnoreCase));
    }

    private void SaveAllRegistries(List<RegistryCredential> list)
    {
        try
        {
            var json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
            var plainBytes = Encoding.UTF8.GetBytes(json);
            var encryptedBytes = ProtectedData.Protect(plainBytes, Entropy, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(_registriesFilePath, encryptedBytes);
        }
        catch { }
    }

    private static string NormalizeServerAddress(string address)
    {
        var clean = address.Trim().ToLowerInvariant();
        if (clean.StartsWith("https://")) clean = clean[8..];
        if (clean.StartsWith("http://")) clean = clean[7..];
        if (clean.EndsWith("/")) clean = clean[..^1];
        return clean;
    }

    private static string ExtractServerAddress(string imageOrServer)
    {
        var clean = NormalizeServerAddress(imageOrServer);
        if (clean.Contains('/'))
        {
            var firstPart = clean[..clean.IndexOf('/')];
            if (firstPart.Contains('.') || firstPart.Contains(':'))
            {
                return firstPart;
            }
            return "docker.io";
        }
        return clean;
    }
}
