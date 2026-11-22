using System.Collections.ObjectModel;
using System.Net.NetworkInformation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DockerManager.App.Models;
using DockerManager.App.Services;

namespace DockerManager.App.ViewModels;

public partial class PortConflictViewModel : ObservableObject
{
    private readonly IDockerService _dockerService;
    private readonly List<ProfileModel> _profiles;
    private readonly string _currentProfileName;
    private readonly string _currentServerName;
    private readonly bool _isRemoteServer;

    public string CurrentServerName => _currentServerName;
    public string CurrentProfileName => _currentProfileName;
    public bool IsRemoteServer => _isRemoteServer;
    public string ServerBadgeText => $"🖥️ Server: {_currentServerName}";
    public string ProfileBadgeText => $"📄 Profiel: {(string.IsNullOrWhiteSpace(_currentProfileName) ? "Alle profielen" : _currentProfileName)}";

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private string _statusSummary = "Analyse uitvoeren...";

    [ObservableProperty]
    private int _conflictCount;

    [ObservableProperty]
    private int _warningCount;

    [ObservableProperty]
    private int _okCount;

    [ObservableProperty]
    private int _totalPortsCount;

    [ObservableProperty]
    private string _selectedFilter = "Issues"; // "All", "Issues", "Ok"

    [ObservableProperty]
    private string _searchText = string.Empty;

    public ObservableCollection<PortAnalysisItem> AllItems { get; } = new();
    public ObservableCollection<PortAnalysisItem> FilteredItems { get; } = new();

    public event Action? RequestClose;

    public PortConflictViewModel(
        IDockerService dockerService,
        IEnumerable<ProfileModel> profiles,
        string currentProfileName = "",
        string currentServerName = "Lokale Docker Desktop",
        bool isRemoteServer = false)
    {
        _dockerService = dockerService;
        _profiles = profiles.ToList();
        _currentProfileName = currentProfileName;
        _currentServerName = string.IsNullOrWhiteSpace(currentServerName) ? "Lokale Docker Desktop" : currentServerName;
        _isRemoteServer = isRemoteServer;
    }

    public async Task InitializeAsync()
    {
        await ScanAsync();
    }

    [RelayCommand]
    public async Task ScanAsync()
    {
        if (IsScanning) return;
        IsScanning = true;
        StatusSummary = $"Docker containers en poorten controleren op '{_currentServerName}'...";

        try
        {
            AllItems.Clear();

            // 1. Get active Docker containers & ports
            var activeDockerPorts = await _dockerService.GetActiveDockerPortsAsync();
            var runningDockerPorts = activeDockerPorts
                .Where(p => p.State.Equals("running", StringComparison.OrdinalIgnoreCase))
                .ToList();

            // 2. Get local active TCP/UDP listeners on host OS (only if local server, not remote VPS)
            HashSet<int> localTcpListeners = new();
            HashSet<int> localUdpListeners = new();
            if (!_isRemoteServer)
            {
                try
                {
                    var ipProps = IPGlobalProperties.GetIPGlobalProperties();
                    foreach (var ep in ipProps.GetActiveTcpListeners())
                    {
                        localTcpListeners.Add(ep.Port);
                    }
                    foreach (var ep in ipProps.GetActiveUdpListeners())
                    {
                        localUdpListeners.Add(ep.Port);
                    }
                }
                catch { }
            }

            // 3. Collect all ports defined across all profiles keyed by (HostPort, Protocol)
            var profilePortMap = new Dictionary<(int Port, string Protocol), List<(ProfileModel Profile, ServiceDefinition Service, PortMapping Port)>>();

            foreach (var profile in _profiles)
            {
                foreach (var service in profile.Services)
                {
                    foreach (var port in service.Ports)
                    {
                        if (port.HostPort <= 0) continue;

                        var key = (port.HostPort, NormalizeProtocol(port.Protocol));
                        if (!profilePortMap.TryGetValue(key, out var list))
                        {
                            list = new List<(ProfileModel, ServiceDefinition, PortMapping)>();
                            profilePortMap[key] = list;
                        }
                        list.Add((profile, service, port));
                    }
                }
            }

            // Also collect any running docker ports that are NOT in any profile
            var allAnalyzedPorts = new HashSet<(int Port, string Protocol)>(profilePortMap.Keys);
            foreach (var dp in runningDockerPorts)
            {
                allAnalyzedPorts.Add((dp.PublicPort, NormalizeProtocol(dp.Protocol)));
            }

            var results = new List<PortAnalysisItem>();

            foreach (var (portNumber, protocol) in allAnalyzedPorts.OrderBy(k => k.Port).ThenBy(k => k.Protocol))
            {
                var item = new PortAnalysisItem
                {
                    Port = portNumber,
                    Protocol = protocol
                };

                var usages = new List<PortUsageItem>();
                bool hasProfileConfig = profilePortMap.TryGetValue((portNumber, protocol), out var configuredList);

                // Find if any running Docker container uses this (port, protocol)
                var matchingActiveDocker = runningDockerPorts
                    .Where(dp => dp.PublicPort == portNumber && NormalizeProtocol(dp.Protocol) == protocol)
                    .ToList();

                foreach (var active in matchingActiveDocker)
                {
                    usages.Add(new PortUsageItem
                    {
                        SourceType = "Actieve Docker Container",
                        Name = active.ContainerName,
                        Details = $"{FormatHostPort(active.IpAddress, active.PublicPort)}→{active.PrivatePort}/{protocol} (Status: {active.State})",
                        IsActive = true
                    });
                }

                // Check profile configurations
                if (hasProfileConfig && configuredList != null)
                {
                    foreach (var (prof, svc, p) in configuredList)
                    {
                        var isCurrentlyRunning = matchingActiveDocker.Any(ad =>
                            MatchesService(ad, svc) && HostIpsOverlap(ad.IpAddress, p.HostIp));

                        usages.Add(new PortUsageItem
                        {
                            SourceType = $"Profiel '{prof.Name}'",
                            Name = svc.DisplayName,
                            Details = $"{FormatHostPort(p.HostIp, p.HostPort)}→{p.ContainerPort}/{protocol}" + (isCurrentlyRunning ? " [DRAAIT ACTIEF]" : ""),
                            IsActive = isCurrentlyRunning
                        });
                    }

                    // Check for duplicate in SAME profile with overlapping HostIp
                    var byProfile = configuredList.GroupBy(c => c.Profile.Name, StringComparer.OrdinalIgnoreCase).ToList();
                    var sameProfileDuplicates = byProfile
                        .Where(g => HasOverlappingHostIps(g.Select(x => x.Port.HostIp).ToList()))
                        .ToList();

                    // Active docker ports that actually overlap on HostIp with configured profile entries
                    var overlappingActive = matchingActiveDocker
                        .Where(ad => configuredList.Any(c => HostIpsOverlap(ad.IpAddress, c.Port.HostIp)))
                        .ToList();

                    if (sameProfileDuplicates.Count > 0)
                    {
                        var dupProfNames = string.Join(", ", sameProfileDuplicates.Select(g => $"'{g.Key}' ({g.Count()} containers)"));
                        item.Severity = PortConflictSeverity.Conflict;
                        item.StatusText = "Dubbel geconfigureerd in hetzelfde profiel";
                        item.Description = $"Poort {portNumber}/{protocol} is meerdere keren met overlappende Host-IP toegewezen binnen profiel {dupProfNames}. Deze containers kunnen niet gelijktijdig draaien.";
                        item.Recommendation = "Wijzig de hostpoort (of het bind IP-adres) van één van de containers via 'Bewerken'.";
                    }
                    else if (overlappingActive.Count > 0)
                    {
                        var activeNames = string.Join(", ", overlappingActive.Select(a => $"'{a.ContainerName}'"));

                        var externalActive = overlappingActive
                            .Where(ad => !configuredList.Any(c => MatchesService(ad, c.Service) && HostIpsOverlap(ad.IpAddress, c.Port.HostIp)))
                            .ToList();

                        if (externalActive.Count > 0)
                        {
                            var extNames = string.Join(", ", externalActive.Select(a => $"'{a.ContainerName}'"));
                            var claimedNames = string.Join(", ", configuredList.Select(c => $"'{c.Service.DisplayName}' ({c.Profile.Name})"));
                            item.Severity = PortConflictSeverity.Conflict;
                            item.StatusText = "Poort bezet door externe Docker container";
                            item.Description = $"Poort {portNumber}/{protocol} wordt momenteel gebruikt door externe Docker container {extNames}, terwijl {claimedNames} deze poort geconfigureerd heeft.";
                            item.Recommendation = $"Stop container {extNames} of wijzig de hostpoort van het profiel via 'Bewerken'.";
                        }
                        else
                        {
                            var unclaimedConflicts = configuredList
                                .Where(c => overlappingActive.Any(ad => HostIpsOverlap(ad.IpAddress, c.Port.HostIp) && !MatchesService(ad, c.Service)))
                                .ToList();

                            if (unclaimedConflicts.Count > 0)
                            {
                                item.Severity = PortConflictSeverity.Conflict;
                                item.StatusText = "In gebruik door actieve container & geclaimd door ander profiel";
                                item.Description = $"Poort {portNumber}/{protocol} draait momenteel in Docker ({activeNames}). Andere profielen met deze poort zullen niet kunnen starten zolang deze container actief is.";
                                item.Recommendation = $"Stop container {activeNames} of pas de poort aan in de andere profielen.";
                            }
                            else
                            {
                                item.Severity = PortConflictSeverity.Ok;
                                item.StatusText = "Actief in gebruik (Normaal)";
                                item.Description = $"Poort {portNumber}/{protocol} is correct toegewezen en draait momenteel in Docker ({activeNames}).";
                                item.Recommendation = "Geen actie vereist.";
                            }
                        }
                    }
                    else if (HasCrossProfileOverlap(byProfile))
                    {
                        // Configured across multiple profiles with overlapping HostIp, not yet running
                        var profNames = string.Join(", ", byProfile.Select(g => $"'{g.Key}'"));
                        item.Severity = PortConflictSeverity.Warning;
                        item.StatusText = "Gedeeld tussen meerdere profielen";
                        item.Description = $"Poort {portNumber}/{protocol} wordt gebruikt in meerdere profielen ({profNames}). Dit is geen probleem zolang je slechts één profiel tegelijk start.";
                        item.Recommendation = "Als je deze profielen tegelijk wilt draaien, wijzig dan de hostpoort in één van de profielen.";
                    }
                    else
                    {
                        // Configured cleanly, not currently running
                        var localListeners = protocol == "udp" ? localUdpListeners : localTcpListeners;
                        bool hasLocalHostListener = localListeners.Contains(portNumber);
                        if (hasLocalHostListener)
                        {
                            item.Severity = PortConflictSeverity.Warning;
                            item.StatusText = "Mogelijk lokaal Windows-proces actief";
                            item.Description = $"Poort {portNumber}/{protocol} lijkt bezet door een lokaal Windows-programma op de host. Docker starten kan mogelijk falen als de poort exclusief gebonden is.";
                            item.Recommendation = "Controleer of er een lokale service (zoals IIS of een lokale database) draait.";
                        }
                        else
                        {
                            item.Severity = PortConflictSeverity.Ok;
                            item.StatusText = "Vrij & Beschikbaar";
                            item.Description = $"Poort {portNumber}/{protocol} is uniek geconfigureerd en direct beschikbaar.";
                            item.Recommendation = "Geen actie vereist.";
                        }
                    }
                }
                else
                {
                    // Running in Docker, but not defined in any loaded profile
                    var activeNames = string.Join(", ", matchingActiveDocker.Select(a => $"'{a.ContainerName}'"));
                    item.Severity = PortConflictSeverity.Warning;
                    item.StatusText = "Actief in Docker (Niet in profiel)";
                    item.Description = $"Poort {portNumber}/{protocol} is bezet door een externe Docker container ({activeNames}) die niet in de profielen staat.";
                    item.Recommendation = "Houd hier rekening mee bij het toewijzen van nieuwe poorten.";
                }

                item.Usages = usages;
                results.Add(item);
            }

            // Order by severity first (Conflict -> Warning -> Ok), then by Port
            foreach (var r in results.OrderBy(r => r.Severity == PortConflictSeverity.Conflict ? 0 : r.Severity == PortConflictSeverity.Warning ? 1 : 2).ThenBy(r => r.Port))
            {
                AllItems.Add(r);
            }

            // Update stats
            ConflictCount = AllItems.Count(i => i.Severity == PortConflictSeverity.Conflict);
            WarningCount = AllItems.Count(i => i.Severity == PortConflictSeverity.Warning);
            OkCount = AllItems.Count(i => i.Severity == PortConflictSeverity.Ok);
            TotalPortsCount = AllItems.Count;

            if (ConflictCount > 0)
            {
                StatusSummary = $"⚠️ {ConflictCount} poortconflict(en) en {WarningCount} waarschuwing(en) gevonden.";
                SelectedFilter = "Issues";
            }
            else if (WarningCount > 0)
            {
                StatusSummary = $"ℹ️ Geen directe conflicten. {WarningCount} gedeelde poort(en) tussen profielen.";
                SelectedFilter = "All";
            }
            else
            {
                StatusSummary = $"✅ Uitstekend! Geen poortconflicten gevonden over alle {TotalPortsCount} gecontroleerde poorten.";
                SelectedFilter = "All";
            }

            ApplyFilter();
        }
        catch (Exception ex)
        {
            StatusSummary = $"Fout bij poortcontrole: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
        }
    }

    partial void OnSelectedFilterChanged(string value) => ApplyFilter();
    partial void OnSearchTextChanged(string value) => ApplyFilter();

    [RelayCommand]
    private void SetFilter(string filter)
    {
        SelectedFilter = filter;
    }

    private void ApplyFilter()
    {
        FilteredItems.Clear();
        var query = AllItems.AsEnumerable();

        if (SelectedFilter == "Issues")
        {
            query = query.Where(i => i.Severity == PortConflictSeverity.Conflict || i.Severity == PortConflictSeverity.Warning);
        }
        else if (SelectedFilter == "Conflicts")
        {
            query = query.Where(i => i.Severity == PortConflictSeverity.Conflict);
        }
        else if (SelectedFilter == "Ok")
        {
            query = query.Where(i => i.Severity == PortConflictSeverity.Ok);
        }

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var s = SearchText.Trim().ToLowerInvariant();
            query = query.Where(i =>
                i.Port.ToString().Contains(s) ||
                i.StatusText.ToLowerInvariant().Contains(s) ||
                i.Description.ToLowerInvariant().Contains(s) ||
                i.Usages.Any(u => u.Name.ToLowerInvariant().Contains(s) || u.Details.ToLowerInvariant().Contains(s) || u.SourceType.ToLowerInvariant().Contains(s)));
        }

        foreach (var item in query)
        {
            FilteredItems.Add(item);
        }
    }

    [RelayCommand]
    private void Close()
    {
        RequestClose?.Invoke();
    }

    internal static bool HostIpsOverlap(string? ipA, string? ipB)
    {
        var a = string.IsNullOrWhiteSpace(ipA) ? "0.0.0.0" : ipA.Trim();
        var b = string.IsNullOrWhiteSpace(ipB) ? "0.0.0.0" : ipB.Trim();
        if (IsWildcardIp(a) || IsWildcardIp(b))
            return true;
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWildcardIp(string ip) =>
        ip == "0.0.0.0" || ip == "::" || ip == "*";

    private static string NormalizeProtocol(string? protocol) =>
        string.IsNullOrWhiteSpace(protocol) ? "tcp" : protocol.Trim().ToLowerInvariant();

    private static string FormatHostPort(string? hostIp, int port) =>
        string.IsNullOrWhiteSpace(hostIp) || hostIp == "0.0.0.0" ? port.ToString() : $"{hostIp.Trim()}:{port}";

    private static bool MatchesService(ActiveDockerPortInfo active, ServiceDefinition svc)
    {
        var activeName = active.ContainerName?.TrimStart('/') ?? string.Empty;
        if (string.IsNullOrWhiteSpace(activeName))
            return false;

        return activeName.Equals(svc.GetEffectiveContainerName(), StringComparison.OrdinalIgnoreCase) ||
               (!string.IsNullOrWhiteSpace(svc.ContainerName) && activeName.Equals(svc.ContainerName.Trim(), StringComparison.OrdinalIgnoreCase)) ||
               (!string.IsNullOrWhiteSpace(svc.Id) && activeName.Equals(svc.Id.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasOverlappingHostIps(IReadOnlyList<string?> hostIps)
    {
        for (int i = 0; i < hostIps.Count; i++)
        {
            for (int j = i + 1; j < hostIps.Count; j++)
            {
                if (HostIpsOverlap(hostIps[i], hostIps[j]))
                    return true;
            }
        }
        return false;
    }

    private static bool HasCrossProfileOverlap(List<IGrouping<string, (ProfileModel Profile, ServiceDefinition Service, PortMapping Port)>> byProfile)
    {
        if (byProfile.Count < 2) return false;
        for (int i = 0; i < byProfile.Count; i++)
        {
            for (int j = i + 1; j < byProfile.Count; j++)
            {
                foreach (var a in byProfile[i])
                {
                    foreach (var b in byProfile[j])
                    {
                        if (HostIpsOverlap(a.Port.HostIp, b.Port.HostIp))
                            return true;
                    }
                }
            }
        }
        return false;
    }
}
