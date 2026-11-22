using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using DockerManager.App.Models;
using DockerManager.App.Services;
using DockerManager.App.ViewModels;
using Xunit;

namespace DockerManager.Tests;

public class Phase3Tests
{
    private sealed class MockHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;
        public List<(HttpMethod Method, string Url, string? Body, string? AuthScheme)> Requests { get; } = new();

        public MockHttpHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content != null ? await request.Content.ReadAsStringAsync(cancellationToken) : null;
            Requests.Add((request.Method, request.RequestUri?.ToString() ?? string.Empty, body, request.Headers.Authorization?.Scheme));
            return await _handler(request);
        }
    }

    private sealed class FakeArchiveDockerService : IDockerService
    {
        public Dictionary<(string Container, string Path), List<(string EntryName, string Content)>> Archives { get; } = new();
        public List<(string Container, string Path, Dictionary<string, string> ExtractedFiles)> Extracted { get; } = new();
        public bool ReturnNullArchive { get; set; }

        public Task<(bool IsConnected, string VersionOrError)> PingDockerAsync(CancellationToken ct = default) => Task.FromResult((true, "27.0"));
        public Task<DockerStartupDiagnosis> DiagnoseDockerStartupAsync(CancellationToken ct = default) => Task.FromResult(new DockerStartupDiagnosis { IsConnected = true });
        public Task<bool> TryLaunchDockerDesktopAsync() => Task.FromResult(true);
        public Task<ContainerRuntimeInfo> GetContainerInfoAsync(string containerName, CancellationToken ct = default) => Task.FromResult(new ContainerRuntimeInfo { ContainerName = containerName });
        public Task<List<ContainerRuntimeInfo>> GetAllContainersAsync(CancellationToken ct = default) => Task.FromResult(new List<ContainerRuntimeInfo>());
        public Task StartContainerAsync(ServiceDefinition service, string profileName, IProgress<string>? progress = null, ProfileRegistryConfig? registryConfig = null, string? volumesSubfolder = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task StopContainerAsync(string containerName, CancellationToken ct = default) => Task.CompletedTask;
        public Task RestartContainerAsync(ServiceDefinition service, string profileName, IProgress<string>? progress = null, ProfileRegistryConfig? registryConfig = null, string? volumesSubfolder = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task<ContainerUpdateResult> SafeUpdateContainerAsync(ServiceDefinition service, string profileName, IProgress<string>? progress = null, ProfileRegistryConfig? registryConfig = null, string? volumesSubfolder = null, CancellationToken ct = default) => Task.FromResult(new ContainerUpdateResult(false, "ok", null, null));
        public Task RemoveContainerAsync(string containerName, bool removeVolumes = false, CancellationToken ct = default) => Task.CompletedTask;
        public Task StreamLogsAsync(string containerName, Action<string> onLineReceived, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetContainerStatsSummaryAsync(string containerName, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<(long SpaceReclaimedBytes, string Summary)> PruneUnusedResourcesAsync(CancellationToken ct = default) => Task.FromResult((0L, "0 B"));
        public Task<List<ActiveDockerPortInfo>> GetActiveDockerPortsAsync(CancellationToken ct = default) => Task.FromResult(new List<ActiveDockerPortInfo>());
        public Task<ImageInspectResult> InspectAndPullImageConfigAsync(string image, bool requiresAuth = false, IProgress<string>? progress = null, CancellationToken ct = default) => Task.FromResult(new ImageInspectResult { Success = true });
        public Task<(bool Success, string Message)> TestRegistryConnectionAsync(RegistryCredential cred, CancellationToken ct = default) => Task.FromResult((true, "OK"));
        public Task<bool> CheckImageUpdateAvailableAsync(ServiceDefinition service, ProfileRegistryConfig? registryConfig = null, CancellationToken ct = default) => Task.FromResult(false);

        public async Task<Stream?> GetContainerArchiveStreamAsync(string containerName, string containerPath, CancellationToken ct = default)
        {
            if (ReturnNullArchive) return null;
            if (!Archives.TryGetValue((containerName, containerPath), out var files))
            {
                return null;
            }

            var ms = new MemoryStream();
            using (var writer = new TarWriter(ms, TarEntryFormat.Pax, leaveOpen: true))
            {
                foreach (var (name, content) in files)
                {
                    var bytes = Encoding.UTF8.GetBytes(content);
                    using var dataStream = new MemoryStream(bytes);
                    var entry = new PaxTarEntry(TarEntryType.RegularFile, name)
                    {
                        DataStream = dataStream
                    };
                    await writer.WriteEntryAsync(entry, ct);
                }
            }

            ms.Position = 0;
            return ms;
        }

        public async Task<bool> ExtractArchiveToContainerAsync(string containerName, string containerPath, Stream tarStream, CancellationToken ct = default)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using var reader = new TarReader(tarStream, leaveOpen: true);
            TarEntry? entry;
            while ((entry = await reader.GetNextEntryAsync(copyData: false, ct)) != null)
            {
                if (entry.DataStream != null)
                {
                    using var sr = new StreamReader(entry.DataStream, Encoding.UTF8, leaveOpen: true);
                    dict[entry.Name] = await sr.ReadToEndAsync(ct);
                }
            }

            Extracted.Add((containerName, containerPath, dict));
            return true;
        }
    }

    [Fact]
    public void DockerService_ResolveVolumeHostPath_PreservesDotfilesAndRespectsServerHostType()
    {
        using var env = new TestEnvironmentScope();
        var dock = new DockerService(env.Settings, env.Credentials);

        // Legacy setting says Tcp, but explicit local Pipe server must be resolved locally
        env.Settings.Settings.DockerHostType = "Tcp";
        var localPipeServer = new DockerServerEnvironment
        {
            Id = "local",
            HostType = "Pipe",
            VolumesRootPath = Path.Combine(env.TempDir, "volroot")
        };

        var envFileVol = new VolumeMapping { HostPath = "./.env", ContainerPath = "/app/.env", IsNamedVolume = false };
        var resolvedLocal = dock.ResolveVolumeHostPath(envFileVol, "MyProfile", "app", "../escape", localPipeServer);

        Assert.EndsWith($"{Path.DirectorySeparatorChar}.env", resolvedLocal);
        Assert.DoesNotContain("..", resolvedLocal);
        Assert.Contains("myprofile", resolvedLocal, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(Path.GetFullPath(localPipeServer.VolumesRootPath), resolvedLocal, StringComparison.OrdinalIgnoreCase);

        // Remote server preserves ./.env -> .env as well
        var remoteServer = new DockerServerEnvironment
        {
            Id = "vps",
            HostType = "Tcp",
            TcpUrl = "tcp://10.0.0.1:2375",
            VolumesRootPath = "/opt/vols"
        };
        var resolvedRemote = dock.ResolveVolumeHostPath(envFileVol, "Prod", "app", "prod_sub", remoteServer);
        Assert.Equal("/opt/vols/prod_sub/.env", resolvedRemote);
    }

    [Fact]
    public void DockerService_ParseImageNameAndTag_HandlesDigestsAndPorts()
    {
        Assert.Equal(("nginx", "latest"), DockerService.ParseImageNameAndTag("nginx"));
        Assert.Equal(("ghcr.io/org/app", "v1.2"), DockerService.ParseImageNameAndTag("ghcr.io/org/app:v1.2"));
        Assert.Equal(("localhost:5000/org/app", "v1"), DockerService.ParseImageNameAndTag("localhost:5000/org/app:v1"));
        Assert.Equal(("localhost:5000/org/app", "latest"), DockerService.ParseImageNameAndTag("localhost:5000/org/app"));
        Assert.Equal(("ghcr.io/org/app@sha256:abcdef", ""), DockerService.ParseImageNameAndTag("ghcr.io/org/app@sha256:abcdef"));
    }

    [Fact]
    public async Task DockerService_FetchRemoteImageDigest_SupportsBearerChallengeAndDetectsNewerDigest()
    {
        using var env = new TestEnvironmentScope();
        var (realm, serviceName, scope) = DockerService.ParseWwwAuthenticateBearer(
            "Bearer realm=\"https://ghcr.io/token\",service=\"ghcr.io\",scope=\"repository:org/app:pull\"");
        Assert.Equal("https://ghcr.io/token", realm);
        Assert.Equal("ghcr.io", serviceName);
        Assert.Equal("repository:org/app:pull", scope);

        int headAttempts = 0;
        using var handler = new MockHttpHandler(req =>
        {
            var uri = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Head && uri == "https://ghcr.io/v2/org/app/manifests/latest")
            {
                headAttempts++;
                if (req.Headers.Authorization == null)
                {
                    var unauth = new HttpResponseMessage(HttpStatusCode.Unauthorized);
                    unauth.Headers.Add("WWW-Authenticate", "Bearer realm=\"https://ghcr.io/token\",service=\"ghcr.io\",scope=\"repository:org/app:pull\"");
                    return Task.FromResult(unauth);
                }

                Assert.Equal("Bearer", req.Headers.Authorization.Scheme);
                Assert.Equal("mock_jwt", req.Headers.Authorization.Parameter);
                var ok = new HttpResponseMessage(HttpStatusCode.OK);
                ok.Headers.Add("Docker-Content-Digest", "sha256:newremote123");
                return Task.FromResult(ok);
            }

            if (req.Method == HttpMethod.Get && uri.StartsWith("https://ghcr.io/token", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"token\":\"mock_jwt\"}", Encoding.UTF8, "application/json")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        using var httpClient = new HttpClient(handler);
        using var dock = new DockerService(env.Settings, env.Credentials, httpClient);

        var digest = await dock.FetchRemoteImageDigestAsync("ghcr.io/org/app:latest");
        Assert.Equal("sha256:newremote123", digest);
        Assert.Equal(2, headAttempts);
    }

    [Fact]
    public async Task DockerService_DiagnoseRemoteServer_DoesNotReportWindowsDockerDesktop()
    {
        using var env = new TestEnvironmentScope();
        env.Settings.Settings.Servers = new List<DockerServerEnvironment>
        {
            new DockerServerEnvironment
            {
                Id = "vps",
                Name = "OracleVPS",
                HostType = "Tcp",
                TcpUrl = "tcp://127.0.0.1:59997"
            }
        };
        env.Settings.Settings.ActiveServerId = "vps";

        using var dock = new DockerService(env.Settings, env.Credentials);
        var diagnosis = await dock.DiagnoseDockerStartupAsync();

        Assert.False(diagnosis.IsConnected);
        Assert.False(diagnosis.IsDockerDesktopInstalled);
        Assert.Null(diagnosis.WindowsServiceStartMode);
        Assert.Contains("OracleVPS", diagnosis.DiagnosisSummary);
        Assert.Contains("59997", diagnosis.DiagnosisSummary);
    }

    [Fact]
    public async Task BackupService_RemoteAndNamedVolume_RoundtripsViaDockerArchiveTar_AndWarnsWhenDockerMissing()
    {
        using var env = new TestEnvironmentScope();
        var server = new DockerServerEnvironment
        {
            Id = "vps",
            Name = "VPS",
            HostType = "Tcp",
            TcpUrl = "tcp://10.0.0.1:2375",
            VolumesRootPath = "/var/lib/dockermanager/volumes"
        };
        env.Settings.Settings.Servers = new List<DockerServerEnvironment> { server };
        env.Settings.Settings.ActiveServerId = "vps";

        var profile = new ProfileModel
        {
            Name = "RemoteStack",
            Services = new List<ServiceDefinition>
            {
                new ServiceDefinition
                {
                    Id = "db",
                    ContainerName = "db",
                    Volumes = new List<VolumeMapping>
                    {
                        new VolumeMapping { HostPath = "pg_data", ContainerPath = "/var/lib/postgresql/data", IsNamedVolume = true }
                    }
                }
            }
        };

        var zipPath = Path.Combine(env.TempDir, "remote_backup.zip");

        // 1. Missing DockerService must emit a warning instead of failing silently
        var backupNoDocker = new BackupService(env.Settings, dockerService: null);
        var warnResult = await backupNoDocker.CreateBackupAsync(profile, zipPath);
        Assert.True(warnResult.Success);
        Assert.NotEmpty(warnResult.Warnings);

        // 2. Roundtrip backup & restore through FakeArchiveDockerService TAR archive
        var fakeDocker = new FakeArchiveDockerService();
        fakeDocker.Archives[("db", "/var/lib/postgresql/data")] = new List<(string, string)>
        {
            ("data/pg_hba.conf", "host all all all scram-sha-256"),
            ("data/sub/state.txt", "ready")
        };

        var backupWithDocker = new BackupService(env.Settings, fakeDocker);
        var backupResult = await backupWithDocker.CreateBackupAsync(profile, zipPath);

        Assert.True(backupResult.Success);
        Assert.Equal(2, backupResult.FilesCount);

        using (var zip = ZipFile.OpenRead(zipPath))
        {
            Assert.NotNull(zip.GetEntry("db/pg_data/pg_hba.conf"));
            Assert.NotNull(zip.GetEntry("db/pg_data/sub/state.txt"));
        }

        var restoreResult = await backupWithDocker.RestoreBackupAsync(zipPath, profile);
        Assert.True(restoreResult.Success);
        Assert.Equal(2, restoreResult.RestoredFilesCount);
        var extracted = Assert.Single(fakeDocker.Extracted);
        Assert.Equal("db", extracted.Container);
        Assert.Equal("/var/lib/postgresql/data", extracted.Path);
        Assert.Equal("host all all all scram-sha-256", extracted.ExtractedFiles["pg_hba.conf"]);
        Assert.Equal("ready", extracted.ExtractedFiles["sub/state.txt"]);
    }

    [Fact]
    public async Task BackupService_LocalJunctionReparsePoint_IsSkippedSafely()
    {
        using var env = new TestEnvironmentScope();
        var server = new DockerServerEnvironment
        {
            Id = "local",
            HostType = "Pipe",
            VolumesRootPath = Path.Combine(env.TempDir, "vols")
        };
        env.Settings.Settings.Servers = new List<DockerServerEnvironment> { server };
        env.Settings.Settings.ActiveServerId = "local";

        var volDir = Path.Combine(server.VolumesRootPath, "localstack", "app_data");
        Directory.CreateDirectory(volDir);
        File.WriteAllText(Path.Combine(volDir, "real.txt"), "real");

        var secretTarget = Path.Combine(env.TempDir, "outside_secret");
        Directory.CreateDirectory(secretTarget);
        File.WriteAllText(Path.Combine(secretTarget, "secret.txt"), "do_not_pack");

        var junctionPath = Path.Combine(volDir, "linked");
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junctionPath}\" \"{secretTarget}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false
        };
        using var p = Process.Start(psi)!;
        p.WaitForExit(5000);

        var profile = new ProfileModel
        {
            Name = "LocalStack",
            Services = new List<ServiceDefinition>
            {
                new ServiceDefinition
                {
                    Id = "app",
                    ContainerName = "app",
                    Volumes = new List<VolumeMapping>
                    {
                        new VolumeMapping { HostPath = "./volumes/app_data", ContainerPath = "/app/data", IsNamedVolume = false }
                    }
                }
            }
        };

        var zipPath = Path.Combine(env.TempDir, "junction_backup.zip");
        var backupSvc = new BackupService(env.Settings);
        var res = await backupSvc.CreateBackupAsync(profile, zipPath);

        Assert.True(res.Success);
        using var zip = ZipFile.OpenRead(zipPath);
        Assert.Contains(zip.Entries, e => e.FullName.EndsWith("real.txt", StringComparison.Ordinal));
        Assert.DoesNotContain(zip.Entries, e => e.FullName.Contains("secret.txt", StringComparison.Ordinal));
    }

    [Fact]
    public void ComposeImporterAndExporter_LongSyntaxVolumes_ReadOnlyOptions_AndRoundtripFields()
    {
        var importer = new DockerComposeImporterService();
        var yaml = """
            services:
              api:
                image: ghcr.io/org/api:latest
                network_mode: host
                working_dir: /srv/api
                labels:
                  custom.env: production
                  cloudflare.tunnel.hostname: api.example.org
                volumes:
                  - type: bind
                    source: ./cfg
                    target: /app/cfg
                    read_only: true
                  - type: volume
                    source: api_data
                    target: /data
                  - type: tmpfs
                    target: /tmp
                  - /var/cache/app
                  - ./logs:/app/logs:ro,z
            """;

        var imported = importer.ParseComposeYaml(yaml);
        Assert.True(imported.Success);
        var svc = Assert.Single(imported.ParsedServices);

        Assert.Equal("host", svc.NetworkMode);
        Assert.Equal("/srv/api", svc.WorkingDir);
        Assert.Equal("production", svc.Labels["custom.env"]);
        Assert.Equal("api.example.org", svc.CloudflareHostname);

        Assert.Equal(4, svc.Volumes.Count); // tmpfs skipped
        Assert.False(svc.Volumes[0].IsNamedVolume);
        Assert.True(svc.Volumes[0].ReadOnly);
        Assert.Equal("./cfg", svc.Volumes[0].HostPath);

        Assert.True(svc.Volumes[1].IsNamedVolume);
        Assert.Equal("api_data", svc.Volumes[1].HostPath);

        Assert.True(svc.Volumes[2].IsNamedVolume);
        Assert.Equal("/var/cache/app", svc.Volumes[2].ContainerPath);

        Assert.False(svc.Volumes[3].IsNamedVolume);
        Assert.True(svc.Volumes[3].ReadOnly);

        var exporter = new DockerComposeExporterService();
        var profile = new ProfileModel
        {
            Name = "Roundtrip",
            Services = new List<ServiceDefinition> { svc }
        };
        var exportedYaml = exporter.ExportToComposeYaml(profile);
        Assert.Contains("working_dir: /srv/api", exportedYaml);
        Assert.Contains("custom.env: production", exportedYaml);
        Assert.Contains("network_mode: host", exportedYaml);

        var reimported = importer.ParseComposeYaml(exportedYaml);
        Assert.True(reimported.Success);
        var roundSvc = Assert.Single(reimported.ParsedServices);
        Assert.Equal("host", roundSvc.NetworkMode);
        Assert.Equal("/srv/api", roundSvc.WorkingDir);
        Assert.Equal("production", roundSvc.Labels["custom.env"]);
    }

    [Fact]
    public async Task DotnetAndPythonScanners_Phase3Fixes()
    {
        using var env = new TestEnvironmentScope();

        // 1. .NET Scanner: Production override, comments/trailing commas, net8.0-windows -> 8.0
        var dotnetDir = Path.Combine(env.TempDir, "dotnet_app");
        Directory.CreateDirectory(dotnetDir);
        await File.WriteAllTextAsync(Path.Combine(dotnetDir, "MyApp.dll"), "dll");
        await File.WriteAllTextAsync(Path.Combine(dotnetDir, "MyApp.runtimeconfig.json"), """
            {
              // generated comment
              "runtimeOptions": {
                "tfm": "net8.0-windows",
              }
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(dotnetDir, "appsettings.json"), """
            {
              // dev settings
              "Api": { "Key": "dev-key", },
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(dotnetDir, "appsettings.Production.json"), """
            {
              "Api": { "Key": "prod-override" }
            }
            """);

        var dotnetScanner = new DotnetAppScannerService();
        var dotnetRes = await dotnetScanner.ScanFolderAsync(dotnetDir);
        Assert.True(dotnetRes.IsValid);
        Assert.Equal("8.0", dotnetRes.DotnetVersion);
        Assert.Equal("mcr.microsoft.com/dotnet/aspnet:8.0", dotnetRes.BaseDockerImage);
        var apiKeyParam = Assert.Single(dotnetRes.Parameters, p => p.Key == "Api:Key");
        Assert.Equal("prod-override", apiKeyParam.CurrentValue);

        // 2. Python Scanner: Case-sensitive App.py, pyproject.toml uvicorn detection, venv skipped
        var pyDir = Path.Combine(env.TempDir, "py_app");
        var venvDir = Path.Combine(pyDir, "venv", "lib");
        Directory.CreateDirectory(venvDir);
        await File.WriteAllTextAsync(Path.Combine(pyDir, "App.py"), "import os\nPORT = os.getenv('APP_PORT')\n");
        await File.WriteAllTextAsync(Path.Combine(pyDir, "pyproject.toml"), "[project]\ndependencies = [\"uvicorn\"]\n");
        await File.WriteAllTextAsync(Path.Combine(venvDir, "site.py"), "import os\nSECRET = os.getenv('SECRET_FROM_VENV')\n");

        var pyScanner = new PythonAppScannerService();
        var pyRes = await pyScanner.ScanPythonFolderAsync(pyDir);
        Assert.True(pyRes.IsValid);
        Assert.True(pyRes.HasPyprojectToml);
        Assert.Equal("uvicorn App:app --host 0.0.0.0 --port 8000", pyRes.StartCommand);
        Assert.Contains(pyRes.Parameters, p => p.Key == "APP_PORT");
        Assert.DoesNotContain(pyRes.Parameters, p => p.Key == "SECRET_FROM_VENV");
    }

    [Fact]
    public void DotnetPublisherService_GenerateDockerfile_AlpinePackagesAndHealthcheck()
    {
        using var env = new TestEnvironmentScope();
        using var pub = new DotnetPublisherService(env.Settings, env.Credentials);

        var alpineReq = new PublishRequest
        {
            Framework = AppFrameworkType.Dotnet,
            AppName = "AlpineApi",
            EntrypointDll = "AlpineApi.dll",
            BaseDockerImage = "mcr.microsoft.com/dotnet/aspnet:8.0-alpine",
            ContainerPort = 8080,
            EnableAmsterdamTimezone = true,
            HealthCheckPath = "/healthz"
        };

        var alpineDf = pub.GenerateDockerfile(alpineReq);
        Assert.Contains("RUN apk add --no-cache curl tzdata icu-libs", alpineDf);
        Assert.DoesNotContain("apt-get", alpineDf);
        Assert.Contains("HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 CMD curl -f http://localhost:8080/healthz || exit 1", alpineDf);

        var debianReq = new PublishRequest
        {
            Framework = AppFrameworkType.Dotnet,
            AppName = "DebianApi",
            EntrypointDll = "DebianApi.dll",
            BaseDockerImage = "mcr.microsoft.com/dotnet/aspnet:8.0",
            ContainerPort = 8080,
            EnableAmsterdamTimezone = true,
            HealthCheckPath = null
        };

        var debianDf = pub.GenerateDockerfile(debianReq);
        Assert.Contains("apt-get update && apt-get install -y --no-install-recommends curl tzdata", debianDf);
        Assert.Contains("# HEALTHCHECK --interval=30s", debianDf);
    }

    [Fact]
    public async Task CloudflareService_PreservesNonIngressConfig_AndUnregisterFailsOnApiError()
    {
        using var env = new TestEnvironmentScope();
        env.Credentials.SaveCloudflareApiToken("cf-token");
        var srv = env.Settings.Settings.GetActiveServer();
        srv.CloudflareAccountId = "acc_1";
        srv.CloudflareTunnelId = "tun_1";
        srv.CloudflareDomain = "example.org";

        JsonObject? capturedPutConfig = null;
        bool simulateError = false;

        using var handler = new MockHttpHandler(req =>
        {
            if (simulateError)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("{\"success\":false,\"errors\":[{\"message\":\"API Error\"}]}")
                });
            }

            var url = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Get && url.Contains("/configurations"))
            {
                var body = """
                    {
                      "success": true,
                      "result": {
                        "config": {
                          "warp-routing": { "enabled": true },
                          "originRequest": { "noTLSVerify": true },
                          "ingress": [ { "service": "http_status:404" } ]
                        }
                      }
                    }
                    """;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                });
            }

            if (req.Method == HttpMethod.Put && url.Contains("/configurations"))
            {
                var text = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                capturedPutConfig = JsonNode.Parse(text)?["config"]?.AsObject();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"success\":true}", Encoding.UTF8, "application/json")
                });
            }

            if (url.Contains("/zones"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"success\":true,\"result\":[]}", Encoding.UTF8, "application/json")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        using var http = new HttpClient(handler);
        using var cf = new CloudflareService(env.Settings, env.Credentials, http);

        var (ok, _, fullHost) = await cf.RegisterSubdomainAsync("api", 8080);
        Assert.True(ok);
        Assert.Equal("api.example.org", fullHost);
        Assert.NotNull(capturedPutConfig);
        Assert.True(capturedPutConfig!["warp-routing"]?["enabled"]?.GetValue<bool>());
        Assert.True(capturedPutConfig["originRequest"]?["noTLSVerify"]?.GetValue<bool>());

        // Error on Unregister must return Success == false
        simulateError = true;
        var (delOk, _) = await cf.UnregisterSubdomainAsync("api.example.org");
        Assert.False(delOk);

        // Empty domain must NOT call /zones?name=
        handler.Requests.Clear();
        srv.CloudflareDomain = string.Empty;
        env.Settings.Settings.CloudflareDomain = string.Empty;
        await cf.UnregisterSubdomainAsync("api.example.org", new ProfileCloudflareConfig { Domain = "" });
        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("/zones", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MainViewModel_And_ServerSettingsTab_DisposeAndNoConstructorNetworkCall()
    {
        using var env = new TestEnvironmentScope();

        int httpCalls = 0;
        using var handler = new MockHttpHandler(_ =>
        {
            httpCalls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        using var http = new HttpClient(handler);
        using var cf = new CloudflareService(env.Settings, env.Credentials, http);
        using var profSvc = new GitHubProfileService(env.Settings, env.Credentials);
        using var dockSvc = new DockerService(env.Settings, env.Credentials, http);

        var server = new DockerServerEnvironment
        {
            Id = "vps",
            Name = "VPS",
            HostType = "Tcp",
            CloudflareAccountId = "acc_1",
            CloudflareTunnelId = "tun_existing",
            CloudflareApiToken = "cf_tok"
        };

        var tabVm = new ServerSettingsTabViewModel(
            server,
            new[] { new ProfileModel { Name = "Default" } },
            env.Settings,
            env.Credentials,
            cf,
            dockSvc,
            () => Array.Empty<ProfileModel>());

        Assert.Equal(0, httpCalls);
        Assert.Equal("tun_existing", tabVm.SelectedTunnel?.Id);

        using var mainVm = new MainViewModel(env.Settings, env.Credentials, profSvc, dockSvc, cf);
        mainVm.ServiceCards.Add(new ServiceCardViewModel(new ServiceDefinition { Id = "web" }, "Default", dockSvc, env.Settings));
        mainVm.Dispose();
        mainVm.Dispose(); // idempotent
    }
}
