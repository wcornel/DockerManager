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

    public ProfileCloudflareConfig Cloudflare { get; set; } = new();
    public ProfileRegistryConfig Registry { get; set; } = new();

    public List<ServiceDefinition> Services { get; set; } = new();

    public string GetEffectiveCloudflareDomain(AppSettings? globalSettings)
    {
        if (!string.IsNullOrWhiteSpace(Cloudflare?.Domain))
        {
            return Cloudflare.Domain.Trim().TrimStart('.').ToLowerInvariant();
        }
        return globalSettings?.CloudflareDomain?.Trim().TrimStart('.').ToLowerInvariant() ?? string.Empty;
    }

    public override string ToString() => Name;
}
