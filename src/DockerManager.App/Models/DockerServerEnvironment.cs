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
            LastActiveProfile = LastActiveProfile
        };
    }
}
