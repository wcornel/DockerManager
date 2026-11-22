using System.IO;
using System.Text.RegularExpressions;
using DockerManager.App.Models;

namespace DockerManager.App.Services;

public class ScannedPythonAppInfo
{
    public bool IsValid { get; set; }
    public string? ErrorMessage { get; set; }
    public string FolderPath { get; set; } = string.Empty;
    public string SuggestedAppName { get; set; } = string.Empty;
    public string SuggestedVersionTag { get; set; } = "v1.0.0";
    public string BaseDockerImage { get; set; } = "python:3.12-slim";
    public string StartCommand { get; set; } = "python main.py";
    public int SuggestedPort { get; set; } = 8000;
    public bool HasRequirementsTxt { get; set; }
    public bool HasPyprojectToml { get; set; }
    public bool HasPipfile { get; set; }
    public List<ScannedConfigParam> Parameters { get; set; } = new();
}

public interface IPythonAppScannerService
{
    Task<ScannedPythonAppInfo> ScanPythonFolderAsync(string folderPath, CancellationToken ct = default);
}

public class PythonAppScannerService : IPythonAppScannerService
{
    private static readonly Regex EnvRegex = new(@"os\.(?:getenv|environ\.get)\(['""]([a-zA-Z0-9_]+)['""]|os\.environ\s*\[['""]([a-zA-Z0-9_]+)['""]\]", RegexOptions.Compiled);

    public async Task<ScannedPythonAppInfo> ScanPythonFolderAsync(string folderPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            return new ScannedPythonAppInfo
            {
                IsValid = false,
                ErrorMessage = "De opgegeven Python projectmap bestaat niet."
            };
        }

        var dirInfo = new DirectoryInfo(folderPath);
        var appName = SanitizeAppName(dirInfo.Name);

        var result = new ScannedPythonAppInfo
        {
            FolderPath = dirInfo.FullName,
            SuggestedAppName = appName,
            BaseDockerImage = "python:3.12-slim",
            SuggestedPort = 8000
        };

        try
        {
            var reqPath = Path.Combine(folderPath, "requirements.txt");
            var pyprojectPath = Path.Combine(folderPath, "pyproject.toml");
            var pipfilePath = Path.Combine(folderPath, "Pipfile");

            result.HasRequirementsTxt = File.Exists(reqPath);
            result.HasPyprojectToml = File.Exists(pyprojectPath);
            result.HasPipfile = File.Exists(pipfilePath);

            var depText = string.Empty;
            if (result.HasRequirementsTxt) depText += await File.ReadAllTextAsync(reqPath, ct) + "\n";
            if (result.HasPyprojectToml) depText += await File.ReadAllTextAsync(pyprojectPath, ct) + "\n";
            if (result.HasPipfile) depText += await File.ReadAllTextAsync(pipfilePath, ct) + "\n";
            bool hasUvicorn = depText.Contains("uvicorn", StringComparison.OrdinalIgnoreCase);

            // 1. Detect Entrypoint & Start Command (preserving exact case for Linux containers)
            var pyFiles = Directory.GetFiles(folderPath, "*.py", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .Where(f => f != null)
                .Select(f => f!)
                .ToList();

            var managePy = pyFiles.FirstOrDefault(f => f.Equals("manage.py", StringComparison.OrdinalIgnoreCase));
            var appPy = pyFiles.FirstOrDefault(f => f.Equals("app.py", StringComparison.OrdinalIgnoreCase));
            var mainPy = pyFiles.FirstOrDefault(f => f.Equals("main.py", StringComparison.OrdinalIgnoreCase));
            var serverPy = pyFiles.FirstOrDefault(f => f.Equals("server.py", StringComparison.OrdinalIgnoreCase));

            if (managePy != null)
            {
                result.StartCommand = $"python {managePy} runserver 0.0.0.0:8000";
                result.SuggestedPort = 8000;
            }
            else if (appPy != null)
            {
                var moduleName = Path.GetFileNameWithoutExtension(appPy);
                result.StartCommand = hasUvicorn
                    ? $"uvicorn {moduleName}:app --host 0.0.0.0 --port 8000"
                    : $"python {appPy}";
                result.SuggestedPort = 8000;
            }
            else if (mainPy != null)
            {
                var moduleName = Path.GetFileNameWithoutExtension(mainPy);
                result.StartCommand = hasUvicorn
                    ? $"uvicorn {moduleName}:app --host 0.0.0.0 --port 8000"
                    : $"python {mainPy}";
                result.SuggestedPort = 8000;
            }
            else if (serverPy != null)
            {
                result.StartCommand = $"python {serverPy}";
                result.SuggestedPort = 8000;
            }
            else if (pyFiles.Count > 0)
            {
                result.StartCommand = $"python {pyFiles[0]}";
            }
            else
            {
                result.StartCommand = "python main.py";
            }

            // Determine suggested version tag based on primary script timestamp
            var primaryScript = mainPy ?? appPy ?? managePy ?? serverPy ?? pyFiles.FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(primaryScript))
            {
                var scriptPath = Path.Combine(folderPath, primaryScript);
                if (File.Exists(scriptPath))
                {
                    var writeTime = File.GetLastWriteTime(scriptPath);
                    result.SuggestedVersionTag = $"v{writeTime:yyyy.MM.dd}";
                }
            }
            else
            {
                result.SuggestedVersionTag = $"v{DateTime.Now:yyyy.MM.dd}";
            }

            var scannedParams = new Dictionary<string, ScannedConfigParam>(StringComparer.OrdinalIgnoreCase);

            // 2. Scan .env or .env.example if present
            var envFiles = new[] { Path.Combine(folderPath, ".env"), Path.Combine(folderPath, ".env.example") };
            foreach (var envFile in envFiles)
            {
                if (File.Exists(envFile))
                {
                    var lines = await File.ReadAllLinesAsync(envFile, ct);
                    foreach (var line in lines)
                    {
                        var trimmed = line.Trim();
                        if (!string.IsNullOrWhiteSpace(trimmed) && !trimmed.StartsWith("#") && trimmed.Contains('='))
                        {
                            var idx = trimmed.IndexOf('=');
                            var key = trimmed[..idx].Trim();
                            var val = trimmed[(idx + 1)..].Trim().Trim('"', '\'');
                            if (!string.IsNullOrWhiteSpace(key) && !scannedParams.ContainsKey(key))
                            {
                                scannedParams[key] = new ScannedConfigParam(key, val) { DockerEnvKey = key };
                            }
                        }
                    }
                }
            }

            // 3. Scan *.py files safely (skipping virtualenvs/caches before descending and handling ACL errors)
            foreach (var py in EnumeratePythonFilesSafely(folderPath, ct))
            {
                try
                {
                    var code = await File.ReadAllTextAsync(py, ct);
                    var matches = EnvRegex.Matches(code);
                    foreach (Match m in matches)
                    {
                        var key = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                        if (!string.IsNullOrWhiteSpace(key) && !scannedParams.ContainsKey(key))
                        {
                            scannedParams[key] = new ScannedConfigParam(key, "") { DockerEnvKey = key };
                        }
                    }
                }
                catch { }
            }

            result.Parameters = scannedParams.Values.OrderByDescending(p => p.IsSelected).ThenBy(p => p.Category).ThenBy(p => p.Key).ToList();
            result.IsValid = true;
            return result;
        }
        catch (Exception ex)
        {
            return new ScannedPythonAppInfo
            {
                IsValid = false,
                ErrorMessage = $"Fout bij analyseren van Python map: {ex.Message}"
            };
        }
    }

    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "venv", ".venv", "env", ".env", "__pycache__", "site-packages", ".git", "node_modules", ".mypy_cache", ".pytest_cache"
    };

    private static IEnumerable<string> EnumeratePythonFilesSafely(string rootFolder, CancellationToken ct)
    {
        var stack = new Stack<string>();
        stack.Push(rootFolder);

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(dir, "*.py");
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
            {
                continue;
            }

            foreach (var f in files)
            {
                yield return f;
            }

            IEnumerable<string> subDirs;
            try
            {
                subDirs = Directory.EnumerateDirectories(dir);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
            {
                continue;
            }

            foreach (var sub in subDirs)
            {
                var name = Path.GetFileName(sub);
                if (!SkippedDirectories.Contains(name))
                {
                    stack.Push(sub);
                }
            }
        }
    }

    private static string SanitizeAppName(string raw)
    {
        var sanitized = raw.ToLowerInvariant().Replace('.', '-').Replace('_', '-');
        return string.Join("-", sanitized.Split('-', StringSplitOptions.RemoveEmptyEntries));
    }
}
