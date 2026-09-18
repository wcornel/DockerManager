namespace DockerManager.App.Models;

public class ProfileModel
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool AutoStopPreviousOnSwitch { get; set; }
    public List<ServiceDefinition> Services { get; set; } = new();

    public override string ToString() => Name;
}
