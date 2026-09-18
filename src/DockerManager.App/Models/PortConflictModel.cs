namespace DockerManager.App.Models;

public enum PortConflictSeverity
{
    Ok,         // Green: Free or properly isolated
    Warning,    // Yellow/Orange: Configured in multiple profiles (could clash if both run)
    Conflict    // Red: Duplicate in same profile, or port currently occupied by running container/process
}

public class PortUsageItem
{
    public string SourceType { get; set; } = string.Empty; // "Actieve Docker Container", "Profiel: Werk", "Lokaal Systeem Proces"
    public string Name { get; set; } = string.Empty;       // Container name or Profile name
    public string Details { get; set; } = string.Empty;    // e.g. "8080 -> 80 (TCP) - Status: running"
    public bool IsActive { get; set; }                     // Whether it is currently running right now
}

public class PortAnalysisItem
{
    public int Port { get; set; }
    public string Protocol { get; set; } = "tcp";
    public PortConflictSeverity Severity { get; set; } = PortConflictSeverity.Ok;
    public string StatusText { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Recommendation { get; set; } = string.Empty;
    public List<PortUsageItem> Usages { get; set; } = new();

    public string SeverityColor => Severity switch
    {
        PortConflictSeverity.Conflict => "#EF4444", // Red
        PortConflictSeverity.Warning => "#F59E0B",  // Amber / Yellow
        _ => "#10B981"                              // Green
    };

    public string SeverityBadgeText => Severity switch
    {
        PortConflictSeverity.Conflict => "🔴 Conflict",
        PortConflictSeverity.Warning => "🟡 Waarschuwing",
        _ => "🟢 Vrij / OK"
    };

    public string UsagesSummary => Usages.Count > 0
        ? string.Join(" | ", Usages.Select(u => $"{u.Name}: {u.Details}"))
        : "Geen actieve toewijzingen";
}

public class ActiveDockerPortInfo
{
    public string ContainerName { get; set; } = string.Empty;
    public string ContainerId { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public int PublicPort { get; set; }
    public int PrivatePort { get; set; }
    public string Protocol { get; set; } = "tcp";
    public string IpAddress { get; set; } = "0.0.0.0";
}
