using System.IO;
using System.Text;
using DockerManager.App.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DockerManager.App.Services;

public class ComposeImportResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public List<ServiceDefinition> ParsedServices { get; set; } = new();
    public string SuggestedProfileName { get; set; } = string.Empty;
}

public interface IDockerComposeImporterService
{
    ComposeImportResult ParseComposeYaml(string yamlContent, string? sourceFilePath = null);
}

public class DockerComposeImporterService : IDockerComposeImporterService
{
    public ComposeImportResult ParseComposeYaml(string yamlContent, string? sourceFilePath = null)
    {
        var result = new ComposeImportResult();

        if (string.IsNullOrWhiteSpace(yamlContent))
        {
            result.Success = false;
            result.ErrorMessage = "YAML inhoud is leeg.";
            return result;
        }

        try
        {
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();

            var rawYaml = deserializer.Deserialize<Dictionary<object, object>>(yamlContent);
            if (rawYaml == null)
            {
                result.Success = false;
                result.ErrorMessage = "Kon YAML niet parsen.";
                return result;
            }

            // Determine suggested profile name from project name or folder
            if (!string.IsNullOrWhiteSpace(sourceFilePath))
            {
                var folderName = Path.GetFileName(Path.GetDirectoryName(sourceFilePath));
                if (!string.IsNullOrWhiteSpace(folderName))
                {
                    result.SuggestedProfileName = char.ToUpperInvariant(folderName[0]) + folderName[1..];
                }
            }
            if (string.IsNullOrWhiteSpace(result.SuggestedProfileName) && rawYaml.TryGetValue("name", out var prjName))
            {
                result.SuggestedProfileName = prjName?.ToString() ?? "Compose Stack";
            }
            if (string.IsNullOrWhiteSpace(result.SuggestedProfileName))
            {
                result.SuggestedProfileName = "Compose Stack";
            }

            // Find 'services' section
            Dictionary<object, object>? servicesDict = null;
            if (rawYaml.TryGetValue("services", out var sObj) && sObj is Dictionary<object, object> sMap)
            {
                servicesDict = sMap;
            }

            if (servicesDict == null || servicesDict.Count == 0)
            {
                result.Success = false;
                result.ErrorMessage = "Geen 'services' sectie gevonden in de opgegeven docker-compose YAML.";
                return result;
            }

            foreach (var (svcKey, svcVal) in servicesDict)
            {
                var serviceName = svcKey?.ToString() ?? "service";
                if (svcVal is not Dictionary<object, object> svcProps)
                    continue;

                var serviceDef = new ServiceDefinition
                {
                    Id = serviceName.ToLowerInvariant().Replace(" ", "-"),
                    DisplayName = FormatDisplayName(serviceName),
                    ContainerName = serviceName.ToLowerInvariant().Replace(" ", "-"),
                    AutoStart = false,
                    RestartPolicy = "unless-stopped"
                };

                // Image
                if (svcProps.TryGetValue("image", out var imgObj) && imgObj != null)
                {
                    serviceDef.Image = imgObj.ToString()?.Trim() ?? "unknown:latest";
                }
                else
                {
                    serviceDef.Image = $"{serviceName}:latest";
                }

                // Container Name
                if (svcProps.TryGetValue("container_name", out var cNameObj) && cNameObj != null)
                {
                    serviceDef.ContainerName = cNameObj.ToString()?.Trim().Replace(" ", "-") ?? serviceDef.ContainerName;
                }

                // Restart policy
                if (svcProps.TryGetValue("restart", out var restartObj) && restartObj != null)
                {
                    serviceDef.RestartPolicy = restartObj.ToString()?.Trim() ?? "unless-stopped";
                }

                // Network mode
                if (svcProps.TryGetValue("network_mode", out var netModeObj) && netModeObj != null)
                {
                    serviceDef.NetworkMode = netModeObj.ToString()?.Trim();
                }

                // Labels
                if (svcProps.TryGetValue("labels", out var labelsObj) && labelsObj != null)
                {
                    ParseEnvironment(labelsObj, serviceDef.Labels);
                    if (string.IsNullOrWhiteSpace(serviceDef.CloudflareHostname) &&
                        serviceDef.Labels.TryGetValue("cloudflare.tunnel.hostname", out var cfHost) &&
                        !string.IsNullOrWhiteSpace(cfHost))
                    {
                        serviceDef.CloudflareHostname = cfHost.Trim();
                    }
                }

                // Ports
                if (svcProps.TryGetValue("ports", out var portsObj) && portsObj is List<object> portsList)
                {
                    foreach (var p in portsList)
                    {
                        var parsedPort = ParsePortMapping(p);
                        if (parsedPort != null)
                        {
                            serviceDef.Ports.Add(parsedPort);
                        }
                    }
                }

                // Volumes
                var sourceDir = !string.IsNullOrWhiteSpace(sourceFilePath) ? Path.GetDirectoryName(sourceFilePath) : null;
                if (svcProps.TryGetValue("volumes", out var volsObj) && volsObj is List<object> volsList)
                {
                    foreach (var v in volsList)
                    {
                        var parsedVol = ParseVolumeMapping(v, serviceDef.GetEffectiveContainerName(), sourceDir);
                        if (parsedVol != null)
                        {
                            serviceDef.Volumes.Add(parsedVol);
                        }
                    }
                }

                // Environment
                if (svcProps.TryGetValue("environment", out var envObj))
                {
                    ParseEnvironment(envObj, serviceDef.Environment);
                }

                // Depends On
                if (svcProps.TryGetValue("depends_on", out var depObj))
                {
                    ParseDependsOn(depObj, serviceDef.DependsOn);
                }

                // Working Dir
                if (svcProps.TryGetValue("working_dir", out var wdObj) && wdObj != null)
                {
                    serviceDef.WorkingDir = wdObj.ToString()?.Trim();
                }

                // Command
                if (svcProps.TryGetValue("command", out var cmdObj) && cmdObj != null)
                {
                    if (cmdObj is List<object> cmdList)
                    {
                        serviceDef.Command = cmdList.Select(x => x?.ToString() ?? string.Empty).Where(x => !string.IsNullOrEmpty(x)).ToList();
                    }
                    else if (cmdObj is string cmdStr)
                    {
                        serviceDef.Command = Helpers.CommandLineHelper.ParseCommandLine(cmdStr);
                    }
                }

                result.ParsedServices.Add(serviceDef);
            }

            result.Success = result.ParsedServices.Count > 0;
            if (!result.Success)
            {
                result.ErrorMessage = "Geen geldige container services gevonden in de YAML.";
            }

            return result;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = $"Fout bij inlezen van YAML: {ex.Message}";
            return result;
        }
    }

    private static PortMapping? ParsePortMapping(object portObj)
    {
        if (portObj == null) return null;

        var str = portObj.ToString()?.Trim();
        if (string.IsNullOrWhiteSpace(str)) return null;

        // Strip quotes
        str = str.Trim('"', '\'');

        var protocol = "tcp";
        if (str.Contains('/'))
        {
            var parts = str.Split('/');
            str = parts[0];
            protocol = parts[1].ToLowerInvariant();
        }

        var colons = str.Split(':');
        if (colons.Length == 1)
        {
            // e.g. "80"
            if (int.TryParse(colons[0], out var p))
            {
                return new PortMapping { HostPort = p, ContainerPort = p, Protocol = protocol };
            }
        }
        else if (colons.Length == 2)
        {
            // e.g. "8080:80"
            if (int.TryParse(colons[0], out var hp) && int.TryParse(colons[1], out var cp))
            {
                return new PortMapping { HostPort = hp, ContainerPort = cp, Protocol = protocol };
            }
        }
        else if (colons.Length == 3)
        {
            // e.g. "127.0.0.1:8080:80"
            if (int.TryParse(colons[1], out var hp) && int.TryParse(colons[2], out var cp))
            {
                var hostIp = string.IsNullOrWhiteSpace(colons[0]) ? "0.0.0.0" : colons[0].Trim();
                return new PortMapping { HostIp = hostIp, HostPort = hp, ContainerPort = cp, Protocol = protocol };
            }
        }

        return null;
    }

    private static VolumeMapping? ParseVolumeMapping(object volObj, string serviceName, string? sourceDirectory = null)
    {
        if (volObj == null) return null;

        // 1. Long-syntax Compose volume object (e.g. { type: bind, source: ./data, target: /app/data, read_only: true })
        if (volObj is Dictionary<object, object> volMap)
        {
            var volType = GetMapString(volMap, "type")?.Trim().ToLowerInvariant() ?? string.Empty;
            if (volType == "tmpfs")
            {
                return null;
            }

            var target = GetMapString(volMap, "target")?.Trim();
            if (string.IsNullOrWhiteSpace(target))
            {
                return null;
            }

            var source = GetMapString(volMap, "source")?.Trim() ?? string.Empty;
            var readOnlyLong = GetMapBool(volMap, "read_only") ?? false;
            var isNamedLong = volType == "volume" || (string.IsNullOrEmpty(volType) && !LooksLikeHostPath(source));

            if (isNamedLong && string.IsNullOrWhiteSpace(source))
            {
                source = BuildAnonymousVolumeName(serviceName, target);
            }

            if (!isNamedLong && !string.IsNullOrWhiteSpace(source))
            {
                source = ResolveRelativeComposeHostPath(source, sourceDirectory);
            }

            return new VolumeMapping
            {
                HostPath = source,
                ContainerPath = target,
                IsNamedVolume = isNamedLong,
                ReadOnly = readOnlyLong
            };
        }

        // 2. Short-syntax Compose volume string
        var str = volObj.ToString()?.Trim();
        if (string.IsNullOrWhiteSpace(str)) return null;

        str = str.Trim('"', '\'');
        var parts = str.Split(':');

        string host;
        string container;
        bool readOnly = false;

        // Check if Windows path with drive letter, e.g. "C:\path:/app/data" or "C:\path:/app/data:ro"
        if (parts.Length >= 3 && parts[0].Length == 1 && char.IsLetter(parts[0][0]))
        {
            host = $"{parts[0]}:{parts[1]}".Trim();
            container = parts[2].Trim();
            if (parts.Length >= 4)
            {
                readOnly = ParseVolumeOptionsHasReadOnly(parts[3]);
            }
        }
        else if (parts.Length >= 2)
        {
            host = parts[0].Trim();
            container = parts[1].Trim();
            if (parts.Length >= 3)
            {
                readOnly = ParseVolumeOptionsHasReadOnly(parts[2]);
            }
        }
        else if (parts.Length == 1)
        {
            // Anonymous volume e.g. "/var/lib/mysql" -> represent as Docker named volume
            var targetPath = parts[0].Trim();
            return new VolumeMapping
            {
                HostPath = BuildAnonymousVolumeName(serviceName, targetPath),
                ContainerPath = targetPath,
                IsNamedVolume = true
            };
        }
        else
        {
            return null;
        }

        var isNamed = !LooksLikeHostPath(host);

        if (!isNamed)
        {
            host = ResolveRelativeComposeHostPath(host, sourceDirectory);
        }

        return new VolumeMapping
        {
            HostPath = host,
            ContainerPath = container,
            IsNamedVolume = isNamed,
            ReadOnly = readOnly
        };
    }

    private static bool ParseVolumeOptionsHasReadOnly(string? optionsPart)
    {
        if (string.IsNullOrWhiteSpace(optionsPart)) return false;
        return optionsPart
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(o => o.Equals("ro", StringComparison.OrdinalIgnoreCase));
    }

    private static bool LooksLikeHostPath(string? host) =>
        !string.IsNullOrWhiteSpace(host) &&
        (host.StartsWith('.') || host.StartsWith('/') || host.StartsWith('~') || host.Contains('\\') || host.Contains(':'));

    private static string ResolveRelativeComposeHostPath(string host, string? sourceDirectory)
    {
        if (string.IsNullOrWhiteSpace(sourceDirectory)) return host;

        if (host.StartsWith("./") || host.StartsWith(".\\"))
        {
            try
            {
                return Path.GetFullPath(Path.Combine(sourceDirectory, host));
            }
            catch { }
        }
        else if (!Path.IsPathRooted(host))
        {
            try
            {
                var combined = Path.Combine(sourceDirectory, host);
                if (Directory.Exists(combined) || File.Exists(combined))
                {
                    return Path.GetFullPath(combined);
                }
            }
            catch { }
        }

        return host;
    }

    private static string BuildAnonymousVolumeName(string serviceName, string containerPath)
    {
        var safeSvc = string.Concat((serviceName ?? "svc").Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_')).Trim('_');
        if (string.IsNullOrWhiteSpace(safeSvc)) safeSvc = "svc";

        var safePath = string.Concat((containerPath ?? "data").Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_')).Trim('_');
        if (string.IsNullOrWhiteSpace(safePath)) safePath = "data";

        return $"{safeSvc}_{safePath}";
    }

    private static string? GetMapString(Dictionary<object, object> map, string key)
    {
        foreach (var (k, v) in map)
        {
            if (string.Equals(k?.ToString(), key, StringComparison.OrdinalIgnoreCase))
            {
                return v?.ToString();
            }
        }
        return null;
    }

    private static bool? GetMapBool(Dictionary<object, object> map, string key)
    {
        var raw = GetMapString(map, key);
        if (bool.TryParse(raw, out var b)) return b;
        return null;
    }

    private static void ParseEnvironment(object envObj, Dictionary<string, string> targetDict)
    {
        if (envObj is Dictionary<object, object> envMap)
        {
            foreach (var (k, v) in envMap)
            {
                if (k != null)
                {
                    targetDict[k.ToString()!] = v?.ToString() ?? string.Empty;
                }
            }
        }
        else if (envObj is List<object> envList)
        {
            foreach (var item in envList)
            {
                var s = item?.ToString();
                if (string.IsNullOrWhiteSpace(s)) continue;

                var idx = s.IndexOf('=');
                if (idx > 0)
                {
                    var k = s[..idx].Trim();
                    var v = s[(idx + 1)..].Trim();
                    targetDict[k] = v;
                }
                else
                {
                    targetDict[s.Trim()] = string.Empty;
                }
            }
        }
    }

    private static void ParseDependsOn(object depObj, List<string> targetList)
    {
        if (depObj is List<object> depList)
        {
            foreach (var item in depList)
            {
                var s = item?.ToString()?.Trim();
                if (!string.IsNullOrWhiteSpace(s) && !targetList.Contains(s, StringComparer.OrdinalIgnoreCase))
                {
                    targetList.Add(s);
                }
            }
        }
        else if (depObj is Dictionary<object, object> depDict)
        {
            foreach (var key in depDict.Keys)
            {
                var s = key?.ToString()?.Trim();
                if (!string.IsNullOrWhiteSpace(s) && !targetList.Contains(s, StringComparer.OrdinalIgnoreCase))
                {
                    targetList.Add(s);
                }
            }
        }
    }

    private static string FormatDisplayName(string rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName)) return "Container";
        var clean = rawName.Replace('-', ' ').Replace('_', ' ');
        var words = clean.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var capitalized = words.Select(w => char.ToUpperInvariant(w[0]) + w[1..]);
        return string.Join(" ", capitalized);
    }
}

