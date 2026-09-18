using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DockerManager.App.Services;

public class ScannedConfigParam : ObservableObject
{
    private string _key = string.Empty;
    public string Key
    {
        get => _key;
        set => SetProperty(ref _key, value);
    }

    private string _dockerEnvKey = string.Empty;
    public string DockerEnvKey
    {
        get => _dockerEnvKey;
        set => SetProperty(ref _dockerEnvKey, value);
    }

    private string _currentValue = string.Empty;
    public string CurrentValue
    {
        get => _currentValue;
        set => SetProperty(ref _currentValue, value);
    }

    private string _category = "⚙️ Configuratie";
    public string Category
    {
        get => _category;
        set => SetProperty(ref _category, value);
    }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public ScannedConfigParam() { }

    public ScannedConfigParam(string key, string currentValue)
    {
        Key = key;
        CurrentValue = currentValue;
        DockerEnvKey = key.Replace(":", "__");
        CategorizeAndPreselect(key);
    }

    private void CategorizeAndPreselect(string key)
    {
        var lower = key.ToLowerInvariant();

        // 1. Determine Section-based Category matching JSON structure
        var parts = key.Split(':');
        string sectionName;

        if (parts.Length == 1)
        {
            sectionName = "Algemeen";
        }
        else if (parts.Length == 2)
        {
            // e.g. ConnectionStrings:DefaultConnection or AppSettings:IsOnPremise
            sectionName = parts[0];
        }
        else if (parts.Length == 3)
        {
            // e.g. AppSettings:Environment:Server -> Environment
            sectionName = parts[1];
        }
        else
        {
            // e.g. AppSettings:Services:AppointmentsSync:Active -> Services > AppointmentsSync
            sectionName = $"{parts[1]} > {parts[2]}";
        }

        // Attach an intuitive icon based on the section or key content
        var secLower = sectionName.ToLowerInvariant();
        string icon = "⚙️";

        if (secLower.Contains("database") || secLower.Contains("environment") || secLower.Contains("connection") || 
            secLower.Contains("sql") || secLower.Contains("data") || secLower.Contains("storage") || secLower.Contains("redis"))
        {
            icon = "🗄️";
        }
        else if (secLower.Contains("mail") || secLower.Contains("smtp") || secLower.Contains("email"))
        {
            icon = "✉️";
        }
        else if (secLower.Contains("token") || secLower.Contains("auth") || secLower.Contains("security") || secLower.Contains("jwt") || secLower.Contains("secret"))
        {
            icon = "🔑";
        }
        else if (secLower.Contains("service") || secLower.Contains("api") || secLower.Contains("kvk") || secLower.Contains("endpoint") || secLower.Contains("url") || secLower.Contains("network"))
        {
            icon = "🌐";
        }
        else if (secLower.Contains("log"))
        {
            icon = "📝";
        }

        Category = $"{icon} {sectionName}";

        // 2. Smart preselection: preselect credentials, server names, ports, URLs, database connections
        if (lower.Contains("connection") || lower.Contains("database") || lower.Contains("db") ||
            lower.Contains("postgres") || lower.Contains("sql") || lower.Contains("server") ||
            lower.Contains("secret") || lower.Contains("key") || lower.Contains("token") ||
            lower.Contains("password") || lower.Contains("jwt") || lower.Contains("auth") ||
            lower.Contains("user") || lower.Contains("username") || lower.Contains("uid") ||
            lower.Contains("url") || lower.Contains("host") || lower.Contains("port") || lower.Contains("endpoint") ||
            lower.Contains("tenantid") || lower.Contains("clientid") || lower.Contains("clientsecret"))
        {
            IsSelected = true;
        }
        else
        {
            IsSelected = false;
        }
    }
}

public class ScannedDotnetAppInfo
{
    public bool IsValid { get; set; }
    public string? ErrorMessage { get; set; }
    public string FolderPath { get; set; } = string.Empty;
    public string EntrypointDll { get; set; } = string.Empty;
    public string SuggestedAppName { get; set; } = string.Empty;
    public string SuggestedVersionTag { get; set; } = "v1.0.0";
    public string DotnetVersion { get; set; } = "8.0";
    public string BaseDockerImage { get; set; } = "mcr.microsoft.com/dotnet/aspnet:8.0";
    public int SuggestedPort { get; set; } = 8080;
    public List<ScannedConfigParam> Parameters { get; set; } = new();
}

public interface IDotnetAppScannerService
{
    Task<ScannedDotnetAppInfo> ScanFolderAsync(string folderPath, CancellationToken ct = default);
}

public class DotnetAppScannerService : IDotnetAppScannerService
{
    public async Task<ScannedDotnetAppInfo> ScanFolderAsync(string folderPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            return new ScannedDotnetAppInfo
            {
                IsValid = false,
                ErrorMessage = "De opgegeven map bestaat niet."
            };
        }

        var result = new ScannedDotnetAppInfo
        {
            FolderPath = Path.GetFullPath(folderPath)
        };

        try
        {
            // 1. Detect DLL & .NET Version via *.runtimeconfig.json
            var runtimeConfigs = Directory.GetFiles(folderPath, "*.runtimeconfig.json");
            if (runtimeConfigs.Length > 0)
            {
                var configFile = runtimeConfigs[0];
                var baseName = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(configFile));
                var dllCandidate = Path.Combine(folderPath, $"{baseName}.dll");
                if (File.Exists(dllCandidate))
                {
                    result.EntrypointDll = $"{baseName}.dll";
                    result.SuggestedAppName = SanitizeAppName(baseName);
                }

                try
                {
                    var jsonContent = await File.ReadAllTextAsync(configFile, ct);
                    using var doc = JsonDocument.Parse(jsonContent);
                    if (doc.RootElement.TryGetProperty("runtimeOptions", out var rt) &&
                        rt.TryGetProperty("tfm", out var tfmElem))
                    {
                        var tfm = tfmElem.GetString() ?? "";
                        if (tfm.StartsWith("net", StringComparison.OrdinalIgnoreCase))
                        {
                            var ver = tfm[3..];
                            if (!string.IsNullOrWhiteSpace(ver))
                            {
                                result.DotnetVersion = ver;
                                result.BaseDockerImage = $"mcr.microsoft.com/dotnet/aspnet:{ver}";
                            }
                        }
                    }
                }
                catch
                {
                    // Fallback to default .NET 8.0
                }
            }

            // 2. Fallback for Entrypoint DLL if not found via runtimeconfig
            if (string.IsNullOrWhiteSpace(result.EntrypointDll))
            {
                var dlls = Directory.GetFiles(folderPath, "*.dll")
                    .Where(d =>
                    {
                        var fn = Path.GetFileName(d);
                        return !fn.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase) &&
                               !fn.StartsWith("System.", StringComparison.OrdinalIgnoreCase) &&
                               !fn.StartsWith("Azure.", StringComparison.OrdinalIgnoreCase) &&
                               !fn.StartsWith("Docker.", StringComparison.OrdinalIgnoreCase) &&
                               !fn.StartsWith("YamlDotNet", StringComparison.OrdinalIgnoreCase) &&
                               !fn.StartsWith("CommunityToolkit.", StringComparison.OrdinalIgnoreCase);
                    })
                    .ToList();

                if (dlls.Count > 0)
                {
                    var chosen = dlls[0];
                    var baseName = Path.GetFileNameWithoutExtension(chosen);
                    result.EntrypointDll = Path.GetFileName(chosen);
                    result.SuggestedAppName = SanitizeAppName(baseName);
                }
                else
                {
                    return new ScannedDotnetAppInfo
                    {
                        IsValid = false,
                        ErrorMessage = "Geen .NET hoofdbibliotheek (*.dll) gevonden in deze map. Zorg ervoor dat je de 'publish' map selecteert."
                    };
                }
            }

            // Determine suggested version tag based on DLL timestamp
            var dllPath = Path.Combine(folderPath, result.EntrypointDll);
            if (File.Exists(dllPath))
            {
                var writeTime = File.GetLastWriteTime(dllPath);
                result.SuggestedVersionTag = $"v{writeTime:yyyy.MM.dd}";
            }
            else
            {
                result.SuggestedVersionTag = $"v{DateTime.Now:yyyy.MM.dd}";
            }

            // 3. Scan & Flatten appsettings.json / config.json
            var appsettingsFiles = new List<string>();
            var standardAppSettings = Path.Combine(folderPath, "appsettings.json");
            var prodAppSettings = Path.Combine(folderPath, "appsettings.Production.json");
            var configJson = Path.Combine(folderPath, "config.json");
            var prodConfigJson = Path.Combine(folderPath, "config.Production.json");
            var configurationJson = Path.Combine(folderPath, "configuration.json");

            if (File.Exists(standardAppSettings)) appsettingsFiles.Add(standardAppSettings);
            if (File.Exists(prodAppSettings)) appsettingsFiles.Add(prodAppSettings);
            if (File.Exists(configJson)) appsettingsFiles.Add(configJson);
            if (File.Exists(prodConfigJson)) appsettingsFiles.Add(prodConfigJson);
            if (File.Exists(configurationJson)) appsettingsFiles.Add(configurationJson);

            if (appsettingsFiles.Count == 0)
            {
                var ignoredPatterns = new[]
                {
                    ".runtimeconfig.",
                    ".deps.",
                    ".staticwebassets",
                    "compilerconfig.",
                    "libman.",
                    "bundleconfig.",
                    "package.",
                    "package-lock.",
                    "tsconfig.",
                    "views."
                };

                var anyJson = Directory.GetFiles(folderPath, "*.json")
                    .Where(f =>
                    {
                        var fn = Path.GetFileName(f);
                        if (ignoredPatterns.Any(p => fn.Contains(p, StringComparison.OrdinalIgnoreCase)))
                            return false;

                        try
                        {
                            var fi = new FileInfo(f);
                            if (fi.Length > 250_000) // Skip files > 250KB as they are almost certainly asset manifests or data dumps
                                return false;
                        }
                        catch { }

                        return true;
                    })
                    .ToList();
                appsettingsFiles.AddRange(anyJson);
            }

            var scannedKeys = new Dictionary<string, ScannedConfigParam>(StringComparer.OrdinalIgnoreCase);

            foreach (var jsonFile in appsettingsFiles)
            {
                try
                {
                    var text = await File.ReadAllTextAsync(jsonFile, ct);
                    using var doc = JsonDocument.Parse(text);
                    FlattenJsonElement(doc.RootElement, string.Empty, scannedKeys);
                }
                catch
                {
                    // Skip unparseable files
                }
            }

            result.Parameters = scannedKeys.Values.OrderBy(p => p.Category).ThenBy(p => p.Key).ToList();
            result.IsValid = true;
            return result;
        }
        catch (Exception ex)
        {
            return new ScannedDotnetAppInfo
            {
                IsValid = false,
                ErrorMessage = $"Fout bij scannen van map: {ex.Message}"
            };
        }
    }

    private static void FlattenJsonElement(JsonElement element, string currentPrefix, Dictionary<string, ScannedConfigParam> dict)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    var newPrefix = string.IsNullOrEmpty(currentPrefix) ? prop.Name : $"{currentPrefix}:{prop.Name}";
                    FlattenJsonElement(prop.Value, newPrefix, dict);
                }
                break;

            case JsonValueKind.Array:
                int index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    if (index >= 20) break;
                    var newPrefix = $"{currentPrefix}:{index}";
                    FlattenJsonElement(item, newPrefix, dict);
                    index++;
                }
                break;

            case JsonValueKind.String:
                AddOrUpdateParam(currentPrefix, element.GetString() ?? "", dict);
                break;

            case JsonValueKind.Number:
                AddOrUpdateParam(currentPrefix, element.GetRawText(), dict);
                break;

            case JsonValueKind.True:
            case JsonValueKind.False:
                AddOrUpdateParam(currentPrefix, element.GetRawText(), dict);
                break;

            case JsonValueKind.Null:
                AddOrUpdateParam(currentPrefix, "", dict);
                break;
        }
    }

    private static void AddOrUpdateParam(string key, string val, Dictionary<string, ScannedConfigParam> dict)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        if (!dict.ContainsKey(key))
        {
            dict[key] = new ScannedConfigParam(key, val);
        }
    }

    private static string SanitizeAppName(string raw)
    {
        var sanitized = raw.ToLowerInvariant().Replace('.', '-').Replace('_', '-');
        return string.Join("-", sanitized.Split('-', StringSplitOptions.RemoveEmptyEntries));
    }
}
