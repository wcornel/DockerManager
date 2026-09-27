using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using DockerManager.App;
using DockerManager.App.Models;
using DockerManager.App.Services;
using DockerManager.App.ViewModels;
using DockerManager.App.Views;
using Xunit;

namespace DockerManager.Tests;

public class DialogTests
{

    [Fact]
    public void TestAllDialogs_InstantiateWithoutErrors()
    {
        Exception? threadException = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (System.Windows.Application.Current == null)
                {
                    var app = new DockerManager.App.App();
                    app.InitializeComponent();
                }

                var cred = new CredentialService();
                var sett = new SettingsService(cred);
                var prof = new GitHubProfileService(sett, cred);
                var dock = new DockerService(sett, cred);
                var auth = new GitHubAuthService();
                var cf = new CloudflareService(sett, cred);

                // 1. SettingsDialog
                var settingsVm = new SettingsViewModel(sett, cred, prof, dock, cf);
                var settingsDlg = new SettingsDialog(settingsVm, auth, cred, sett);
                Assert.NotNull(settingsDlg);

                // 2. EditServiceDialog (Add Mode)
                var addVm = new EditServiceViewModel();
                var addDlg = new EditServiceDialog(addVm);
                Assert.NotNull(addDlg);

                // 3. EditServiceDialog (Edit Mode)
                var editVm = new EditServiceViewModel(new ServiceDefinition { Id = "test", Image = "nginx:latest" });
                var editDlg = new EditServiceDialog(editVm);
                Assert.NotNull(editDlg);

                // 4. GitHubLoginDialog
                var loginVm = new GitHubLoginViewModel(auth, cred, sett);
                var loginDlg = new GitHubLoginDialog(loginVm);
                Assert.NotNull(loginDlg);

                // 5. LogViewerDialog
                var logVm = new LogViewerViewModel("test-container", dock, new System.Collections.Generic.List<string>());
                var logDlg = new LogViewerDialog(logVm);
                Assert.NotNull(logDlg);

                // 6. ProfileSwitchDialog
                var switchDlg = new ProfileSwitchDialog("old-profile");
                Assert.NotNull(switchDlg);

                // 7. MainWindow
                var mainWin = new MainWindow();
                Assert.NotNull(mainWin);

                // 8. ImportComposeDialog
                var composeImporter = new DockerComposeImporterService();
                var importVm = new ImportComposeViewModel(composeImporter, "Test");
                importVm.YamlText = "services:\n  web:\n    image: nginx:alpine\n    ports:\n      - \"8080:80\"";
                importVm.ParseYaml();
                var importDlg = new ImportComposeDialog(importVm);
                importDlg.Show();
                importDlg.UpdateLayout();
                Assert.NotNull(importDlg);
            }
            catch (Exception ex)
            {
                threadException = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (threadException != null)
        {
            throw new Exception($"Dialog instantiation failed: {threadException}", threadException);
        }
    }

    [Fact]
    public async Task TestBackupService_CreatesValidZipArchive()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"dm_test_{Guid.NewGuid():N}");
        var volumeSourceDir = Path.Combine(tempDir, "nginx_conf");
        Directory.CreateDirectory(volumeSourceDir);
        File.WriteAllText(Path.Combine(volumeSourceDir, "nginx.conf"), "events {} http { server { listen 80; } }");
        File.WriteAllText(Path.Combine(volumeSourceDir, "default.conf"), "location / { return 200 'ok'; }");

        var destinationZip = Path.Combine(tempDir, "backup_output.zip");

        var cred = new CredentialService();
        var sett = new SettingsService(cred);
        var backupService = new BackupService(sett);

        var profile = new ProfileModel
        {
            Name = "test-profile",
            Description = "Test Profiel Omschrijving",
            Services =
            {
                new ServiceDefinition
                {
                    Id = "web",
                    ContainerName = "my-nginx",
                    Image = "nginx:alpine",
                    Volumes =
                    {
                        new VolumeMapping { HostPath = volumeSourceDir, ContainerPath = "/etc/nginx" }
                    }
                }
            }
        };

        var result = await backupService.CreateBackupAsync(profile, destinationZip);

        Assert.True(result.Success);
        Assert.True(File.Exists(destinationZip));
        Assert.Equal(2, result.FilesCount);
        Assert.True(result.TotalBytes > 0);

        // Verify ZIP contents
        using var zip = ZipFile.OpenRead(destinationZip);
        Assert.NotNull(zip.GetEntry("profile.json"));
        Assert.NotNull(zip.GetEntry("backup_manifest.txt"));
        Assert.NotNull(zip.GetEntry("my-nginx/nginx_conf/nginx.conf"));
        Assert.NotNull(zip.GetEntry("my-nginx/nginx_conf/default.conf"));

        // Cleanup
        try { Directory.Delete(tempDir, true); } catch { }
    }

    [Fact]
    public async Task TestBackupService_RestoresValidZipArchive()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"dm_restore_test_{Guid.NewGuid():N}");
        var volumeSourceDir = Path.Combine(tempDir, "nginx_conf");
        Directory.CreateDirectory(volumeSourceDir);
        var configFile = Path.Combine(volumeSourceDir, "nginx.conf");
        File.WriteAllText(configFile, "original_content");

        var destinationZip = Path.Combine(tempDir, "backup_to_restore.zip");

        var cred = new CredentialService();
        var sett = new SettingsService(cred);
        var backupService = new BackupService(sett);

        var profile = new ProfileModel
        {
            Name = "test-profile",
            Description = "Test Profiel Omschrijving",
            Services =
            {
                new ServiceDefinition
                {
                    Id = "web",
                    ContainerName = "my-nginx",
                    Image = "nginx:alpine",
                    Volumes =
                    {
                        new VolumeMapping { HostPath = volumeSourceDir, ContainerPath = "/etc/nginx" }
                    }
                }
            }
        };

        // 1. Create backup
        var backupResult = await backupService.CreateBackupAsync(profile, destinationZip);
        Assert.True(backupResult.Success);

        // 2. Modify original file to simulate corruption/loss
        File.WriteAllText(configFile, "corrupted_or_modified");

        // 3. Restore backup
        var restoreResult = await backupService.RestoreBackupAsync(destinationZip, profile, overwriteExisting: true);
        Assert.True(restoreResult.Success);
        Assert.Equal(1, restoreResult.RestoredFilesCount);

        // 4. Verify original content is restored
        var restoredContent = File.ReadAllText(configFile);
        Assert.Equal("original_content", restoredContent);

        // Cleanup
        try { Directory.Delete(tempDir, true); } catch { }
    }

    [Fact]
    public void TestLocalizationService_SwitchesLanguagesCorrectly()
    {
        var loc = LocalizationService.Instance;

        // 1. Dutch
        loc.SetLanguage("nl");
        Assert.Equal("nl", loc.CurrentLanguage);
        Assert.Equal("📁 Bestand", loc["Menu_File"]);
        Assert.Equal("▶ Alle containers starten", loc["Menu_StartAll"]);
        Assert.Equal("3 containers", loc.Get("Toolbar_ContainersCount", 3));

        // 2. English
        loc.SetLanguage("en");
        Assert.Equal("en", loc.CurrentLanguage);
        Assert.Equal("📁 File", loc["Menu_File"]);
        Assert.Equal("▶ Start All Containers", loc["Menu_StartAll"]);
        Assert.Equal("3 containers", loc.Get("Toolbar_ContainersCount", 3));

        // 3. Fallback to Dutch
        loc.SetLanguage("nl");
        Assert.Equal("📁 Bestand", loc["Menu_File"]);
    }

    [Fact]
    public void TestCredentialService_MultiRegistryCredentials()
    {
        var cred = new CredentialService();

        // 1. Save custom registries
        cred.SaveRegistryCredential(new RegistryCredential
        {
            ServerAddress = "mycorp.azurecr.io",
            Username = "azure-sp-user",
            Password = "azure-secret-token",
            DisplayName = "Azure ACR"
        });

        cred.SaveRegistryCredential(new RegistryCredential
        {
            ServerAddress = "registry.gitlab.com",
            Username = "gitlab-deploy",
            Password = "glpat-secret-token",
            DisplayName = "GitLab"
        });

        // 2. Retrieve by image name matching
        var azureMatch = cred.GetCredentialForImage("mycorp.azurecr.io/apps/orderapi:v1.2");
        Assert.NotNull(azureMatch);
        Assert.Equal("mycorp.azurecr.io", azureMatch.ServerAddress);
        Assert.Equal("azure-sp-user", azureMatch.Username);
        Assert.Equal("azure-secret-token", azureMatch.Password);

        var gitlabMatch = cred.GetCredentialForImage("registry.gitlab.com/group/repo/service:latest");
        Assert.NotNull(gitlabMatch);
        Assert.Equal("registry.gitlab.com", gitlabMatch.ServerAddress);

        // 3. Delete registry
        cred.DeleteRegistryCredential("mycorp.azurecr.io");
        var deletedMatch = cred.GetCredentialForImage("mycorp.azurecr.io/apps/orderapi:v1.2");
        Assert.Null(deletedMatch);
    }

    [Fact]
    public void TestDockerComposeImporter_ParsesComposeYamlCorrectly()
    {
        var importer = new DockerComposeImporterService();
        var yaml = @"
services:
  web:
    image: nginx:alpine
    container_name: custom_nginx
    restart: unless-stopped
    ports:
      - ""8080:80""
      - ""127.0.0.1:8443:443/tcp""
    volumes:
      - ./html:/usr/share/nginx/html
      - logs_vol:/var/log/nginx
    environment:
      NODE_ENV: production
      PORT: ""8080""

  redis:
    image: redis:7-alpine
    restart: always
    ports:
      - 6379:6379
    volumes:
      - /var/redis/data:/data
    environment:
      - ALLOW_EMPTY_PASSWORD=yes
";

        var result = importer.ParseComposeYaml(yaml);
        Assert.True(result.Success);
        Assert.Equal(2, result.ParsedServices.Count);

        var web = result.ParsedServices[0];
        Assert.Equal("web", web.Id);
        Assert.Equal("custom_nginx", web.ContainerName);
        Assert.Equal("nginx:alpine", web.Image);
        Assert.Equal("unless-stopped", web.RestartPolicy);
        Assert.Equal(2, web.Ports.Count);
        Assert.Equal(8080, web.Ports[0].HostPort);
        Assert.Equal(80, web.Ports[0].ContainerPort);
        Assert.Equal(8443, web.Ports[1].HostPort);
        Assert.Equal(443, web.Ports[1].ContainerPort);

        Assert.Equal(2, web.Volumes.Count);
        Assert.False(web.Volumes[0].IsNamedVolume);
        Assert.True(web.Volumes[1].IsNamedVolume);

        Assert.Equal("production", web.Environment["NODE_ENV"]);
        Assert.Equal("8080", web.Environment["PORT"]);

        var redis = result.ParsedServices[1];
        Assert.Equal("redis", redis.Id);
        Assert.Equal("redis:7-alpine", redis.Image);
        Assert.Equal("always", redis.RestartPolicy);
        Assert.Single(redis.Ports);
        Assert.Equal(6379, redis.Ports[0].HostPort);
        Assert.Equal(6379, redis.Ports[0].ContainerPort);
        Assert.Equal("yes", redis.Environment["ALLOW_EMPTY_PASSWORD"]);
    }

    [Fact]
    public void TestDotnetPublisher_SaveDockerfileToProjectFolder()
    {
        var cred = new CredentialService();
        var settings = new SettingsService(cred);
        var publisher = new DotnetPublisherService(settings, cred);

        var tempDir = Path.Combine(Path.GetTempPath(), "DockerManager_Publish_Test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var dockerfileContent = "FROM mcr.microsoft.com/dotnet/aspnet:8.0\nWORKDIR /app\nENTRYPOINT [\"dotnet\", \"App.dll\"]";
            var success = publisher.SaveDockerfileToProjectFolder(tempDir, dockerfileContent, AppFrameworkType.Dotnet, "ghcr.io/user/app:latest", "ghcr.io/user/app:v2026.09.01", out var err);

            Assert.True(success);
            Assert.Null(err);
            Assert.True(File.Exists(Path.Combine(tempDir, "Dockerfile")));
            Assert.True(File.Exists(Path.Combine(tempDir, ".dockerignore")));
            Assert.True(File.Exists(Path.Combine(tempDir, "docker-build.bat")));

            var savedContent = File.ReadAllText(Path.Combine(tempDir, "Dockerfile"));
            Assert.Equal(dockerfileContent, savedContent);

            var batContent = File.ReadAllText(Path.Combine(tempDir, "docker-build.bat"));
            Assert.Contains("ghcr.io/user/app:v2026.09.01", batContent);
            Assert.Contains("ghcr.io/user/app:latest", batContent);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task TestScanners_SuggestVersionTagFromTimestamp()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "DockerManager_TagTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            // 1. Test .NET Scanner
            var dllFile = Path.Combine(tempDir, "OrderApi.dll");
            File.WriteAllText(dllFile, "mock dll content");
            var fixedDate = new DateTime(2026, 9, 1, 12, 0, 0);
            File.SetLastWriteTime(dllFile, fixedDate);

            var dotnetScanner = new DotnetAppScannerService();
            var dotnetResult = await dotnetScanner.ScanFolderAsync(tempDir);

            Assert.True(dotnetResult.IsValid);
            Assert.Equal("v2026.09.01", dotnetResult.SuggestedVersionTag);

            // 2. Test Python Scanner
            var pyDir = Path.Combine(tempDir, "python_app");
            Directory.CreateDirectory(pyDir);
            var mainPy = Path.Combine(pyDir, "main.py");
            File.WriteAllText(mainPy, "print('hello')");
            File.SetLastWriteTime(mainPy, fixedDate);

            var pythonScanner = new PythonAppScannerService();
            var pyResult = await pythonScanner.ScanPythonFolderAsync(pyDir);

            Assert.True(pyResult.IsValid);
            Assert.Equal("v2026.09.01", pyResult.SuggestedVersionTag);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void TestDockerComposeExporter_ExportsValidComposeYaml()
    {
        var profile = new ProfileModel
        {
            Name = "WebStack",
            Description = "Production Web Stack",
            Services = new List<ServiceDefinition>
            {
                new ServiceDefinition
                {
                    Id = "web",
                    ContainerName = "web_app",
                    Image = "nginx:alpine",
                    RestartPolicy = "unless-stopped",
                    Ports = new List<PortMapping>
                    {
                        new PortMapping { HostPort = 8080, ContainerPort = 80, Protocol = "tcp" }
                    },
                    Volumes = new List<VolumeMapping>
                    {
                        new VolumeMapping { HostPath = "web_data", ContainerPath = "/usr/share/nginx/html", IsNamedVolume = true }
                    },
                    Environment = new Dictionary<string, string>
                    {
                        ["ENV"] = "production"
                    }
                }
            }
        };

        var exporter = new DockerComposeExporterService();
        var yaml = exporter.ExportToComposeYaml(profile);

        Assert.NotNull(yaml);
        Assert.Contains("name: webstack", yaml);
        Assert.Contains("container_name: web_app", yaml);
        Assert.Contains("image: nginx:alpine", yaml);
        Assert.Contains("8080:80", yaml);
        Assert.Contains("web_data:/usr/share/nginx/html", yaml);
        Assert.Contains("ENV: production", yaml);
        Assert.Contains("volumes:", yaml);
        Assert.Contains("web_data:", yaml);
    }

    [Fact]
    public async Task TestDotnetScanner_PrefersConfigJsonAndIgnoresStaticWebAssets()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "DockerManager_ConfigTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var dllFile = Path.Combine(tempDir, "MyApp.dll");
            File.WriteAllText(dllFile, "mock dll");

            var runtimeConfigFile = Path.Combine(tempDir, "MyApp.runtimeconfig.json");
            File.WriteAllText(runtimeConfigFile, "{\"runtimeOptions\":{\"tfm\":\"net8.0\"}}");

            var configJson = Path.Combine(tempDir, "config.json");
            File.WriteAllText(configJson, "{\"Database\":{\"Host\":\"localhost\",\"Password\":\"secret123\"},\"Port\":5000}");

            // Create a fake staticwebassets endpoints json that should be ignored
            var staticWebAssets = Path.Combine(tempDir, "MyApp.staticwebassets.endpoints.json");
            File.WriteAllText(staticWebAssets, "{\"Endpoints\":[{\"Route\":\"test.css\",\"AssetFile\":\"test.css\"}]}");

            var scanner = new DotnetAppScannerService();
            var result = await scanner.ScanFolderAsync(tempDir);

            Assert.True(result.IsValid);
            Assert.Equal("MyApp.dll", result.EntrypointDll);
            Assert.Equal("8.0", result.DotnetVersion);

            // Should contain Database:Host, Database:Password, Port
            Assert.Contains(result.Parameters, p => p.Key == "Database:Host");
            Assert.Contains(result.Parameters, p => p.Key == "Database:Password");
            Assert.Contains(result.Parameters, p => p.Key == "Port");

            // Should NOT contain anything from staticwebassets
            Assert.DoesNotContain(result.Parameters, p => p.Key.Contains("Endpoints"));
            Assert.Equal(3, result.Parameters.Count);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task TestDotnetScanner_DeeplyNestedConfig_ScannedCorrectly()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "DockerManager_DeepConfigTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var dllFile = Path.Combine(tempDir, "SampleApp.Server.dll");
            File.WriteAllText(dllFile, "mock dll");

            var runtimeConfigFile = Path.Combine(tempDir, "SampleApp.Server.runtimeconfig.json");
            File.WriteAllText(runtimeConfigFile, "{\"runtimeOptions\":{\"tfm\":\"net8.0\"}}");

            var configJson = Path.Combine(tempDir, "config.json");
            File.WriteAllText(configJson, "{\"AppSettings\":{\"Environment\":{\"Server\":\"127.0.0.1\"},\"Services\":{\"Sync\":{\"Active\":true}}},\"Port\":8080}");

            var scanner = new DotnetAppScannerService();
            var result = await scanner.ScanFolderAsync(tempDir);

            Assert.True(result.IsValid);
            Assert.Equal("SampleApp.Server.dll", result.EntrypointDll);
            Assert.Contains(result.Parameters, p => p.Key.StartsWith("AppSettings:"));
            Assert.Contains(result.Parameters, p => p.Key == "AppSettings:Environment:Server");
            Assert.Contains(result.Parameters, p => p.Key == "AppSettings:Services:Sync:Active");
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void TestCloudflareService_EnsureCloudflaredServiceInProfile()
    {
        var cred = new CredentialService();
        var sett = new SettingsService(cred);
        sett.Settings.CloudflareTunnelToken = "eyJhIjoiMTIzNDU2In0=";

        var cf = new CloudflareService(sett, cred);
        var profile = new ProfileModel
        {
            Name = "TestProfile"
        };

        cf.EnsureCloudflaredServiceInProfile(profile);

        Assert.Single(profile.Services);
        var svc = profile.Services[0];
        Assert.Equal("cloudflared", svc.Id);
        Assert.Equal("cloudflare/cloudflared:latest", svc.Image);
        Assert.Equal("host", svc.NetworkMode);
        Assert.NotNull(svc.Command);
        Assert.Contains("tunnel", svc.Command);
        Assert.Contains("eyJhIjoiMTIzNDU2In0=", svc.Command);

        // Calling again should not duplicate
        cf.EnsureCloudflaredServiceInProfile(profile);
        Assert.Single(profile.Services);
    }

    [Fact]
    public void TestEditServiceViewModel_CloudflareOverride()
    {
        var cred = new CredentialService();
        var sett = new SettingsService(cred);
        sett.Settings.CloudflareDomain = "example.com";
        sett.Settings.CloudflareAccountId = "acc123";
        sett.Settings.CloudflareTunnelId = "tun123";

        var cf = new CloudflareService(sett, cred);

        var service = new ServiceDefinition
        {
            Id = "demo-app",
            DisplayName = "Demo App",
            Image = "demo-app:latest",
            CloudflareHostname = "old-app.example.com",
            Ports = new List<PortMapping>
            {
                new PortMapping { HostPort = 8004, ContainerPort = 8080, Protocol = "tcp" }
            }
        };

        var editVm = new EditServiceViewModel(service, null, cf, sett);

        Assert.True(editVm.EnableCloudflareTunnel);
        Assert.Equal("old-app", editVm.CloudflareSubdomain);
        Assert.Equal("example.com", editVm.CloudflareDomain);
        Assert.Equal(8004, editVm.SelectedCloudflarePort);

        // Override with a new subdomain
        editVm.CloudflareSubdomain = "new-demo-app";
        Assert.Equal("https://new-demo-app.example.com", editVm.FullCloudflareUrl);

        var updated = editVm.ToServiceDefinition();
        Assert.Equal("new-demo-app.example.com", updated.CloudflareHostname);
        Assert.Equal("new-demo-app.example.com", updated.Labels["cloudflare.tunnel.hostname"]);
        Assert.Equal("true", updated.Environment["ASPNETCORE_FORWARDEDHEADERS_ENABLED"]);
    }

    [Fact]
    public void TestServiceCardViewModel_CloudflareUrl()
    {
        var cred = new CredentialService();
        var sett = new SettingsService(cred);
        sett.Settings.CloudflareDomain = "example.com";
        var dock = new DockerService(sett, cred);

        var service = new ServiceDefinition
        {
            Id = "demo-app",
            DisplayName = "Demo App",
            Image = "demo-app:latest",
            CloudflareHostname = "demo-app.example.com"
        };

        var cardVm = new ServiceCardViewModel(service, "TestProfile", dock, sett);

        Assert.True(cardVm.HasCloudflareUrl);
        Assert.Equal("https://demo-app.example.com", cardVm.CloudflareUrl);

        // Edge case 1: Hostname stored with http:// scheme -> must upgrade to https://
        service.CloudflareHostname = "http://demo-app.example.com";
        Assert.Equal("https://demo-app.example.com", cardVm.CloudflareUrl);

        // Edge case 2: Only subdomain stored without domain -> must append domain from settings and use https://
        service.CloudflareHostname = "demo-app";
        Assert.Equal("https://demo-app.example.com", cardVm.CloudflareUrl);

        // Edge case 3: Label fallback with http:// -> must upgrade to https://
        service.CloudflareHostname = null;
        service.Labels["cloudflare.tunnel.hostname"] = "http://label-app.example.com";
        Assert.Equal("https://label-app.example.com", cardVm.CloudflareUrl);
    }

    [Fact]
    public void TestDockerComposeImporter_SampleComposeYaml_ParsesAndResolvesCorrectly()
    {
        var importer = new DockerComposeImporterService();
        var composePath = @"C:\Projects\SampleApp\docker-compose.yml";
        var yaml = """
            services:
              webapp:
                image: ghcr.io/sampleorg/webapp:latest
                container_name: webapp-service
                restart: unless-stopped
                ports:
                  - "8080:8080"
                volumes:
                  - ./connectionsettings.json:/app/connectionsettings.json:rw
                  - ./appsettings.local.json:/app/appsettings.local.json:rw
                  - ./DataProtection-Keys:/app/DataProtection-Keys:rw
                  - ./SignedDocuments:/app/SignedDocuments:rw
                  - ./Logs:/app/Logs:rw
                environment:
                  - ASPNETCORE_ENVIRONMENT=Production
                  - ASPNETCORE_URLS=http://+:8080
            """;

        var result = importer.ParseComposeYaml(yaml, composePath);

        Assert.True(result.Success);
        Assert.Single(result.ParsedServices);
        Assert.Equal("SampleApp", result.SuggestedProfileName);

        var svc = result.ParsedServices[0];
        Assert.Equal("webapp", svc.Id);
        Assert.Equal("webapp-service", svc.ContainerName);
        Assert.Equal("Webapp", svc.DisplayName);
        Assert.Equal("ghcr.io/sampleorg/webapp:latest", svc.Image);
        Assert.Single(svc.Ports);
        Assert.Equal(8080, svc.Ports[0].HostPort);
        Assert.Equal(8080, svc.Ports[0].ContainerPort);

        // Verify volume resolution
        Assert.Equal(5, svc.Volumes.Count);
        var settingsVol = svc.Volumes.Find(v => v.ContainerPath == "/app/connectionsettings.json");
        Assert.NotNull(settingsVol);
        Assert.Equal(@"C:\Projects\SampleApp\connectionsettings.json", settingsVol.HostPath);
        Assert.False(settingsVol.ReadOnly);

        var signedDocsVol = svc.Volumes.Find(v => v.ContainerPath == "/app/SignedDocuments");
        Assert.NotNull(signedDocsVol);
        Assert.Equal(@"C:\Projects\SampleApp\SignedDocuments", signedDocsVol.HostPath);

        // Verify environment
        Assert.Equal("Production", svc.Environment["ASPNETCORE_ENVIRONMENT"]);
        Assert.Equal("http://+:8080", svc.Environment["ASPNETCORE_URLS"]);
    }

    [Fact]
    public async Task TestCloudflareService_UnregisterSubdomain_WithMockHttp()
    {
        var cred = new CredentialService();
        cred.SaveCloudflareApiToken("test-token");
        var sett = new SettingsService(cred);
        sett.Settings.CloudflareDomain = "example.com";
        sett.Settings.CloudflareAccountId = "acc123";
        sett.Settings.CloudflareTunnelId = "tun123";

        var mockHandler = new MockCloudflareHttpHandler();
        var httpClient = new System.Net.Http.HttpClient(mockHandler);
        var cf = new CloudflareService(sett, cred, httpClient);

        var (ok, msg) = await cf.UnregisterSubdomainAsync("demo-app.example.com");

        Assert.True(ok);
        Assert.Contains("succesvol opgeruimd", msg);
        Assert.Contains("tunnel routing", msg);
        Assert.Contains("DNS CNAME", msg);

        // Verify that DELETE calls and PUT calls were made
        Assert.Contains(mockHandler.RequestedUrls, u => u.StartsWith("PUT") && u.Contains("/cfd_tunnel/tun123/configurations"));
        Assert.Contains(mockHandler.RequestedUrls, u => u.StartsWith("DELETE") && u.Contains("/dns_records/rec123"));
    }

    [Fact]
    public async Task TestEditServiceViewModel_UncheckCloudflare_TriggersConfirmationAndUnregister()
    {
        var cred = new CredentialService();
        cred.SaveCloudflareApiToken("test-token");
        var sett = new SettingsService(cred);
        sett.Settings.CloudflareDomain = "example.com";
        sett.Settings.CloudflareAccountId = "acc123";
        sett.Settings.CloudflareTunnelId = "tun123";

        var mockHandler = new MockCloudflareHttpHandler();
        var httpClient = new System.Net.Http.HttpClient(mockHandler);
        var cf = new CloudflareService(sett, cred, httpClient);

        var service = new ServiceDefinition
        {
            Id = "demo-app",
            DisplayName = "Demo App",
            Image = "demo-app:latest",
            CloudflareHostname = "demo-app.example.com",
            Ports = new List<PortMapping>
            {
                new PortMapping { HostPort = 8004, ContainerPort = 8080, Protocol = "tcp" }
            }
        };

        var editVm = new EditServiceViewModel(service, null, cf, sett);
        Assert.True(editVm.EnableCloudflareTunnel);

        // Track if confirmation prompt was called
        bool promptWasCalled = false;
        editVm.ConfirmPrompt = (msg, title) =>
        {
            promptWasCalled = true;
            return true; // Simulate clicking 'Yes'
        };

        // Uncheck Cloudflare
        editVm.EnableCloudflareTunnel = false;

        // Save
        await editVm.SaveCommand.ExecuteAsync(null);

        Assert.True(promptWasCalled, "ConfirmPrompt should have been invoked when disabling Cloudflare");
        Assert.Contains(mockHandler.RequestedUrls, u => u.StartsWith("DELETE") && u.Contains("/dns_records/rec123"));
    }

    private class MockCloudflareHttpHandler : System.Net.Http.HttpMessageHandler
    {
        public List<string> RequestedUrls { get; } = new();

        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.ToString() ?? "";
            RequestedUrls.Add($"{request.Method} {url}");

            if (url.Contains("/cfd_tunnel/tun123/configurations") && request.Method == System.Net.Http.HttpMethod.Get)
            {
                var content = """
                {
                    "success": true,
                    "result": {
                        "config": {
                            "ingress": [
                                { "hostname": "demo-app.example.com", "service": "http://localhost:8004" },
                                { "service": "http_status:404" }
                            ]
                        }
                    }
                }
                """;
                return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new System.Net.Http.StringContent(content, System.Text.Encoding.UTF8, "application/json")
                });
            }

            if (url.Contains("/cfd_tunnel/tun123/configurations") && request.Method == System.Net.Http.HttpMethod.Put)
            {
                return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new System.Net.Http.StringContent("""{"success": true}""", System.Text.Encoding.UTF8, "application/json")
                });
            }

            if (url.Contains("/zones?name=example.com"))
            {
                var content = """
                {
                    "success": true,
                    "result": [ { "id": "zone123", "name": "example.com" } ]
                }
                """;
                return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new System.Net.Http.StringContent(content, System.Text.Encoding.UTF8, "application/json")
                });
            }

            if (url.Contains("/dns_records?type=CNAME&name=demo-app.example.com") && request.Method == System.Net.Http.HttpMethod.Get)
            {
                var content = """
                {
                    "success": true,
                    "result": [ { "id": "rec123", "name": "demo-app.example.com", "type": "CNAME" } ]
                }
                """;
                return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new System.Net.Http.StringContent(content, System.Text.Encoding.UTF8, "application/json")
                });
            }

            if (url.Contains("/dns_records/rec123") && request.Method == System.Net.Http.HttpMethod.Delete)
            {
                return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new System.Net.Http.StringContent("""{"success": true}""", System.Text.Encoding.UTF8, "application/json")
                });
            }

            return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new System.Net.Http.StringContent("""{"success": true, "result": []}""", System.Text.Encoding.UTF8, "application/json")
            });
        }
    }

    [Fact]
    public void TestDockerService_ResolveVolumeHostPath_LocalAndRemote()
    {
        var cred = new CredentialService();
        var sett = new SettingsService(cred);
        var dockerService = new DockerService(sett, cred);

        // 1. Named volume always stays unchanged
        var namedVol = new VolumeMapping { HostPath = "kennisbase_data", IsNamedVolume = true };
        Assert.Equal("kennisbase_data", dockerService.ResolveVolumeHostPath(namedVol, "Oracle", "kennisbase"));

        // 2. Remote TCP mode (Linux paths)
        sett.Settings.DockerHostType = "Tcp";
        sett.Settings.DockerTcpUrl = "tcp://100.80.90.100:2375";

        var relVol = new VolumeMapping { HostPath = "./volumes/nginx/data", IsNamedVolume = false };
        var resolvedRemote = dockerService.ResolveVolumeHostPath(relVol, "Oracle", "nginx");
        Assert.Equal("/var/lib/dockermanager/volumes/oracle/nginx/data", resolvedRemote);

        var absLinuxVol = new VolumeMapping { HostPath = "/custom/data/path", IsNamedVolume = false };
        Assert.Equal("/custom/data/path", dockerService.ResolveVolumeHostPath(absLinuxVol, "Oracle", "app"));

        // Reset to local pipe
        sett.Settings.DockerHostType = "Pipe";
    }

    [Fact]
    public async Task TestMultiServer_EnvironmentProfilesIsolation()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"dm_multiserver_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var cred = new CredentialService();
            var sett = new SettingsService(cred);

            // Configure app settings with 2 distinct servers
            sett.Settings.Servers.Clear();
            var localServer = new DockerServerEnvironment
            {
                Id = "srv_local",
                Name = "Local Desktop",
                HostType = "Pipe",
                ProfilesSubfolder = "local"
            };
            var vpsServer = new DockerServerEnvironment
            {
                Id = "srv_vps",
                Name = "Cloud VPS",
                HostType = "Tcp",
                TcpUrl = "tcp://100.64.0.1:2375",
                ProfilesSubfolder = "vps"
            };
            sett.Settings.Servers.Add(localServer);
            sett.Settings.Servers.Add(vpsServer);
            sett.Settings.ActiveServerId = localServer.Id;

            // Verify active server resolution
            Assert.Equal("srv_local", sett.Settings.GetActiveServer().Id);
            Assert.Equal("npipe://./pipe/docker_engine", sett.Settings.GetActiveServer().GetEffectiveUri().ToString());
            Assert.Equal("tcp://100.64.0.1:2375", vpsServer.GetEffectiveUri().ToString());

            // Test profile service isolation
            var profService = new GitHubProfileService(sett, cred);
            await profService.EnsureSubfoldersAndMigrateAsync(sett.Settings.Servers);

            // Create profile for local server
            var localProfile = new ProfileModel
            {
                Name = "DevStack",
                Description = "Local Development Stack",
                Services = { new ServiceDefinition { Id = "db", Image = "postgres:16" } }
            };
            await profService.SaveProfileLocallyAsync(localProfile, localServer.ProfilesSubfolder);

            // Create profile for vps server
            var vpsProfile = new ProfileModel
            {
                Name = "ProdStack",
                Description = "Production Stack",
                Services = { new ServiceDefinition { Id = "web", Image = "nginx:alpine" } }
            };
            await profService.SaveProfileLocallyAsync(vpsProfile, vpsServer.ProfilesSubfolder);

            // Load profiles for local server
            var localProfiles = await profService.LoadProfilesAsync(localServer.ProfilesSubfolder, forceRefreshFromGitHub: false);
            Assert.Contains(localProfiles, p => p.Name == "DevStack");
            Assert.DoesNotContain(localProfiles, p => p.Name == "ProdStack");

            // Load profiles for VPS server
            var vpsProfiles = await profService.LoadProfilesAsync(vpsServer.ProfilesSubfolder, forceRefreshFromGitHub: false);
            Assert.Contains(vpsProfiles, p => p.Name == "ProdStack");
            Assert.DoesNotContain(vpsProfiles, p => p.Name == "DevStack");
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }
}


