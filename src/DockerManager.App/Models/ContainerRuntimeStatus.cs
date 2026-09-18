namespace DockerManager.App.Models;

public enum ContainerState
{
    Unknown,
    NotCreated,
    Created,
    Running,
    Restarting,
    Paused,
    Exited,
    Dead,
    Updating,
    Error
}

public class ContainerRuntimeInfo
{
    public string ContainerId { get; set; } = string.Empty;
    public string ContainerName { get; set; } = string.Empty;
    public string Image { get; set; } = string.Empty;
    public ContainerState State { get; set; } = ContainerState.Unknown;
    public string StatusText { get; set; } = "Onbekend";
    public DateTime? StartedAt { get; set; }
    public string? HealthStatus { get; set; }
    public string? ErrorMessage { get; set; }
}
