using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DockerManager.App.Models;

namespace DockerManager.App.Services;

public enum AppFrameworkType
{
    Dotnet,
    Python
}

public class PublishRequest
{
    public AppFrameworkType Framework { get; set; } = AppFrameworkType.Dotnet;
    public string SourceFolderPath { get; set; } = string.Empty;
    public string AppName { get; set; } = string.Empty;
    public string EntrypointDll { get; set; } = string.Empty;
    public string BaseDockerImage { get; set; } = "mcr.microsoft.com/dotnet/aspnet:8.0";
    public string StartCommand { get; set; } = "python main.py";
    public bool HasRequirementsTxt { get; set; } = true;
    public int ContainerPort { get; set; } = 8080;
    public List<ScannedConfigParam> SelectedParameters { get; set; } = new();
    public string TargetGitHubOwner { get; set; } = string.Empty;
    public string TargetGitHubRepo { get; set; } = "DotnetContainers";
    public string TargetRegistry { get; set; } = "ghcr.io";
    public string VersionTag { get; set; } = "v1.0.0";
    public string GitHubBranch { get; set; } = "main";
    public bool AddToActiveProfile { get; set; } = true;
    public bool EnablePersistentVolume { get; set; } = true;
    public bool EnableAmsterdamTimezone { get; set; } = true;
}

public class PublishResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public string ImageTag { get; set; } = string.Empty;
    public string ReleaseTag { get; set; } = string.Empty;
    public string GitHubRepoUrl { get; set; } = string.Empty;
    public string GeneratedDockerfile { get; set; } = string.Empty;
}

public interface IDotnetPublisherService
{
    string GenerateDockerfile(PublishRequest request);
    Task<PublishResult> BuildAndOptionallyPushAsync(PublishRequest request, bool pushToRegistry, IProgress<string>? progress = null, CancellationToken ct = default);
    Task<PublishResult> PublishAppToGitHubAsync(PublishRequest request, IProgress<string>? progress = null, CancellationToken ct = default);
    bool SaveDockerfileToProjectFolder(string folderPath, string dockerfileContent, AppFrameworkType framework, string fullImageTag, string releaseImageTag, out string? errorMessage);
}

public class DotnetPublisherService : IDotnetPublisherService
{
    private readonly ISettingsService _settingsService;
    private readonly ICredentialService _credentialService;
    private readonly HttpClient _httpClient;

    public DotnetPublisherService(ISettingsService settingsService, ICredentialService credentialService)
    {
        _settingsService = settingsService;
        _credentialService = credentialService;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "DockerManager-AppPublisher");
    }

    public bool SaveDockerfileToProjectFolder(string folderPath, string dockerfileContent, AppFrameworkType framework, string fullImageTag, string releaseImageTag, out string? errorMessage)
    {
        errorMessage = null;
        try
        {
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            {
                errorMessage = "Projectmap bestaat niet.";
                return false;
            }

            var dockerfilePath = Path.Combine(folderPath, "Dockerfile");
            File.WriteAllText(dockerfilePath, dockerfileContent, Encoding.UTF8);

            var dockerignorePath = Path.Combine(folderPath, ".dockerignore");
            if (!File.Exists(dockerignorePath))
            {
                var ignoreContent = framework == AppFrameworkType.Python
                    ? "__pycache__/\n*.pyc\n*.pyo\n*.pyd\n.env\n.venv\nenv/\nvenv/\n.git\n.gitignore\n.idea\n.vscode\n"
                    : "bin/\nobj/\n.git\n.gitignore\n.idea\n.vs\n*.user\n";
                File.WriteAllText(dockerignorePath, ignoreContent, Encoding.UTF8);
            }

            var batPath = Path.Combine(folderPath, "docker-build.bat");
            var batContent = $"@echo off\r\n" +
                             $"echo ===========================================\r\n" +
                             $"echo   Docker Build ^& Push: {releaseImageTag}\r\n" +
                             $"echo ===========================================\r\n\r\n" +
                             $"echo [1/3] Docker Image bouwen met dual-tags...\r\n" +
                             $"docker build -t \"{releaseImageTag}\" -t \"{fullImageTag}\" .\r\n" +
                             $"if %ERRORLEVEL% NEQ 0 (\r\n" +
                             $"    echo ❌ Fout bij docker build!\r\n" +
                             $"    pause\r\n" +
                             $"    exit /b %ERRORLEVEL%\r\n" +
                             $")\r\n\r\n" +
                             $"echo [2/3] Release tag pushen naar registry ({releaseImageTag})...\r\n" +
                             $"docker push \"{releaseImageTag}\"\r\n\r\n" +
                             $"echo [3/3] Latest tag pushen naar registry ({fullImageTag})...\r\n" +
                             $"docker push \"{fullImageTag}\"\r\n\r\n" +
                             $"echo.\r\n" +
                             $"echo 🎉 Succesvol gebouwd en gepusht!\r\n" +
                             $"pause\r\n";
            File.WriteAllText(batPath, batContent, Encoding.UTF8);

            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    public string GenerateDockerfile(PublishRequest request)
    {
        var sb = new StringBuilder();

        if (request.Framework == AppFrameworkType.Python)
        {
            sb.AppendLine($"# Dockerfile gegenereerd door DockerManager voor {request.AppName} (Python)");
            sb.AppendLine($"FROM {request.BaseDockerImage} AS runtime");
            sb.AppendLine("WORKDIR /app");
            sb.AppendLine();

            if (request.EnableAmsterdamTimezone)
            {
                sb.AppendLine("# Systeempakketten voor Nederlandse tijdzone en healthchecks");
                sb.AppendLine("RUN apt-get update && apt-get install -y --no-install-recommends curl tzdata && rm -rf /var/lib/apt/lists/*");
                sb.AppendLine();
            }

            var reqFile = Path.Combine(request.SourceFolderPath, "requirements.txt");
            if (request.HasRequirementsTxt || File.Exists(reqFile))
            {
                sb.AppendLine("# Requirements eerst kopiëren voor optimale Docker caching");
                sb.AppendLine("COPY requirements.txt .");
                sb.AppendLine("RUN pip install --no-cache-dir -r requirements.txt");
                sb.AppendLine();
            }

            sb.AppendLine("# Applicatiebestanden kopiëren");
            sb.AppendLine("COPY . .");
            sb.AppendLine();
            sb.AppendLine("ENV PYTHONUNBUFFERED=1");
            sb.AppendLine($"ENV PORT={request.ContainerPort}");
            if (request.EnableAmsterdamTimezone)
            {
                sb.AppendLine("ENV TZ=Europe/Amsterdam");
            }
            sb.AppendLine();

            if (request.SelectedParameters != null && request.SelectedParameters.Count > 0)
            {
                sb.AppendLine("# Geconfigureerde omgevingsvariabelen:");
                foreach (var p in request.SelectedParameters)
                {
                    if (p.IsSelected && !string.IsNullOrWhiteSpace(p.DockerEnvKey))
                    {
                        sb.AppendLine($"ENV {p.DockerEnvKey}=\"\"");
                    }
                }
                sb.AppendLine();
            }

            sb.AppendLine($"EXPOSE {request.ContainerPort}");

            // Format CMD as JSON array or shell command
            var cmdParts = request.StartCommand.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (cmdParts.Length > 0)
            {
                var quoted = string.Join(", ", cmdParts.Select(p => $"\"{p}\""));
                sb.AppendLine($"CMD [{quoted}]");
            }
            else
            {
                sb.AppendLine("CMD [\"python\", \"main.py\"]");
            }
        }
        else
        {
            // .NET Dockerfile
            sb.AppendLine($"# Dockerfile gegenereerd door DockerManager voor {request.AppName} (.NET)");
            sb.AppendLine($"FROM {request.BaseDockerImage} AS runtime");
            sb.AppendLine("WORKDIR /app");
            sb.AppendLine();

            if (request.EnableAmsterdamTimezone)
            {
                sb.AppendLine("# Systeempakketten voor Nederlandse tijdzone en healthchecks");
                sb.AppendLine("RUN apt-get update && apt-get install -y --no-install-recommends curl tzdata && rm -rf /var/lib/apt/lists/*");
                sb.AppendLine();
            }

            sb.AppendLine("COPY . .");
            sb.AppendLine();
            sb.AppendLine($"ENV ASPNETCORE_URLS=http://+:{request.ContainerPort}");
            sb.AppendLine("ENV ASPNETCORE_ENVIRONMENT=Production");
            sb.AppendLine("ENV ASPNETCORE_FORWARDEDHEADERS_ENABLED=true");
            sb.AppendLine("ENV DOTNET_RUNNING_IN_CONTAINER=true");
            if (request.EnableAmsterdamTimezone)
            {
                sb.AppendLine("ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false");
                sb.AppendLine("ENV TZ=Europe/Amsterdam");
            }
            sb.AppendLine();

            if (request.SelectedParameters != null && request.SelectedParameters.Count > 0)
            {
                sb.AppendLine("# Geconfigureerde omgevingsvariabelen voor appsettings.json overrides:");
                foreach (var p in request.SelectedParameters)
                {
                    if (p.IsSelected && !string.IsNullOrWhiteSpace(p.DockerEnvKey))
                    {
                        sb.AppendLine($"# ENV {p.DockerEnvKey}=\"{p.CurrentValue}\"");
                    }
                }
                sb.AppendLine();
            }

            sb.AppendLine($"EXPOSE {request.ContainerPort}");
            sb.AppendLine($"ENTRYPOINT [\"dotnet\", \"{request.EntrypointDll}\"]");
        }

        return sb.ToString();
    }

    public async Task<PublishResult> BuildAndOptionallyPushAsync(PublishRequest request, bool pushToRegistry, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var owner = string.IsNullOrWhiteSpace(request.TargetGitHubOwner) ? _settingsService.Settings.GitHubRepoOwner : request.TargetGitHubOwner;
        var appName = request.AppName.Trim().ToLowerInvariant();
        var reg = string.IsNullOrWhiteSpace(request.TargetRegistry) ? "ghcr.io" : request.TargetRegistry.Trim().ToLowerInvariant();

        var fullImageTag = string.IsNullOrWhiteSpace(owner)
            ? $"{appName}:latest"
            : (reg.Equals("docker.io", StringComparison.OrdinalIgnoreCase) ? $"{owner.ToLowerInvariant()}/{appName}:latest" : $"{reg}/{owner.ToLowerInvariant()}/{appName}:latest");

        var vTag = string.IsNullOrWhiteSpace(request.VersionTag) ? $"v{DateTime.Now:yyyy.MM.dd}" : request.VersionTag.Trim();
        if (!vTag.StartsWith("v", StringComparison.OrdinalIgnoreCase)) vTag = $"v{vTag}";

        var releaseImageTag = string.IsNullOrWhiteSpace(owner)
            ? $"{appName}:{vTag}"
            : (reg.Equals("docker.io", StringComparison.OrdinalIgnoreCase) ? $"{owner.ToLowerInvariant()}/{appName}:{vTag}" : $"{reg}/{owner.ToLowerInvariant()}/{appName}:{vTag}");

        var dockerfileContent = GenerateDockerfile(request);

        try
        {
            // 1. Save Dockerfile, .dockerignore and docker-build.bat in project folder
            progress?.Report($"[1/{(pushToRegistry ? 2 : 1)}] Dockerfile en .dockerignore gereedmaken in projectmap...");
            SaveDockerfileToProjectFolder(request.SourceFolderPath, dockerfileContent, request.Framework, fullImageTag, releaseImageTag, out var saveErr);
            if (!string.IsNullOrWhiteSpace(saveErr))
            {
                progress?.Report($"⚠️ Waarschuwing bij opslaan bestanden: {saveErr}");
            }

            var dockerCli = FindDockerCli();

            // 2. Run docker build
            progress?.Report($"[1/{(pushToRegistry ? 2 : 1)}] Docker image lokaal bouwen: '{releaseImageTag}' & latest...");
            var buildArgs = $"build -t \"{releaseImageTag}\" -t \"{fullImageTag}\" .";
            var buildResult = await RunProcessAsync(dockerCli, buildArgs, request.SourceFolderPath, progress, ct);

            if (!buildResult.Success)
            {
                return new PublishResult
                {
                    Success = false,
                    ErrorMessage = buildResult.Error,
                    GeneratedDockerfile = dockerfileContent
                };
            }

            // 3. Optional: Push to Registry
            if (pushToRegistry)
            {
                progress?.Report($"[2/2] Image uploaden naar container registry ({reg})...");

                // Check for saved credentials for this registry
                var cred = _credentialService.GetCredentialForImage(fullImageTag) ?? _credentialService.GetCredentialForImage(reg);
                if (cred != null && !string.IsNullOrWhiteSpace(cred.Password))
                {
                    var user = !string.IsNullOrWhiteSpace(cred.Username) ? cred.Username : "token";
                    await RunDockerLoginAsync(dockerCli, reg, user, cred.Password, ct);
                }

                progress?.Report($"Pushen van release tag '{releaseImageTag}'...");
                var pushRelease = await RunProcessAsync(dockerCli, $"push \"{releaseImageTag}\"", request.SourceFolderPath, progress, ct);
                if (!pushRelease.Success)
                {
                    return new PublishResult
                    {
                        Success = false,
                        ErrorMessage = $"Fout bij pushen van '{releaseImageTag}': {pushRelease.Error}",
                        ImageTag = fullImageTag,
                        ReleaseTag = releaseImageTag,
                        GeneratedDockerfile = dockerfileContent
                    };
                }

                progress?.Report($"Pushen van latest tag '{fullImageTag}'...");
                var pushLatest = await RunProcessAsync(dockerCli, $"push \"{fullImageTag}\"", request.SourceFolderPath, progress, ct);
                if (!pushLatest.Success)
                {
                    return new PublishResult
                    {
                        Success = false,
                        ErrorMessage = $"Fout bij pushen van '{fullImageTag}': {pushLatest.Error}",
                        ImageTag = fullImageTag,
                        ReleaseTag = releaseImageTag,
                        GeneratedDockerfile = dockerfileContent
                    };
                }

                progress?.Report($"✅ Image succesvol geüpload naar registry {reg}!");
            }

            return new PublishResult
            {
                Success = true,
                ImageTag = fullImageTag,
                ReleaseTag = releaseImageTag,
                GeneratedDockerfile = dockerfileContent
            };
        }
        catch (Exception ex)
        {
            return new PublishResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                GeneratedDockerfile = dockerfileContent
            };
        }
    }

    private static string FindDockerCli()
    {
        var candidates = new[]
        {
            "docker",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Docker", "Docker", "resources", "bin", "docker.exe"),
            @"C:\Program Files\Docker\Docker\resources\bin\docker.exe"
        };

        foreach (var c in candidates)
        {
            try
            {
                if (File.Exists(c)) return c;
            }
            catch { }
        }

        return "docker";
    }

    private static async Task<(bool Success, string Output, string Error)> RunProcessAsync(
        string fileName,
        string arguments,
        string workingDirectory,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = psi };

            process.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    outputBuilder.AppendLine(e.Data);
                    progress?.Report(e.Data.Trim());
                }
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    errorBuilder.AppendLine(e.Data);
                    progress?.Report(e.Data.Trim());
                }
            };

            if (!process.Start())
            {
                return (false, "", "Kon het proces niet starten.");
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync(ct);

            var success = process.ExitCode == 0;
            var err = errorBuilder.ToString().Trim();
            if (string.IsNullOrWhiteSpace(err) && !success)
            {
                err = outputBuilder.ToString().Trim();
            }

            return (success, outputBuilder.ToString(), err);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (false, "", "Docker CLI ('docker') is niet gevonden op dit systeem. Zorg ervoor dat Docker Desktop (of Rancher Desktop) geïnstalleerd is en 'docker.exe' in je PATH staat.");
        }
        catch (Exception ex)
        {
            return (false, "", ex.Message);
        }
    }

    private static async Task RunDockerLoginAsync(string dockerCli, string registry, string username, string password, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = dockerCli,
                Arguments = $"login --username \"{username}\" --password-stdin \"{registry}\"",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = psi };
            if (process.Start())
            {
                await process.StandardInput.WriteLineAsync(password.AsMemory(), ct);
                process.StandardInput.Close();
                await process.WaitForExitAsync(ct);
            }
        }
        catch { }
    }

    public async Task<PublishResult> PublishAppToGitHubAsync(PublishRequest request, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var token = _credentialService.GetGitHubToken();
        if (string.IsNullOrWhiteSpace(token))
        {
            return new PublishResult
            {
                Success = false,
                ErrorMessage = "Geen GitHub Token gevonden. Koppel eerst je GitHub Account via Instellingen."
            };
        }

        var owner = string.IsNullOrWhiteSpace(request.TargetGitHubOwner) ? _settingsService.Settings.GitHubRepoOwner : request.TargetGitHubOwner;
        var repo = string.IsNullOrWhiteSpace(request.TargetGitHubRepo) ? _settingsService.Settings.GitHubDeployRepo : request.TargetGitHubRepo;
        var branch = string.IsNullOrWhiteSpace(request.GitHubBranch) ? "main" : request.GitHubBranch;
        var appName = request.AppName.Trim().ToLowerInvariant();
        var reg = string.IsNullOrWhiteSpace(request.TargetRegistry) ? "ghcr.io" : request.TargetRegistry.Trim().ToLowerInvariant();
        var fullImageTag = reg.Equals("docker.io", StringComparison.OrdinalIgnoreCase)
            ? $"{owner.ToLowerInvariant()}/{appName}:latest"
            : $"{reg}/{owner.ToLowerInvariant()}/{appName}:latest";

        var vTag = string.IsNullOrWhiteSpace(request.VersionTag) ? $"v{DateTime.Now:yyyy.MM.dd}" : request.VersionTag.Trim();
        if (!vTag.StartsWith("v", StringComparison.OrdinalIgnoreCase)) vTag = $"v{vTag}";

        var releaseImageTag = reg.Equals("docker.io", StringComparison.OrdinalIgnoreCase)
            ? $"{owner.ToLowerInvariant()}/{appName}:{vTag}"
            : $"{reg}/{owner.ToLowerInvariant()}/{appName}:{vTag}";

        var dockerfileContent = GenerateDockerfile(request);

        try
        {
            // 1. Ensure Repository Exists
            progress?.Report($"[1/5] Controleren van GitHub repository '{owner}/{repo}'...");
            await EnsureRepositoryExistsAsync(owner, repo, token, progress, ct);

            // 2. Ensure GitHub Actions Workflow Exists
            progress?.Report($"[2/5] Controleren van GitHub Actions CI/CD workflow in repo...");
            await EnsureWorkflowFileExistsAsync(owner, repo, branch, token, progress, ct);

            // 3. Upload App Files and Dockerfile
            progress?.Report($"[3/5] Publicatiebestanden en Dockerfile uploaden naar /apps/{appName}/...");
            await UploadAppFilesAsync(owner, repo, branch, appName, request.SourceFolderPath, dockerfileContent, token, progress, ct);

            progress?.Report($"[4/5] GitHub Actions build getriggerd op GitHub!");
            progress?.Report($"[5/5] Docker image wordt gebouwd onder: {fullImageTag} (Release: {releaseImageTag})");

            return new PublishResult
            {
                Success = true,
                ImageTag = fullImageTag,
                ReleaseTag = releaseImageTag,
                GitHubRepoUrl = $"https://github.com/{owner}/{repo}/tree/{branch}/apps/{appName}",
                GeneratedDockerfile = dockerfileContent
            };
        }
        catch (Exception ex)
        {
            return new PublishResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                GeneratedDockerfile = dockerfileContent
            };
        }
    }

    private async Task EnsureRepositoryExistsAsync(string owner, string repo, string token, IProgress<string>? progress, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{owner}/{repo}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        var response = await _httpClient.SendAsync(request, ct);
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            progress?.Report($"Repository '{owner}/{repo}' bestaat nog niet. Automatisch aanmaken op GitHub...");
            var createReq = new HttpRequestMessage(HttpMethod.Post, "https://api.github.com/user/repos");
            createReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            createReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            var body = new
            {
                name = repo,
                description = "Centrale repository voor geautomatiseerde Docker container builds via DockerManager",
                @private = true,
                auto_init = true
            };
            createReq.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

            var createResp = await _httpClient.SendAsync(createReq, ct);
            if (!createResp.IsSuccessStatusCode)
            {
                var err = await createResp.Content.ReadAsStringAsync(ct);
                throw new InvalidOperationException($"Kon repository '{repo}' niet aanmaken op GitHub: {err}");
            }

            await Task.Delay(2000, ct);
        }
    }

    private async Task EnsureWorkflowFileExistsAsync(string owner, string repo, string branch, string token, IProgress<string>? progress, CancellationToken ct)
    {
        var path = ".github/workflows/docker-build.yml";
        var checkReq = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{owner}/{repo}/contents/{path}?ref={branch}");
        checkReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        checkReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        var response = await _httpClient.SendAsync(checkReq, ct);
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        progress?.Report("Centrale GitHub Actions build workflow toevoegen aan repository...");

        var workflowYaml = @"name: Build & Push Docker Container Images

on:
  push:
    branches: [ main, master ]
    paths:
      - 'apps/**'
  workflow_dispatch:

jobs:
  build-and-push:
    runs-on: ubuntu-latest
    permissions:
      contents: read
      packages: write

    steps:
      - name: Checkout repository
        uses: actions/checkout@v4

      - name: Set up QEMU
        uses: docker/setup-qemu-action@v3

      - name: Set up Docker Buildx
        uses: docker/setup-buildx-action@v3

      - name: Log in to GitHub Container Registry (ghcr.io)
        uses: docker/login-action@v3
        with:
          registry: ghcr.io
          username: ${{ github.actor }}
          password: ${{ secrets.GITHUB_TOKEN }}

      - name: Build and push modified apps
        run: |
          mkdir -p apps
          for dir in apps/*/; do
            if [ -d ""$dir"" ] && [ -f ""$dir/Dockerfile"" ]; then
              app=$(basename ""$dir"")
              echo ""📦 Gevonden app: $app in $dir""
              IMAGE_TAG=""ghcr.io/${{ github.repository_owner }}/$app:latest""
              IMAGE_TAG_LOWER=$(echo ""$IMAGE_TAG"" | tr '[:upper:]' '[:lower:]')
              echo ""🚀 Bouwen en pushen van $IMAGE_TAG_LOWER...""
              docker buildx build \
                --platform linux/amd64,linux/arm64 \
                --file ""$dir/Dockerfile"" \
                --tag ""$IMAGE_TAG_LOWER"" \
                --push \
                ""$dir""
            fi
          done
";

        await PutGitHubFileAsync(owner, repo, branch, path, workflowYaml, "Setup DockerManager central GitHub Actions workflow", token, ct);
    }

    private async Task UploadAppFilesAsync(string owner, string repo, string branch, string appName, string sourceFolder, string dockerfileContent, string token, IProgress<string>? progress, CancellationToken ct)
    {
        // 1. Upload Dockerfile
        var dockerfilePath = $"apps/{appName}/Dockerfile";
        await PutGitHubFileAsync(owner, repo, branch, dockerfilePath, dockerfileContent, $"Update Dockerfile for {appName}", token, ct);

        // 2. Upload files in project directory (excluding venv / cache)
        var allFiles = Directory.GetFiles(sourceFolder, "*.*", SearchOption.AllDirectories)
            .Where(f => !f.Contains(".venv") && !f.Contains("__pycache__") && !f.Contains(".git") && !f.Contains(".idea") && !f.EndsWith(".pyc"))
            .ToArray();

        var total = allFiles.Length;
        int count = 0;

        foreach (var filePath in allFiles)
        {
            count++;
            var relativePath = Path.GetRelativePath(sourceFolder, filePath).Replace('\\', '/');
            var targetPath = $"apps/{appName}/{relativePath}";
            var fileName = Path.GetFileName(filePath);

            progress?.Report($"Bestand uploaden ({count}/{total}): {fileName}...");

            var bytes = await File.ReadAllBytesAsync(filePath, ct);
            var base64 = Convert.ToBase64String(bytes);

            await PutGitHubBase64FileAsync(owner, repo, branch, targetPath, base64, $"Upload {fileName} for {appName}", token, ct);
        }
    }

    private async Task PutGitHubFileAsync(string owner, string repo, string branch, string path, string textContent, string commitMsg, string token, CancellationToken ct)
    {
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(textContent));
        await PutGitHubBase64FileAsync(owner, repo, branch, path, base64, commitMsg, token, ct);
    }

    private async Task PutGitHubBase64FileAsync(string owner, string repo, string branch, string path, string base64Content, string commitMsg, string token, CancellationToken ct)
    {
        string? sha = null;

        var getReq = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{owner}/{repo}/contents/{path}?ref={branch}");
        getReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        getReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        var getResp = await _httpClient.SendAsync(getReq, ct);
        if (getResp.IsSuccessStatusCode)
        {
            var content = await getResp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(content);
            if (doc.RootElement.TryGetProperty("sha", out var shaElem))
            {
                sha = shaElem.GetString();
            }
        }

        var putReq = new HttpRequestMessage(HttpMethod.Put, $"https://api.github.com/repos/{owner}/{repo}/contents/{path}");
        putReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        putReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        var payload = new Dictionary<string, object>
        {
            ["message"] = commitMsg,
            ["content"] = base64Content,
            ["branch"] = branch
        };

        if (!string.IsNullOrWhiteSpace(sha))
        {
            payload["sha"] = sha;
        }

        putReq.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        var putResp = await _httpClient.SendAsync(putReq, ct);

        if (!putResp.IsSuccessStatusCode)
        {
            var err = await putResp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Kon '{path}' niet uploaden naar GitHub: {err}");
        }
    }
}
