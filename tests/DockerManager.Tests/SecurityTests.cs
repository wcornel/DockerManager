using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using DockerManager.App.Models;
using DockerManager.App.Services;
using Xunit;

namespace DockerManager.Tests;

public class SecurityTests
{
    [Fact]
    public void SettingsService_CloudflareTokens_EncryptedViaDpapi_NotPlaintextInSettingsJson()
    {
        using var env = new TestEnvironmentScope();

        const string globalTunnelToken = "eyJhIjoiR0xPQkFMX1RVTk5FTF9UT0tFTiJ9";
        const string serverApiToken = "cf_api_super_secret_token_123";
        const string serverTunnelToken = "eyJhIjoiU0VSVkVSX1RVTk5FTF9UT0tFTiJ9";

        var server = new DockerServerEnvironment
        {
            Id = "srv-test",
            Name = "Test VPS",
            HostType = "Tcp",
            TcpUrl = "tcp://100.64.0.10:2375",
            CloudflareDomain = "example.org",
            CloudflareAccountId = "acc-001",
            CloudflareTunnelId = "tun-001",
            CloudflareApiToken = serverApiToken,
            CloudflareTunnelToken = serverTunnelToken
        };

        env.Settings.Settings.CloudflareTunnelToken = globalTunnelToken;
        env.Settings.Settings.Servers = new List<DockerServerEnvironment> { server };
        env.Settings.Settings.ActiveServerId = server.Id;
        env.Settings.Save();

        var rawSettingsJson = File.ReadAllText(env.SettingsPath);
        Assert.DoesNotContain(globalTunnelToken, rawSettingsJson);
        Assert.DoesNotContain(serverApiToken, rawSettingsJson);
        Assert.DoesNotContain(serverTunnelToken, rawSettingsJson);

        // Reload from disk and verify hydration from DPAPI
        var reloaded = new SettingsService(env.Credentials, env.SettingsPath);
        Assert.Equal(globalTunnelToken, reloaded.Settings.CloudflareTunnelToken);
        var reloadedSrv = reloaded.Settings.Servers.Single(s => s.Id == "srv-test");
        Assert.Equal(serverApiToken, reloadedSrv.CloudflareApiToken);
        Assert.Equal(serverTunnelToken, reloadedSrv.CloudflareTunnelToken);

        // Verify legacy plaintext settings.json migration & scrubbing
        using var legacyEnv = new TestEnvironmentScope();
        const string legacyGlobalTok = "legacy_global_tunnel_secret";
        const string legacySrvApiTok = "legacy_server_api_secret";
        const string legacySrvTunTok = "legacy_server_tunnel_secret";

        var legacyJson = $$"""
        {
          "CloudflareTunnelToken": "{{legacyGlobalTok}}",
          "ActiveServerId": "legacy-vps",
          "Servers": [
            {
              "Id": "legacy-vps",
              "Name": "Legacy VPS",
              "HostType": "Tcp",
              "TcpUrl": "tcp://10.0.0.5:2375",
              "CloudflareApiToken": "{{legacySrvApiTok}}",
              "CloudflareTunnelToken": "{{legacySrvTunTok}}"
            }
          ]
        }
        """;
        File.WriteAllText(legacyEnv.SettingsPath, legacyJson);

        var migratedSettings = new SettingsService(legacyEnv.Credentials, legacyEnv.SettingsPath);
        Assert.Equal(legacyGlobalTok, migratedSettings.Settings.CloudflareTunnelToken);
        var migratedServer = migratedSettings.Settings.Servers.Single(s => s.Id == "legacy-vps");
        Assert.Equal(legacySrvApiTok, migratedServer.CloudflareApiToken);
        Assert.Equal(legacySrvTunTok, migratedServer.CloudflareTunnelToken);

        var scrubbedJson = File.ReadAllText(legacyEnv.SettingsPath);
        Assert.DoesNotContain(legacyGlobalTok, scrubbedJson);
        Assert.DoesNotContain(legacySrvApiTok, scrubbedJson);
        Assert.DoesNotContain(legacySrvTunTok, scrubbedJson);
    }

    [Fact]
    public void CredentialService_GetCredentialForImage_RejectsHostPrefixSpoofing()
    {
        using var env = new TestEnvironmentScope();
        env.Credentials.SaveRegistryCredential(new RegistryCredential
        {
            ServerAddress = "ghcr.io",
            Username = "realuser",
            Password = "ghp_real_pat_secret",
            DisplayName = "GHCR"
        });

        Assert.Null(env.Credentials.GetCredentialForImage("ghcr.io.evil.com/org/app:latest"));
        Assert.Null(env.Credentials.GetCredentialForImage("evil.com/ghcr.io/app:latest"));

        var valid = env.Credentials.GetCredentialForImage("ghcr.io/org/app:latest");
        Assert.NotNull(valid);
        Assert.Equal("ghcr.io", valid.ServerAddress);
        Assert.Equal("ghp_real_pat_secret", valid.Password);
    }

    [Fact]
    public void GitHubProfileService_SerializeProfileForSync_ScrubsEnvSecretsAndCloudflaredEqualsSyntax()
    {
        var profile = new ProfileModel
        {
            Name = "SyncSecurityTest",
            Services = new List<ServiceDefinition>
            {
                new()
                {
                    Id = "cloudflared",
                    Image = "cloudflare/cloudflared:latest",
                    Command = new List<string> { "tunnel", "--token=raw_equals_tunnel_secret" },
                    Environment = new Dictionary<string, string>
                    {
                        ["TUNNEL_TOKEN"] = "raw_env_tunnel_secret"
                    }
                },
                new()
                {
                    Id = "webapp",
                    Image = "ghcr.io/org/webapp:latest",
                    Environment = new Dictionary<string, string>
                    {
                        ["ConnectionStrings__Default"] = "Server=db;Database=app;Password=SuperSecretDbPwd;",
                        ["DB_PASSWORD"] = "db_pass_123",
                        ["API_KEY"] = "ak_live_999",
                        ["TZ"] = "Europe/Amsterdam",
                        ["ASPNETCORE_ENVIRONMENT"] = "Production",
                        ["PORT"] = "8080"
                    }
                }
            }
        };

        var syncedJson = GitHubProfileService.SerializeProfileForSync(profile);

        Assert.DoesNotContain("raw_equals_tunnel_secret", syncedJson);
        Assert.DoesNotContain("raw_env_tunnel_secret", syncedJson);
        Assert.DoesNotContain("SuperSecretDbPwd", syncedJson);
        Assert.DoesNotContain("db_pass_123", syncedJson);
        Assert.DoesNotContain("ak_live_999", syncedJson);

        Assert.Contains("--token=${CLOUDFLARE_TUNNEL_TOKEN}", syncedJson);
        Assert.Contains("\"TUNNEL_TOKEN\": \"${CLOUDFLARE_TUNNEL_TOKEN}\"", syncedJson);
        Assert.Contains("\"TZ\": \"Europe/Amsterdam\"", syncedJson);
        Assert.Contains("\"ASPNETCORE_ENVIRONMENT\": \"Production\"", syncedJson);
        Assert.Contains("\"PORT\": \"8080\"", syncedJson);

        // Original in-memory model must not be mutated
        Assert.Equal("db_pass_123", profile.Services[1].Environment["DB_PASSWORD"]);
    }

    [Fact]
    public void GitHubProfileService_PathTraversal_BlockedOnDeleteAndSubfolder()
    {
        using var env = new TestEnvironmentScope();
        var profService = new GitHubProfileService(env.Settings, env.Credentials);

        // Put settings.json inside TempDir (two levels up from profiles/local when traversing via profiles/local/../../settings.json)
        var targetSettings = Path.Combine(env.TempDir, "settings.json");
        File.WriteAllText(targetSettings, "{\"protected\":true}");
        var localSubDir = Path.Combine(env.ProfilesDir, "local");
        Directory.CreateDirectory(localSubDir);

        profService.DeleteProfileLocally("../../settings", "local");
        Assert.True(File.Exists(targetSettings), "settings.json outside profiles subfolder must never be deleted");

        Assert.Throws<ArgumentException>(() => profService.LoadProfilesLocally("../../outside"));
        Assert.Throws<ArgumentException>(() => profService.SaveProfileLocally(new ProfileModel { Name = "Test" }, "../../outside"));
        Assert.Throws<ArgumentException>(() => profService.DeleteProfileLocally("test", "../../outside"));
    }

    [Fact]
    public void ProfileModel_GetEffectiveVolumesSubfolder_RejectsTraversalAndRootedPaths()
    {
        var traversalProfile = new ProfileModel { Name = "My App!", VolumesSubfolder = "../../Windows/Temp" };
        Assert.Equal("myapp", traversalProfile.GetEffectiveVolumesSubfolder());

        var rootedWinProfile = new ProfileModel { Name = "Prod", VolumesSubfolder = @"C:\evil\path" };
        Assert.Equal("prod", rootedWinProfile.GetEffectiveVolumesSubfolder());

        var rootedUnixProfile = new ProfileModel { Name = "Prod", VolumesSubfolder = "/etc/passwd" };
        Assert.Equal("prod", rootedUnixProfile.GetEffectiveVolumesSubfolder());

        var validProfile = new ProfileModel { Name = "Prod", VolumesSubfolder = @"prod\sub" };
        Assert.Equal("prod/sub", validProfile.GetEffectiveVolumesSubfolder());
    }

    [Fact]
    public async Task BackupService_RestoreBackupAsync_BlocksZipSlipTraversal()
    {
        using var env = new TestEnvironmentScope();
        var volumeDir = Path.Combine(env.TempDir, "vol_root", "data");
        Directory.CreateDirectory(volumeDir);

        var zipPath = Path.Combine(env.TempDir, "malicious.zip");
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            static void AddEntry(ZipArchive arc, string name, string text)
            {
                var e = arc.CreateEntry(name);
                using var w = new StreamWriter(e.Open());
                w.Write(text);
            }

            AddEntry(archive, "app/data/../../escaped.txt", "evil1");
            AddEntry(archive, "app/data//rooted.txt", "evil2");
            AddEntry(archive, "../evil/x/y.txt", "evil3");
            AddEntry(archive, "app/data/ok.txt", "safe_content");
        }

        var profile = new ProfileModel
        {
            Name = "ZipTest",
            Services =
            {
                new ServiceDefinition
                {
                    Id = "app",
                    ContainerName = "app",
                    Image = "nginx:alpine",
                    Volumes =
                    {
                        new VolumeMapping { HostPath = volumeDir, ContainerPath = "/app/data" }
                    }
                }
            }
        };

        var backupService = new BackupService(env.Settings);
        var result = await backupService.RestoreBackupAsync(zipPath, profile, overwriteExisting: true);

        Assert.True(result.Success);
        Assert.Equal(1, result.RestoredFilesCount);
        Assert.True(File.Exists(Path.Combine(volumeDir, "ok.txt")));
        Assert.Equal("safe_content", File.ReadAllText(Path.Combine(volumeDir, "ok.txt")));
        Assert.False(File.Exists(Path.Combine(env.TempDir, "vol_root", "escaped.txt")));
        Assert.False(File.Exists(Path.Combine(env.TempDir, "escaped.txt")));
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public void DotnetPublisherService_GenerateDockerfile_NoPlaintextSecrets_ValidJson_NoBom()
    {
        using var env = new TestEnvironmentScope();
        var publisher = new DotnetPublisherService(env.Settings, env.Credentials);

        var projDir = Path.Combine(env.TempDir, "dotnet_proj");
        Directory.CreateDirectory(projDir);

        var dotnetReq = new PublishRequest
        {
            Framework = AppFrameworkType.Dotnet,
            SourceFolderPath = projDir,
            AppName = "MyApp",
            EntrypointDll = "My\"App.dll",
            BaseDockerImage = "mcr.microsoft.com/dotnet/aspnet:10.0",
            ContainerPort = 8080,
            SelectedParameters = new List<ScannedConfigParam>
            {
                new("ConnectionStrings:Default", "Server=prod;Password=SuperSecretConnStr;") { IsSelected = true }
            }
        };

        var dockerfile = publisher.GenerateDockerfile(dotnetReq);
        Assert.DoesNotContain("SuperSecretConnStr", dockerfile);
        Assert.Contains("# ENV ConnectionStrings__Default=\"\"", dockerfile);
        Assert.Contains("ENTRYPOINT [\"dotnet\",\"My\\u0022App.dll\"]", dockerfile);

        // Verify newline injection is rejected
        var injectedReq = new PublishRequest
        {
            Framework = AppFrameworkType.Dotnet,
            SourceFolderPath = projDir,
            AppName = "Bad\nRUN curl evil.com",
            EntrypointDll = "App.dll"
        };
        Assert.Throws<ArgumentException>(() => publisher.GenerateDockerfile(injectedReq));

        // Verify SaveDockerfileToProjectFolder writes UTF-8 WITHOUT BOM
        var saved = publisher.SaveDockerfileToProjectFolder(
            projDir,
            dockerfile,
            AppFrameworkType.Dotnet,
            "ghcr.io/user/myapp:latest",
            "ghcr.io/user/myapp:v1.0.0",
            out var err);

        Assert.True(saved, err);
        foreach (var fileName in new[] { "Dockerfile", ".dockerignore", "docker-build.bat" })
        {
            var bytes = File.ReadAllBytes(Path.Combine(projDir, fileName));
            var hasUtf8Bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            Assert.False(hasUtf8Bom, $"{fileName} must not contain a UTF-8 BOM");
        }

        // Verify cmd injection in image tag is rejected
        var badSave = publisher.SaveDockerfileToProjectFolder(
            projDir,
            dockerfile,
            AppFrameworkType.Dotnet,
            "ghcr.io/user/app:latest & calc.exe",
            "ghcr.io/user/app:v1.0.0",
            out var badErr);
        Assert.False(badSave);
        Assert.False(string.IsNullOrWhiteSpace(badErr));
    }

    [Fact]
    public void ServiceDefinition_GetEffectiveContainerName_StripsShellMetacharacters()
    {
        var svc = new ServiceDefinition
        {
            Id = "id",
            ContainerName = "app'; calc.exe; #"
        };

        var effective = svc.GetEffectiveContainerName();
        Assert.DoesNotContain(";", effective);
        Assert.DoesNotContain("'", effective);
        Assert.DoesNotContain("#", effective);
        Assert.DoesNotContain(" ", effective);
        Assert.Matches("^[a-zA-Z0-9][a-zA-Z0-9_.-]*$", effective);
    }

    [Fact]
    public void DockerCompose_PreservesHostIp_OnImportAndExport()
    {
        var importer = new DockerComposeImporterService();
        var exporter = new DockerComposeExporterService();

        var yaml = """
            services:
              postgres:
                image: postgres:16
                ports:
                  - "127.0.0.1:5432:5432"
            """;

        var imported = importer.ParseComposeYaml(yaml);
        Assert.True(imported.Success);
        var svc = Assert.Single(imported.ParsedServices);
        var port = Assert.Single(svc.Ports);
        Assert.Equal("127.0.0.1", port.HostIp);
        Assert.Equal(5432, port.HostPort);
        Assert.Equal(5432, port.ContainerPort);

        var profile = new ProfileModel
        {
            Name = "DbStack",
            Services = new List<ServiceDefinition> { svc }
        };

        var exportedYaml = exporter.ExportToComposeYaml(profile);
        Assert.Contains("127.0.0.1:5432:5432", exportedYaml);
    }
}
