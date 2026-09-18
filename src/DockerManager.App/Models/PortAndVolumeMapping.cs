namespace DockerManager.App.Models;

public class PortMapping
{
    public string HostIp { get; set; } = "0.0.0.0";
    public int HostPort { get; set; }
    public int ContainerPort { get; set; }
    public string Protocol { get; set; } = "tcp";

    public override string ToString() => string.IsNullOrWhiteSpace(HostIp) || HostIp == "0.0.0.0"
        ? $"{HostPort}:{ContainerPort}/{Protocol}"
        : $"{HostIp}:{HostPort}:{ContainerPort}/{Protocol}";
}

public class VolumeMapping
{
    public string HostPath { get; set; } = string.Empty;
    public string ContainerPath { get; set; } = string.Empty;
    public bool IsNamedVolume { get; set; }
    public bool ReadOnly { get; set; }

    public override string ToString() => $"{HostPath}:{ContainerPath}" + (ReadOnly ? ":ro" : "");
}
