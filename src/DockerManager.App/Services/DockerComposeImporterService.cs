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
                        var parsedVol = ParseVolumeMapping(v, sourceDir);
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
                return new PortMapping { HostPort = hp, ContainerPort = cp, Protocol = protocol };
            }
        }

        return null;
    }

    private static VolumeMapping? ParseVolumeMapping(object volObj, string? sourceDirectory = null)
    {
        if (volObj == null) return null;

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
            if (parts.Length >= 4 && parts[3].Trim().Equals("ro", StringComparison.OrdinalIgnoreCase))
            {
                readOnly = true;
            }
        }
        else if (parts.Length >= 2)
        {
            host = parts[0].Trim();
            container = parts[1].Trim();
            if (parts.Length >= 3 && parts[2].Trim().Equals("ro", StringComparison.OrdinalIgnoreCase))
            {
                readOnly = true;
            }
        }
        else if (parts.Length == 1)
        {
            // Anonymous volume e.g. "/var/lib/mysql"
            return new VolumeMapping
            {
                HostPath = parts[0].Trim(),
                ContainerPath = parts[0].Trim(),
                IsNamedVolume = false
            };
        }
        else
        {
            return null;
        }

        var isNamed = !host.StartsWith(".") && !host.StartsWith("/") && !host.Contains('\\') && !host.Contains(':');

        // Resolve relative paths if source directory is known
        if (!isNamed && !string.IsNullOrWhiteSpace(sourceDirectory))
        {
            if (host.StartsWith("./") || host.StartsWith(".\\"))
            {
                try
                {
                    host = Path.GetFullPath(Path.Combine(sourceDirectory, host));
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
                        host = Path.GetFullPath(combined);
                    }
                }
                catch { }
            }
        }

        return new VolumeMapping
        {
            HostPath = host,
            ContainerPath = container,
            IsNamedVolume = isNamed,
            ReadOnly = readOnly
        };
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

    private static string FormatDisplayName(string rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName)) return "Container";
        var clean = rawName.Replace('-', ' ').Replace('_', ' ');
        var words = clean.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var capitalized = words.Select(w => char.ToUpperInvariant(w[0]) + w[1..]);
        return string.Join(" ", capitalized);
    }
}
