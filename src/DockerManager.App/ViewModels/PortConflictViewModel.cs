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
        string currentProfileName = "")
    {
        _dockerService = dockerService;
        _profiles = profiles.ToList();
        _currentProfileName = currentProfileName;
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
        StatusSummary = "Docker containers en poorten controleren...";

        try
        {
            AllItems.Clear();

            // 1. Get active Docker containers & ports
            var activeDockerPorts = await _dockerService.GetActiveDockerPortsAsync();
            var runningDockerPorts = activeDockerPorts
                .Where(p => p.State.Equals("running", StringComparison.OrdinalIgnoreCase))
                .ToList();

            // 2. Get local active TCP listeners on host OS
            HashSet<int> localTcpListeners = new();
            try
            {
                var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
                foreach (var ep in listeners)
                {
                    localTcpListeners.Add(ep.Port);
                }
            }
            catch { }

            // 3. Collect all ports defined across all profiles
            // Key: HostPort
            var profilePortMap = new Dictionary<int, List<(ProfileModel Profile, ServiceDefinition Service, PortMapping Port)>>();

            foreach (var profile in _profiles)
            {
                foreach (var service in profile.Services)
                {
                    foreach (var port in service.Ports)
                    {
                        if (port.HostPort <= 0) continue;

                        if (!profilePortMap.TryGetValue(port.HostPort, out var list))
                        {
                            list = new List<(ProfileModel, ServiceDefinition, PortMapping)>();
                            profilePortMap[port.HostPort] = list;
                        }
                        list.Add((profile, service, port));
                    }
                }
            }

            // Also collect any running docker ports that are NOT in any profile
            var allAnalyzedPorts = new HashSet<int>(profilePortMap.Keys);
            foreach (var dp in runningDockerPorts)
            {
                allAnalyzedPorts.Add(dp.PublicPort);
            }

            var results = new List<PortAnalysisItem>();

            foreach (var portNumber in allAnalyzedPorts.OrderBy(p => p))
            {
                var item = new PortAnalysisItem
                {
                    Port = portNumber,
                    Protocol = "tcp"
                };

                var usages = new List<PortUsageItem>();
                bool hasProfileConfig = profilePortMap.TryGetValue(portNumber, out var configuredList);

                // Find if any running Docker container uses this port
                var matchingActiveDocker = runningDockerPorts.Where(dp => dp.PublicPort == portNumber).ToList();
                foreach (var active in matchingActiveDocker)
                {
                    usages.Add(new PortUsageItem
                    {
                        SourceType = "Actieve Docker Container",
                        Name = active.ContainerName,
                        Details = $"{active.PublicPort}→{active.PrivatePort}/{active.Protocol} (Status: {active.State})",
                        IsActive = true
                    });
                }

                // Check profile configurations
                if (hasProfileConfig && configuredList != null)
                {
                    foreach (var (prof, svc, p) in configuredList)
                    {
                        item.Protocol = p.Protocol;
                        var isCurrentlyRunning = matchingActiveDocker.Any(ad => 
                            ad.ContainerName.Equals(svc.ContainerName, StringComparison.OrdinalIgnoreCase) ||
                            ad.ContainerName.Equals(svc.Id, StringComparison.OrdinalIgnoreCase));

                        usages.Add(new PortUsageItem
                        {
                            SourceType = $"Profiel '{prof.Name}'",
                            Name = svc.DisplayName,
                            Details = $"{p.HostPort}→{p.ContainerPort}/{p.Protocol}" + (isCurrentlyRunning ? " [DRAAIT ACTIEF]" : ""),
                            IsActive = isCurrentlyRunning
                        });
                    }

                    // Check for duplicate in SAME profile
                    var byProfile = configuredList.GroupBy(c => c.Profile.Name).ToList();
                    var sameProfileDuplicates = byProfile.Where(g => g.Count() > 1).ToList();

                    if (sameProfileDuplicates.Count > 0)
                    {
                        var dupProfNames = string.Join(", ", sameProfileDuplicates.Select(g => $"'{g.Key}' ({g.Count()} containers)"));
                        item.Severity = PortConflictSeverity.Conflict;
                        item.StatusText = "Dubbel geconfigureerd in hetzelfde profiel";
                        item.Description = $"Poort {portNumber} is meerdere keren toegewezen binnen profiel {dupProfNames}. Deze containers kunnen niet gelijktijdig draaien.";
                        item.Recommendation = "Wijzig de hostpoort van één van de containers via 'Bewerken' naar een vrij poortnummer.";
                    }
                    else if (matchingActiveDocker.Count > 0)
                    {
                        // Port is active in Docker
                        var activeNames = string.Join(", ", matchingActiveDocker.Select(a => $"'{a.ContainerName}'"));
                        
                        // Check if the running container is the ONLY configured one, or if others also want this port
                        if (configuredList.Count > 1)
                        {
                            item.Severity = PortConflictSeverity.Conflict;
                            item.StatusText = "In gebruik door actieve container & geclaimd door ander profiel";
                            item.Description = $"Poort {portNumber} draait momenteel in Docker ({activeNames}). Andere profielen met deze poort zullen niet kunnen starten zolang deze container actief is.";
                            item.Recommendation = $"Stop container {activeNames} of pas de poort aan in de andere profielen.";
                        }
                        else
                        {
                            item.Severity = PortConflictSeverity.Ok;
                            item.StatusText = "Actief in gebruik (Normaal)";
                            item.Description = $"Poort {portNumber} is correct toegewezen en draait momenteel in Docker ({activeNames}).";
                            item.Recommendation = "Geen actie vereist.";
                        }
                    }
                    else if (byProfile.Count > 1)
                    {
                        // Configured across multiple profiles, but not yet running
                        var profNames = string.Join(", ", byProfile.Select(g => $"'{g.Key}'"));
                        item.Severity = PortConflictSeverity.Warning;
                        item.StatusText = "Gedeeld tussen meerdere profielen";
                        item.Description = $"Poort {portNumber} wordt gebruikt in meerdere profielen ({profNames}). Dit is geen probleem zolang je slechts één profiel tegelijk start.";
                        item.Recommendation = "Als je deze profielen tegelijk wilt draaien, wijzig dan de hostpoort in één van de profielen.";
                    }
                    else
                    {
                        // Configured in 1 profile, not currently running
                        bool hasLocalHostListener = localTcpListeners.Contains(portNumber);
                        if (hasLocalHostListener)
                        {
                            item.Severity = PortConflictSeverity.Warning;
                            item.StatusText = "Mogelijk lokaal Windows-proces actief";
                            item.Description = $"Poort {portNumber} lijkt bezet door een lokaal Windows-programma op de host. Docker starten kan mogelijk falen als de poort exclusief gebonden is.";
                            item.Recommendation = "Controleer of er een lokale service (zoals IIS of een lokale database) draait.";
                        }
                        else
                        {
                            item.Severity = PortConflictSeverity.Ok;
                            item.StatusText = "Vrij & Beschikbaar";
                            item.Description = $"Poort {portNumber} is uniek geconfigureerd in profiel '{configuredList.First().Profile.Name}' en direct beschikbaar.";
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
                    item.Description = $"Poort {portNumber} is bezet door een externe Docker container ({activeNames}) die niet in de profielen staat.";
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
}
