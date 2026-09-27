namespace DockerManager.App.Models;

public class AppSettings
{
    public string DockerHostType { get; set; } = "Pipe"; // "Pipe" or "Tcp"
    public string DockerPipeName { get; set; } = "npipe://./pipe/docker_engine";
    public string DockerTcpUrl { get; set; } = "tcp://192.168.1.50:2375";

    public string GitHubRepoOwner { get; set; } = string.Empty;
    public string GitHubRepoName { get; set; } = string.Empty;
    public string GitHubDeployRepo { get; set; } = string.Empty;
    public string GitHubBranch { get; set; } = "main";
    public string GitHubProfilesFolder { get; set; } = "profiles";
    public string GitHubOAuthClientId { get; set; } = "Iv23li7mUq8bU5X0aKj1";
    public string LoggedInUsername { get; set; } = string.Empty;
    public bool HasGitHubToken { get; set; }

    public string CloudflareAccountId { get; set; } = string.Empty;
    public string CloudflareTunnelId { get; set; } = string.Empty;
    public string CloudflareDomain { get; set; } = string.Empty;
    public string CloudflareTunnelToken { get; set; } = string.Empty;
    public bool HasCloudflareToken { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasCloudflareConfigured =>
        !string.IsNullOrWhiteSpace(CloudflareAccountId) &&
        !string.IsNullOrWhiteSpace(CloudflareTunnelId) &&
        !string.IsNullOrWhiteSpace(CloudflareDomain);

    public string LastActiveProfile { get; set; } = "Example Stack";
    public string Language { get; set; } = "auto"; // "auto", "nl", "en"
    public bool AutoCheckUpdatesOnStart { get; set; } = true;
    public bool PromptToStopPreviousProfile { get; set; } = true;
    public bool DefaultKeepRunningOnSwitch { get; set; } = true;

    public bool AutoCheckImageUpdates { get; set; } = true;
    public string UpdateCheckFrequency { get; set; } = "Dagelijks"; // "Dagelijks", "Wekelijks", "Handmatig"
    public DateTime? LastUpdateCheckUtc { get; set; }

    public string LocalProfilesFolder { get; set; } = string.Empty;

    public double WindowWidth { get; set; } = 1200;
    public double WindowHeight { get; set; } = 830;
    public double? WindowTop { get; set; }
    public double? WindowLeft { get; set; }
    public string WindowState { get; set; } = "Normal";
    public List<DockerServerEnvironment> Servers { get; set; } = new();
    public string ActiveServerId { get; set; } = string.Empty;

    public DockerServerEnvironment GetActiveServer()
    {
        EnsureDefaultServers();
        var found = Servers.FirstOrDefault(s => s.Id.Equals(ActiveServerId, StringComparison.OrdinalIgnoreCase));
        if (found != null) return found;
        return Servers.First();
    }

    public void EnsureDefaultServers()
    {
        if (Servers == null)
        {
            Servers = new List<DockerServerEnvironment>();
        }

        if (Servers.Count == 0)
        {
            // Backward compatibility: migrate existing settings if present
            var isExistingTcp = DockerHostType.Equals("Tcp", StringComparison.OrdinalIgnoreCase);

            var localServer = new DockerServerEnvironment
            {
                Id = "local",
                Name = "🖥️ Lokale PC",
                HostType = "Pipe",
                PipeName = !string.IsNullOrWhiteSpace(DockerPipeName) ? DockerPipeName : "npipe://./pipe/docker_engine",
                ProfilesSubfolder = "local"
            };

            var vpsServer = new DockerServerEnvironment
            {
                Id = "vps",
                Name = "🌐 Externe VPS",
                HostType = "Tcp",
                TcpUrl = !string.IsNullOrWhiteSpace(DockerTcpUrl) ? DockerTcpUrl : "tcp://100.x.y.z:2375",
                ProfilesSubfolder = "vps"
            };

            Servers.Add(localServer);
            Servers.Add(vpsServer);

            ActiveServerId = isExistingTcp ? "vps" : "local";
        }
        else if (string.IsNullOrWhiteSpace(ActiveServerId) || !Servers.Any(s => s.Id.Equals(ActiveServerId, StringComparison.OrdinalIgnoreCase)))
        {
            ActiveServerId = Servers[0].Id;
        }
    }

    public string GetEffectiveDockerUri()
    {
        return GetActiveServer().GetEffectiveUri();
    }
}
