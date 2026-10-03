using CommunityToolkit.Mvvm.ComponentModel;
using System.Text.Json.Serialization;

namespace DockerManager.App.Models;

public class ProfileCloudflareConfig
{
    public string AccountId { get; set; } = string.Empty;
    public string TunnelId { get; set; } = string.Empty;
    public string Domain { get; set; } = string.Empty;
    public string ApiToken { get; set; } = string.Empty;
    public string TunnelToken { get; set; } = string.Empty;

    [JsonIgnore]
    public bool HasConfiguration =>
        !string.IsNullOrWhiteSpace(AccountId) ||
        !string.IsNullOrWhiteSpace(TunnelId) ||
        !string.IsNullOrWhiteSpace(Domain) ||
        !string.IsNullOrWhiteSpace(ApiToken);

    public ProfileCloudflareConfig Clone() => new()
    {
        AccountId = AccountId,
        TunnelId = TunnelId,
        Domain = Domain,
        ApiToken = ApiToken,
        TunnelToken = TunnelToken
    };
}

public class ProfileRegistryConfig
{
    public string Server { get; set; } = string.Empty;
    public string Namespace { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;

    [JsonIgnore]
    public bool HasConfiguration =>
        !string.IsNullOrWhiteSpace(Server) ||
        !string.IsNullOrWhiteSpace(Namespace);

    public ProfileRegistryConfig Clone() => new()
    {
        Server = Server,
        Namespace = Namespace,
        Username = Username,
        Password = Password
    };
}

public class ProfileModel : ObservableObject
{
    private string _name = string.Empty;
    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    private string _description = string.Empty;
    public string Description
    {
        get => _description;
        set => SetProperty(ref _description, value);
    }

    private bool _autoStopPreviousOnSwitch;
    public bool AutoStopPreviousOnSwitch
    {
        get => _autoStopPreviousOnSwitch;
        set => SetProperty(ref _autoStopPreviousOnSwitch, value);
    }

    private string _volumesSubfolder = string.Empty;
    public string VolumesSubfolder
    {
        get => _volumesSubfolder;
        set => SetProperty(ref _volumesSubfolder, value);
    }

    public string GetEffectiveVolumesSubfolder()
    {
        if (!string.IsNullOrWhiteSpace(VolumesSubfolder))
        {
            var clean = VolumesSubfolder.Trim().TrimStart('/', '\\').Replace('\\', '/');
            if (!string.IsNullOrWhiteSpace(clean)) return clean;
        }

        var safe = string.Join("", (Name ?? "default").Where(char.IsLetterOrDigit)).ToLowerInvariant();
        return string.IsNullOrWhiteSpace(safe) ? "default" : safe;
    }

    public ProfileCloudflareConfig Cloudflare { get; set; } = new();
    public ProfileRegistryConfig Registry { get; set; } = new();

    public List<ServiceDefinition> Services { get; set; } = new();

    public string GetEffectiveCloudflareDomain(DockerServerEnvironment? server) => GetEffectiveCloudflareDomain(null, server);

    public string GetEffectiveCloudflareDomain(AppSettings? globalSettings, DockerServerEnvironment? server = null)
    {
        if (!string.IsNullOrWhiteSpace(Cloudflare?.Domain))
        {
            return Cloudflare.Domain.Trim().TrimStart('.').ToLowerInvariant();
        }
        if (!string.IsNullOrWhiteSpace(server?.CloudflareDomain))
        {
            return server.CloudflareDomain.Trim().TrimStart('.').ToLowerInvariant();
        }
        return globalSettings?.CloudflareDomain?.Trim().TrimStart('.').ToLowerInvariant() ?? string.Empty;
    }

    public ProfileCloudflareConfig GetEffectiveCloudflareConfig(DockerServerEnvironment? server) => GetEffectiveCloudflareConfig(null, server);

    public ProfileCloudflareConfig GetEffectiveCloudflareConfig(AppSettings? globalSettings, DockerServerEnvironment? server = null)
    {
        var cfg = new ProfileCloudflareConfig();

        cfg.Domain = !string.IsNullOrWhiteSpace(Cloudflare?.Domain)
            ? Cloudflare.Domain
            : (!string.IsNullOrWhiteSpace(server?.CloudflareDomain) ? server.CloudflareDomain : (globalSettings?.CloudflareDomain ?? string.Empty));

        cfg.AccountId = !string.IsNullOrWhiteSpace(Cloudflare?.AccountId)
            ? Cloudflare.AccountId
            : (!string.IsNullOrWhiteSpace(server?.CloudflareAccountId) ? server.CloudflareAccountId : (globalSettings?.CloudflareAccountId ?? string.Empty));

        cfg.TunnelId = !string.IsNullOrWhiteSpace(Cloudflare?.TunnelId)
            ? Cloudflare.TunnelId
            : (!string.IsNullOrWhiteSpace(server?.CloudflareTunnelId) ? server.CloudflareTunnelId : (globalSettings?.CloudflareTunnelId ?? string.Empty));

        cfg.TunnelToken = !string.IsNullOrWhiteSpace(Cloudflare?.TunnelToken)
            ? Cloudflare.TunnelToken
            : (!string.IsNullOrWhiteSpace(server?.CloudflareTunnelToken) ? server.CloudflareTunnelToken : (globalSettings?.CloudflareTunnelToken ?? string.Empty));

        cfg.ApiToken = !string.IsNullOrWhiteSpace(Cloudflare?.ApiToken)
            ? Cloudflare.ApiToken
            : (!string.IsNullOrWhiteSpace(server?.CloudflareApiToken) ? server.CloudflareApiToken : string.Empty);

        return cfg;
    }

    public override string ToString() => Name;
}
