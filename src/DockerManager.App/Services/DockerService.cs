using System.IO;
using System.Net.Http;
using System.Text;
using Docker.DotNet;
using Docker.DotNet.Models;
using DockerManager.App.Models;

namespace DockerManager.App.Services;

public class ImageInspectResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public List<int> ExposedPorts { get; set; } = new();
    public Dictionary<string, string> EnvironmentVariables { get; set; } = new();
    public string? WorkingDir { get; set; }
    public string? Command { get; set; }
    public string? SuggestedDisplayName { get; set; }
    public string? SuggestedContainerName { get; set; }
}

public class DockerStartupDiagnosis
{
    public bool IsConnected { get; set; }
    public bool IsDockerDesktopInstalled { get; set; }
    public string? DockerDesktopExecutablePath { get; set; }
    public bool? IsDockerDesktopAutoStartEnabled { get; set; }
    public string? WindowsServiceStartMode { get; set; } // "Automatic", "Manual", "Disabled"
    public string DiagnosisSummary { get; set; } = string.Empty;
    public string DetailedTip { get; set; } = string.Empty;
}

public record ContainerUpdateResult(bool WasUpdated, string Message, string? OldImageId, string? NewImageId);

public interface IDockerService
{
    Task<(bool IsConnected, string VersionOrError)> PingDockerAsync(CancellationToken ct = default);
    Task<DockerStartupDiagnosis> DiagnoseDockerStartupAsync(CancellationToken ct = default);
    Task<bool> TryLaunchDockerDesktopAsync();
    Task<ContainerRuntimeInfo> GetContainerInfoAsync(string containerName, CancellationToken ct = default);
    Task<List<ContainerRuntimeInfo>> GetAllContainersAsync(CancellationToken ct = default);
    Task StartContainerAsync(ServiceDefinition service, string profileName, IProgress<string>? progress = null, ProfileRegistryConfig? registryConfig = null, string? volumesSubfolder = null, CancellationToken ct = default);
    Task StopContainerAsync(string containerName, CancellationToken ct = default);
    Task RestartContainerAsync(ServiceDefinition service, string profileName, IProgress<string>? progress = null, ProfileRegistryConfig? registryConfig = null, string? volumesSubfolder = null, CancellationToken ct = default);
    Task<ContainerUpdateResult> SafeUpdateContainerAsync(ServiceDefinition service, string profileName, IProgress<string>? progress = null, ProfileRegistryConfig? registryConfig = null, string? volumesSubfolder = null, CancellationToken ct = default);
    Task RemoveContainerAsync(string containerName, bool removeVolumes = false, CancellationToken ct = default);
    Task StreamLogsAsync(string containerName, Action<string> onLineReceived, CancellationToken ct = default);
    Task<string?> GetContainerStatsSummaryAsync(string containerName, CancellationToken ct = default);
    Task<(long SpaceReclaimedBytes, string Summary)> PruneUnusedResourcesAsync(CancellationToken ct = default);
    Task<List<ActiveDockerPortInfo>> GetActiveDockerPortsAsync(CancellationToken ct = default);
    Task<ImageInspectResult> InspectAndPullImageConfigAsync(string image, bool requiresAuth = false, IProgress<string>? progress = null, CancellationToken ct = default);
    Task<(bool Success, string Message)> TestRegistryConnectionAsync(RegistryCredential cred, CancellationToken ct = default);
    Task<bool> CheckImageUpdateAvailableAsync(ServiceDefinition service, ProfileRegistryConfig? registryConfig = null, CancellationToken ct = default);
    Task<Stream?> GetContainerArchiveStreamAsync(string containerName, string containerPath, CancellationToken ct = default);
    Task<bool> ExtractArchiveToContainerAsync(string containerName, string containerPath, Stream tarStream, CancellationToken ct = default);
}

public partial class DockerService : IDockerService, IDisposable
{
    private readonly ISettingsService _settingsService;
    private readonly ICredentialService _credentialService;
    private readonly HttpClient _registryHttpClient;
    private readonly bool _ownsRegistryHttpClient;
    private readonly object _clientLock = new();
    private readonly Dictionary<string, DockerClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    private sealed class SyncProgress<T>(Action<T> onReport) : IProgress<T>
    {
        public void Report(T value) => onReport(value);
    }

    public DockerService(ISettingsService settingsService, ICredentialService credentialService, HttpClient? registryHttpClient = null)
    {
        _settingsService = settingsService;
        _credentialService = credentialService;
        _ownsRegistryHttpClient = registryHttpClient == null;
        _registryHttpClient = registryHttpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        if (!_registryHttpClient.DefaultRequestHeaders.Contains("User-Agent"))
        {
            _registryHttpClient.DefaultRequestHeaders.Add("User-Agent", "DockerManager-App");
        }
    }

    private DockerClient GetClient()
    {
        var endpoint = _settingsService.Settings.GetEffectiveDockerUri();
        lock (_clientLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_clients.TryGetValue(endpoint, out var client))
            {
                client = new DockerClientConfiguration(new Uri(endpoint)).CreateClient();
                _clients[endpoint] = client;
            }
            return client;
        }
    }

    public void Dispose()
    {
        lock (_clientLock)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var client in _clients.Values)
            {
                try { client.Dispose(); } catch { }
            }
            _clients.Clear();
        }

        if (_ownsRegistryHttpClient)
        {
            try { _registryHttpClient.Dispose(); } catch { }
        }
        GC.SuppressFinalize(this);
    }

    public async Task<(bool IsConnected, string VersionOrError)> PingDockerAsync(CancellationToken ct = default)
    {
        try
        {
            var client = GetClient();
            await client.System.PingAsync(ct);
            var version = await client.System.GetVersionAsync(ct);
            return (true, $"Docker {version.Version} (API {version.APIVersion}, OS: {version.Os})");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public async Task<List<ContainerRuntimeInfo>> GetAllContainersAsync(CancellationToken ct = default)
    {
        var list = new List<ContainerRuntimeInfo>();
        try
        {
            var client = GetClient();
            var containers = await client.Containers.ListContainersAsync(new ContainersListParameters { All = true }, ct);
            foreach (var c in containers)
            {
                var name = c.Names.FirstOrDefault()?.TrimStart('/') ?? c.ID[..12];
                list.Add(new ContainerRuntimeInfo
                {
                    ContainerId = c.ID,
                    ContainerName = name,
                    Image = c.Image,
                    State = ParseState(c.State),
                    StatusText = c.Status,
                    StartedAt = c.Created
                });
            }
        }
        catch
        {
            // Return empty list if docker is not reachable
        }
        return list;
    }

    public async Task<ContainerRuntimeInfo> GetContainerInfoAsync(string containerName, CancellationToken ct = default)
    {
        try
        {
            var client = GetClient();
            var inspect = await client.Containers.InspectContainerAsync(containerName, ct);
            return new ContainerRuntimeInfo
            {
                ContainerId = inspect.ID,
                ContainerName = inspect.Name.TrimStart('/'),
                Image = inspect.Config.Image,
                State = ParseState(inspect.State.Status),
                StatusText = $"{inspect.State.Status} (PID: {inspect.State.Pid})",
                StartedAt = DateTime.TryParse(inspect.State.StartedAt, out var dt) ? dt : null,
                HealthStatus = inspect.State.Health?.Status
            };
        }
        catch (DockerContainerNotFoundException)
        {
            return new ContainerRuntimeInfo
            {
                ContainerName = containerName,
                State = Models.ContainerState.NotCreated,
                StatusText = LocalizationService.Instance.Get("Container_NotCreated")
            };
        }
        catch (Exception ex)
        {
            return new ContainerRuntimeInfo
            {
                ContainerName = containerName,
                State = Models.ContainerState.Error,
                StatusText = "Fout bij controleren",
                ErrorMessage = ex.Message
            };
        }
    }

    public async Task StartContainerAsync(ServiceDefinition service, string profileName, IProgress<string>? progress = null, ProfileRegistryConfig? registryConfig = null, string? volumesSubfolder = null, CancellationToken ct = default)
    {
        var client = GetClient();
        var containerName = service.GetEffectiveContainerName();

        ContainerInspectResponse? inspect = null;
        try
        {
            inspect = await client.Containers.InspectContainerAsync(containerName, ct);
        }
        catch (DockerContainerNotFoundException)
        {
            inspect = null;
        }

        if (inspect == null)
        {
            // Container doesn't exist yet: Pull image & Create
            progress?.Report($"Image '{service.Image}' ophalen van registry...");
            await PullImageAsync(client, service, progress, registryConfig, ct);
            progress?.Report($"Container '{containerName}' aanmaken...");
            var containerId = await CreateContainerInternalAsync(client, service, profileName, volumesSubfolder, ct);
            progress?.Report($"Container '{containerName}' starten...");
            await client.Containers.StartContainerAsync(containerId, new ContainerStartParameters(), ct);
            progress?.Report($"Container '{containerName}' succesvol gestart.");
        }
        else
        {
            if (!inspect.State.Running)
            {
                progress?.Report($"Bestaande container '{containerName}' starten...");
                await client.Containers.StartContainerAsync(inspect.ID, new ContainerStartParameters(), ct);
                progress?.Report($"Container '{containerName}' gestart.");
            }
            else
            {
                progress?.Report($"Container '{containerName}' draait al.");
            }
        }
    }

    public async Task StopContainerAsync(string containerName, CancellationToken ct = default)
    {
        var client = GetClient();
        try
        {
            await client.Containers.StopContainerAsync(containerName, new ContainerStopParameters { WaitBeforeKillSeconds = 10 }, ct);
        }
        catch (DockerContainerNotFoundException)
        {
            // Already gone
        }
    }

    public async Task RestartContainerAsync(ServiceDefinition service, string profileName, IProgress<string>? progress = null, ProfileRegistryConfig? registryConfig = null, string? volumesSubfolder = null, CancellationToken ct = default)
    {
        var client = GetClient();
        var containerName = service.GetEffectiveContainerName();
        progress?.Report($"Container '{containerName}' herstarten met actuele configuratie...");

        // Ensure the image exists locally BEFORE stopping and deleting the existing container
        try
        {
            await client.Images.InspectImageAsync(service.Image, ct);
        }
        catch (DockerImageNotFoundException)
        {
            progress?.Report($"Image '{service.Image}' ontbreekt lokaal; eerst downloaden...");
            await PullImageAsync(client, service, progress, registryConfig, ct);
        }

        try
        {
            await client.Containers.StopContainerAsync(containerName, new ContainerStopParameters { WaitBeforeKillSeconds = 5 }, ct);
        }
        catch (DockerContainerNotFoundException) { }

        try
        {
            await client.Containers.RemoveContainerAsync(containerName, new ContainerRemoveParameters { Force = true, RemoveVolumes = false }, ct);
        }
        catch (DockerContainerNotFoundException) { }

        var containerId = await CreateContainerInternalAsync(client, service, profileName, volumesSubfolder, ct);
        await client.Containers.StartContainerAsync(containerId, new ContainerStartParameters(), ct);
        progress?.Report($"Container '{containerName}' succesvol herstart met actuele instellingen.");
    }

    public async Task<ContainerUpdateResult> SafeUpdateContainerAsync(ServiceDefinition service, string profileName, IProgress<string>? progress = null, ProfileRegistryConfig? registryConfig = null, string? volumesSubfolder = null, CancellationToken ct = default)
    {
        var client = GetClient();
        var containerName = service.GetEffectiveContainerName();

        string? oldImageId = null;
        bool isCurrentlyRunning = false;
        try
        {
            var inspectOld = await client.Containers.InspectContainerAsync(containerName, ct);
            oldImageId = inspectOld?.Image;
            isCurrentlyRunning = inspectOld?.State?.Running == true;
        }
        catch (DockerContainerNotFoundException) { }

        // Step 1: Pull the latest image layer in the background while container keeps running!
        progress?.Report($"Nieuwste image '{service.Image}' controleren op registry...");
        var hadNewerLayers = await PullImageAsync(client, service, progress, registryConfig, ct);

        string? newImageId = null;
        try
        {
            var inspectImage = await client.Images.InspectImageAsync(service.Image, ct);
            newImageId = inspectImage?.ID;
        }
        catch { }

        bool imageActuallyUpdated = hadNewerLayers || (oldImageId != null && newImageId != null && !oldImageId.Equals(newImageId, StringComparison.OrdinalIgnoreCase));

        // If the container is already running and the image has NOT changed: keep running without downtime!
        if (!imageActuallyUpdated && isCurrentlyRunning)
        {
            progress?.Report($"✅ '{containerName}' draait al op de nieuwste versie. Container blijft actief zonder downtime.");
            return new ContainerUpdateResult(false, "Reeds up-to-date (Geen onderbreking)", oldImageId, newImageId ?? oldImageId);
        }

        // Only if there is a new image (or if the container was stopped/missing): proceed with recreation
        if (isCurrentlyRunning)
        {
            progress?.Report($"Nieuwe versie beschikbaar! Oude container '{containerName}' stoppen...");
            try
            {
                await client.Containers.StopContainerAsync(containerName, new ContainerStopParameters { WaitBeforeKillSeconds = 10 }, ct);
            }
            catch (DockerContainerNotFoundException) { }
        }

        // Step 3: Remove old container (RemoveVolumes = FALSE to preserve all data!)
        progress?.Report($"Container '{containerName}' vervangen met behoud van data en volumes...");
        try
        {
            await client.Containers.RemoveContainerAsync(containerName, new ContainerRemoveParameters { Force = true, RemoveVolumes = false }, ct);
        }
        catch (DockerContainerNotFoundException) { }

        // Step 4: Re-create container with identical settings
        progress?.Report($"Nieuwe container '{containerName}' aanmaken...");
        var containerId = await CreateContainerInternalAsync(client, service, profileName, volumesSubfolder, ct);

        // Step 5: Start container
        progress?.Report($"Container '{containerName}' starten...");
        await client.Containers.StartContainerAsync(containerId, new ContainerStartParameters(), ct);

        if (imageActuallyUpdated)
        {
            progress?.Report($"✨ Update van '{containerName}' succesvol geïnstalleerd!");
            return new ContainerUpdateResult(true, "Update geïnstalleerd (Nieuwe image)", oldImageId, newImageId);
        }
        else
        {
            progress?.Report($"✅ '{containerName}' herstart met actuele instellingen.");
            return new ContainerUpdateResult(false, "Herstart met actuele instellingen", oldImageId, newImageId);
        }
    }

    public async Task RemoveContainerAsync(string containerName, bool removeVolumes = false, CancellationToken ct = default)
    {
        var client = GetClient();
        try
        {
            await client.Containers.RemoveContainerAsync(containerName, new ContainerRemoveParameters { Force = true, RemoveVolumes = removeVolumes }, ct);
        }
        catch (DockerContainerNotFoundException) { }
    }

    public async Task StreamLogsAsync(string containerName, Action<string> onLineReceived, CancellationToken ct = default)
    {
        try
        {
            var client = GetClient();
            var inspect = await client.Containers.InspectContainerAsync(containerName, ct);
            bool isTty = inspect.Config?.Tty == true;

            var parameters = new ContainerLogsParameters
            {
                ShowStdout = true,
                ShowStderr = true,
                Follow = true,
                Tail = "200",
                Timestamps = true
            };

            using var stream = await client.Containers.GetContainerLogsAsync(containerName, isTty, parameters, ct);
            var buffer = new byte[4096];
            var decoder = Encoding.UTF8.GetDecoder();
            var charBuf = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length) + 1];
            var lineBuf = new StringBuilder();

            while (!ct.IsCancellationRequested)
            {
                var result = await stream.ReadOutputAsync(buffer, 0, buffer.Length, ct);
                if (result.EOF)
                {
                    int tailChars = decoder.GetChars(Array.Empty<byte>(), 0, 0, charBuf, 0, flush: true);
                    if (tailChars > 0)
                    {
                        AppendDecodedChunk(lineBuf, charBuf, tailChars, onLineReceived);
                    }
                    if (lineBuf.Length > 0)
                    {
                        var remaining = lineBuf.ToString().TrimEnd('\r');
                        if (remaining.Length > 0)
                        {
                            onLineReceived(remaining);
                        }
                        lineBuf.Clear();
                    }
                    break;
                }

                if (result.Count > 0)
                {
                    int charCount = decoder.GetChars(buffer, 0, result.Count, charBuf, 0, flush: false);
                    AppendDecodedChunk(lineBuf, charBuf, charCount, onLineReceived);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (DockerContainerNotFoundException)
        {
            onLineReceived($"⚠️ De container '{containerName}' bestaat momenteel niet in Docker.");
            onLineReceived("💡 Klik eerst op '▶ Start' op de container-kaart om de image te downloaden en de container te starten.");
        }
        catch (Exception ex)
        {
            onLineReceived($"⚠️ Fout bij ophalen van logs: {ex.Message}");
        }
    }

    private static void AppendDecodedChunk(StringBuilder lineBuf, char[] charBuf, int charCount, Action<string> onLineReceived)
    {
        for (int i = 0; i < charCount; i++)
        {
            char c = charBuf[i];
            if (c == '\n')
            {
                var line = lineBuf.ToString().TrimEnd('\r');
                if (line.Length > 0)
                {
                    onLineReceived(line);
                }
                lineBuf.Clear();
            }
            else
            {
                lineBuf.Append(c);
            }
        }
    }

    private async Task<string> CreateContainerInternalAsync(DockerClient client, ServiceDefinition service, string profileName, string? volumesSubfolder, CancellationToken ct)
    {
        var containerName = service.GetEffectiveContainerName();

        // Port bindings
        var portBindings = new Dictionary<string, IList<PortBinding>>();
        var exposedPorts = new Dictionary<string, EmptyStruct>();

        foreach (var p in service.Ports)
        {
            var key = $"{p.ContainerPort}/{p.Protocol}";
            exposedPorts[key] = new EmptyStruct();
            portBindings[key] = new List<PortBinding>
            {
                new() 
                { 
                    HostIP = string.IsNullOrWhiteSpace(p.HostIp) ? "0.0.0.0" : p.HostIp,
                    HostPort = p.HostPort.ToString() 
                }
            };
        }

        // Volume mounts / binds
        var binds = new List<string>();
        foreach (var v in service.Volumes)
        {
            var resolvedHost = ResolveVolumeHostPath(v, profileName, containerName, volumesSubfolder);
            binds.Add($"{resolvedHost}:{v.ContainerPath}" + (v.ReadOnly ? ":ro" : ""));
        }

        // Environment
        var envList = service.Environment.Select(kvp => $"{kvp.Key}={kvp.Value}").ToList();

        // Labels
        var labels = new Dictionary<string, string>
        {
            ["com.dockermanager.managed"] = "true",
            ["com.dockermanager.profile"] = profileName,
            ["com.dockermanager.serviceid"] = service.Id
        };
        if (service.Labels != null)
        {
            foreach (var kvp in service.Labels)
            {
                labels[kvp.Key] = kvp.Value;
            }
        }

        var profileNetwork = await EnsureProfileNetworkAsync(client, profileName, ct);

        var hostConfig = new HostConfig
        {
            PortBindings = portBindings,
            Binds = binds,
            NetworkMode = !string.IsNullOrWhiteSpace(service.NetworkMode) ? service.NetworkMode : profileNetwork,
            ExtraHosts = new List<string> { "host.docker.internal:host-gateway" },
            RestartPolicy = new RestartPolicy
            {
                Name = ParseRestartPolicy(service.RestartPolicy)
            }
        };

        var createParams = new CreateContainerParameters
        {
            Name = containerName,
            Image = service.Image,
            ExposedPorts = exposedPorts,
            HostConfig = hostConfig,
            Env = envList,
            Labels = labels,
            Cmd = ResolveServiceCommand(service),
            WorkingDir = service.WorkingDir
        };

        var result = await client.Containers.CreateContainerAsync(createParams, ct);
        return result.ID;
    }

    private List<string>? ResolveServiceCommand(ServiceDefinition service)
    {
        if (service.Command == null || service.Command.Count == 0) return service.Command;

        const string tokenPlaceholder = "${CLOUDFLARE_TUNNEL_TOKEN}";
        if (!service.Command.Any(arg => arg.Contains(tokenPlaceholder, StringComparison.Ordinal)))
        {
            return service.Command;
        }

        var activeServer = _settingsService.Settings.GetActiveServer();
        var effectiveToken = activeServer?.CloudflareTunnelToken;
        if (string.IsNullOrWhiteSpace(effectiveToken) && activeServer != null && !string.IsNullOrWhiteSpace(activeServer.Id))
        {
            effectiveToken = _credentialService.GetServerCloudflareTunnelToken(activeServer.Id);
        }
        if (string.IsNullOrWhiteSpace(effectiveToken))
        {
            effectiveToken = _settingsService.Settings.CloudflareTunnelToken;
        }
        if (string.IsNullOrWhiteSpace(effectiveToken))
        {
            effectiveToken = _credentialService.GetCloudflareTunnelToken();
        }

        if (string.IsNullOrWhiteSpace(effectiveToken))
        {
            throw new InvalidOperationException("Cloudflare Tunnel Token ontbreekt in de server- of app-instellingen.");
        }

        var trimmedToken = effectiveToken.Trim();
        return service.Command
            .Select(arg => arg.Replace(tokenPlaceholder, trimmedToken, StringComparison.Ordinal))
            .ToList();
    }

    private async Task<string> EnsureProfileNetworkAsync(DockerClient client, string profileName, CancellationToken ct)
    {
        var safeProfile = string.Join("", profileName.Where(char.IsLetterOrDigit)).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(safeProfile)) safeProfile = "default";
        var networkName = $"dockermanager_{safeProfile}";

        try
        {
            var networks = await client.Networks.ListNetworksAsync(new NetworksListParameters(), ct);
            if (!networks.Any(n => n.Name.Equals(networkName, StringComparison.OrdinalIgnoreCase)))
            {
                await client.Networks.CreateNetworkAsync(new NetworksCreateParameters
                {
                    Name = networkName,
                    Driver = "bridge",
                    CheckDuplicate = true
                }, ct);
            }
        }
        catch { }

        return networkName;
    }

    internal static string StripRelativePrefix(string? path)
    {
        var p = (path ?? string.Empty).Trim().Replace('\\', '/');
        while (p.StartsWith("./", StringComparison.Ordinal))
        {
            p = p[2..];
        }
        return p.TrimStart('/');
    }

    internal string ResolveVolumeHostPath(VolumeMapping volume, string profileName, string containerName, string? profileVolumesSubfolder = null, DockerServerEnvironment? server = null)
    {
        if (volume.IsNamedVolume)
        {
            return volume.HostPath;
        }

        server ??= _settingsService.Settings.GetActiveServer();
        var isRemote = server != null
            ? server.HostType.Equals("Tcp", StringComparison.OrdinalIgnoreCase)
            : _settingsService.Settings.DockerHostType.Equals("Tcp", StringComparison.OrdinalIgnoreCase);
        var hostPath = volume.HostPath;

        string? sanitizedSubfolder = null;
        if (!string.IsNullOrWhiteSpace(profileVolumesSubfolder))
        {
            var trimmedSub = profileVolumesSubfolder.Trim();
            if (!Path.IsPathRooted(trimmedSub))
            {
                var cleanSub = trimmedSub.Replace('\\', '/').Trim('/');
                var segments = cleanSub.Split('/');
                var invalidChars = Path.GetInvalidFileNameChars();
                if (segments.Length > 0 &&
                    segments.All(seg =>
                        !string.IsNullOrWhiteSpace(seg) &&
                        seg != "." &&
                        seg != ".." &&
                        !seg.Contains(':') &&
                        seg.IndexOfAny(invalidChars) < 0))
                {
                    sanitizedSubfolder = string.Join('/', segments);
                }
            }
        }

        var subfolder = sanitizedSubfolder ?? string.Join("", (profileName ?? "default").Where(char.IsLetterOrDigit)).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(subfolder)) subfolder = "default";

        if (isRemote)
        {
            // If already an absolute Linux path (starts with /)
            if (hostPath.StartsWith("/"))
            {
                return hostPath.Replace('\\', '/');
            }

            var rootPath = server != null ? server.GetEffectiveVolumesRootPath().Replace('\\', '/').TrimEnd('/') : "/var/lib/dockermanager/volumes";
            var cleanPath = StripRelativePrefix(hostPath);

            if (cleanPath.StartsWith("volumes/", StringComparison.OrdinalIgnoreCase))
            {
                cleanPath = cleanPath["volumes/".Length..];
            }

            return string.IsNullOrEmpty(cleanPath)
                ? $"{rootPath}/{subfolder}"
                : $"{rootPath}/{subfolder}/{cleanPath}";
        }

        string fullHostPath;

        if (Path.IsPathRooted(hostPath))
        {
            fullHostPath = Path.GetFullPath(hostPath);
        }
        else
        {
            var rootPath = server != null ? server.GetEffectiveVolumesRootPath() : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DockerManager", "volumes");
            var cleanPath = StripRelativePrefix(hostPath);

            if (cleanPath.StartsWith("volumes/", StringComparison.OrdinalIgnoreCase))
            {
                cleanPath = cleanPath["volumes/".Length..];
            }

            var windowsRelPath = cleanPath.Replace('/', Path.DirectorySeparatorChar);
            fullHostPath = Path.GetFullPath(Path.Combine(rootPath, subfolder.Replace('/', Path.DirectorySeparatorChar), windowsRelPath));
        }

        try
        {
            if (!File.Exists(fullHostPath) && !Directory.Exists(fullHostPath))
            {
                if (Path.HasExtension(fullHostPath))
                {
                    var parent = Path.GetDirectoryName(fullHostPath);
                    if (!string.IsNullOrWhiteSpace(parent) && !Directory.Exists(parent))
                    {
                        Directory.CreateDirectory(parent);
                    }
                }
                else
                {
                    Directory.CreateDirectory(fullHostPath);
                }
            }
        }
        catch { }

        return fullHostPath;
    }

    private static RestartPolicyKind ParseRestartPolicy(string policy) => policy.ToLowerInvariant() switch
    {
        "always" => RestartPolicyKind.Always,
        "unless-stopped" => RestartPolicyKind.UnlessStopped,
        "on-failure" => RestartPolicyKind.OnFailure,
        _ => RestartPolicyKind.No
    };

    public async Task<string?> GetContainerStatsSummaryAsync(string containerName, CancellationToken ct = default)
    {
        try
        {
            var client = GetClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(2));

            ContainerStatsResponse? captured = null;
            int completed = 0;
            var progress = new SyncProgress<ContainerStatsResponse>(stats =>
            {
                if (Interlocked.Exchange(ref completed, 1) == 0)
                {
                    captured = stats;
                    try
                    {
                        if (!cts.IsCancellationRequested)
                        {
                            cts.Cancel();
                        }
                    }
                    catch { }
                }
            });

            try
            {
                await client.Containers.GetContainerStatsAsync(containerName, new ContainerStatsParameters { Stream = false }, progress, cts.Token);
            }
            catch (OperationCanceledException) { }
            catch { }

            if (captured == null) return null;

            double cpuPercent = 0.0;
            if (captured.CPUStats?.CPUUsage != null && captured.PreCPUStats?.CPUUsage != null &&
                captured.CPUStats.CPUUsage.TotalUsage >= captured.PreCPUStats.CPUUsage.TotalUsage &&
                captured.CPUStats.SystemUsage >= captured.PreCPUStats.SystemUsage)
            {
                var cpuDelta = (double)(captured.CPUStats.CPUUsage.TotalUsage - captured.PreCPUStats.CPUUsage.TotalUsage);
                var systemDelta = (double)(captured.CPUStats.SystemUsage - captured.PreCPUStats.SystemUsage);
                double onlineCpus = Math.Max(1.0, captured.CPUStats.OnlineCPUs > 0
                    ? (double)captured.CPUStats.OnlineCPUs
                    : (double)(captured.CPUStats.CPUUsage.PercpuUsage?.Count ?? 1));

                if (systemDelta > 0.0 && cpuDelta > 0.0)
                {
                    cpuPercent = (cpuDelta / systemDelta) * onlineCpus * 100.0;
                }
            }

            double ramMb = 0.0;
            if (captured.MemoryStats != null)
            {
                ramMb = captured.MemoryStats.Usage / (1024.0 * 1024.0);
            }

            return $"⚡ {cpuPercent:F1}%  |  💾 {ramMb:F0} MB";
        }
        catch
        {
            return null;
        }
    }

    public async Task<Stream?> GetContainerArchiveStreamAsync(string containerName, string containerPath, CancellationToken ct = default)
    {
        try
        {
            var client = GetClient();
            var response = await client.Containers.GetArchiveFromContainerAsync(
                containerName,
                new GetArchiveFromContainerParameters { Path = containerPath },
                false,
                ct);

            return response?.Stream;
        }
        catch
        {
            return null;
        }
    }

    public async Task<bool> ExtractArchiveToContainerAsync(string containerName, string containerPath, Stream tarStream, CancellationToken ct = default)
    {
        try
        {
            var client = GetClient();
            await client.Containers.ExtractArchiveToContainerAsync(
                containerName,
                new ContainerPathStatParameters { Path = containerPath },
                tarStream,
                ct);

            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<(long SpaceReclaimedBytes, string Summary)> PruneUnusedResourcesAsync(CancellationToken ct = default)
    {
        try
        {
            var client = GetClient();
            var imagesPrune = await client.Images.PruneImagesAsync(new ImagesPruneParameters(), ct);
            var containersPrune = await client.Containers.PruneContainersAsync(new ContainersPruneParameters(), ct);

            long totalReclaimed = (long)((imagesPrune.SpaceReclaimed) + (containersPrune.SpaceReclaimed));
            var imagesCount = imagesPrune.ImagesDeleted?.Count ?? 0;
            var containersCount = containersPrune.ContainersDeleted?.Count ?? 0;

            var summary = $"{imagesCount} images en {containersCount} containers opgeruimd.";
            return (totalReclaimed, summary);
        }
        catch (Exception ex)
        {
            return (0, $"Fout bij opschonen: {ex.Message}");
        }
    }

    public async Task<List<ActiveDockerPortInfo>> GetActiveDockerPortsAsync(CancellationToken ct = default)
    {
        var list = new List<ActiveDockerPortInfo>();
        try
        {
            var client = GetClient();
            var containers = await client.Containers.ListContainersAsync(new ContainersListParameters { All = true }, ct);
            foreach (var c in containers)
            {
                var name = c.Names.FirstOrDefault()?.TrimStart('/') ?? c.ID[..12];
                if (c.Ports != null)
                {
                    foreach (var p in c.Ports)
                    {
                        if (p.PublicPort > 0)
                        {
                            list.Add(new ActiveDockerPortInfo
                            {
                                ContainerId = c.ID,
                                ContainerName = name,
                                State = c.State ?? "unknown",
                                PublicPort = (int)p.PublicPort,
                                PrivatePort = (int)p.PrivatePort,
                                Protocol = string.IsNullOrWhiteSpace(p.Type) ? "tcp" : p.Type,
                                IpAddress = string.IsNullOrWhiteSpace(p.IP) ? "0.0.0.0" : p.IP
                            });
                        }
                    }
                }
            }
        }
        catch
        {
            // Ignore if docker unreachable
        }
        return list;
    }

    private static Models.ContainerState ParseState(string? state) => state?.ToLowerInvariant() switch
    {
        "running" => Models.ContainerState.Running,
        "created" => Models.ContainerState.Created,
        "restarting" => Models.ContainerState.Restarting,
        "paused" => Models.ContainerState.Paused,
        "exited" => Models.ContainerState.Exited,
        "dead" => Models.ContainerState.Dead,
        _ => Models.ContainerState.Unknown
    };
}
