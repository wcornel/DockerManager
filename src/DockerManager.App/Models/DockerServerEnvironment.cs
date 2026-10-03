namespace DockerManager.App.Models;

public class DockerServerEnvironment
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "🖥️ Lokale PC";
    public string HostType { get; set; } = "Pipe"; // "Pipe" or "Tcp"
    public string PipeName { get; set; } = "npipe://./pipe/docker_engine";
    public string TcpUrl { get; set; } = "tcp://localhost:2375";
    public string ProfilesSubfolder { get; set; } = "local";
    public string LastActiveProfile { get; set; } = string.Empty;
    public string VolumesRootPath { get; set; } = string.Empty;

    // Cloudflare Tunnel (1 tunnel per server)
    public string CloudflareDomain { get; set; } = string.Empty;
    public string CloudflareAccountId { get; set; } = string.Empty;
    public string CloudflareTunnelId { get; set; } = string.Empty;
    public string CloudflareTunnelName { get; set; } = string.Empty;
    public string CloudflareTunnelToken { get; set; } = string.Empty;
    public string CloudflareApiToken { get; set; } = string.Empty;

    public string GetEffectiveVolumesRootPath()
    {
        if (!string.IsNullOrWhiteSpace(VolumesRootPath))
        {
            return VolumesRootPath.Trim();
        }

        if (HostType.Equals("Tcp", StringComparison.OrdinalIgnoreCase))
        {
            return "/var/lib/dockermanager/volumes";
        }

        var appData = System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData);
        return System.IO.Path.Combine(appData, "DockerManager", "volumes");
    }

    public string GetEffectiveUri()
    {
        if (HostType.Equals("Tcp", StringComparison.OrdinalIgnoreCase))
        {
            return TcpUrl;
        }
        return PipeName;
    }

    public override string ToString() => Name;

    public DockerServerEnvironment Clone()
    {
        return new DockerServerEnvironment
        {
            Id = Id,
            Name = Name,
            HostType = HostType,
            PipeName = PipeName,
            TcpUrl = TcpUrl,
            ProfilesSubfolder = ProfilesSubfolder,
            LastActiveProfile = LastActiveProfile,
            VolumesRootPath = VolumesRootPath,
            CloudflareDomain = CloudflareDomain,
            CloudflareAccountId = CloudflareAccountId,
            CloudflareTunnelId = CloudflareTunnelId,
            CloudflareTunnelName = CloudflareTunnelName,
            CloudflareTunnelToken = CloudflareTunnelToken,
            CloudflareApiToken = CloudflareApiToken
        };
    }
}
