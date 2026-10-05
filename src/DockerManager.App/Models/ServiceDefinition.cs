namespace DockerManager.App.Models;

public class ServiceDefinition
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string ContainerName { get; set; } = string.Empty;
    public string Image { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool AutoStart { get; set; }
    public bool RequiresAuth { get; set; } = true;
    public string RestartPolicy { get; set; } = "unless-stopped"; // "no", "always", "unless-stopped", "on-failure"
    public string? NetworkMode { get; set; }
    public List<PortMapping> Ports { get; set; } = new();
    public List<VolumeMapping> Volumes { get; set; } = new();
    public Dictionary<string, string> Environment { get; set; } = new();
    public Dictionary<string, string> Labels { get; set; } = new();
    public List<string>? Command { get; set; }
    public string? WorkingDir { get; set; }
    public string? CloudflareHostname { get; set; }
    public DateTime? LastUpdatedUtc { get; set; }

    public string GetEffectiveContainerName()
    {
        var raw = !string.IsNullOrWhiteSpace(ContainerName) ? ContainerName : Id;
        return string.IsNullOrWhiteSpace(raw) ? "container" : raw.Trim().Replace(" ", "-");
    }
}
