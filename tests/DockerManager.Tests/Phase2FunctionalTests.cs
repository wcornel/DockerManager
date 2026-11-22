using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using DockerManager.App.Helpers;
using DockerManager.App.Models;
using DockerManager.App.Services;
using DockerManager.App.ViewModels;
using Xunit;

namespace DockerManager.Tests;

public class Phase2FunctionalTests
{
    private sealed class FakeDockerService : IDockerService
    {
        public Dictionary<string, ContainerState> States { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> StartedOrder { get; } = new();
        public List<string> CompletedOrder { get; } = new();
        public List<string> StoppedContainers { get; } = new();
        public List<ActiveDockerPortInfo> ActivePorts { get; } = new();
        public Func<string, CancellationToken, Task>? OnStartAsync { get; set; }
        public Action<string>? OnStop { get; set; }

        public Task<(bool IsConnected, string VersionOrError)> PingDockerAsync(CancellationToken ct = default)
            => Task.FromResult((true, "Docker 27.0 (fake)"));

        public Task<DockerStartupDiagnosis> DiagnoseDockerStartupAsync(CancellationToken ct = default)
            => Task.FromResult(new DockerStartupDiagnosis { IsConnected = true, DiagnosisSummary = "Connected" });

        public Task<bool> TryLaunchDockerDesktopAsync() => Task.FromResult(true);

        public Task<ContainerRuntimeInfo> GetContainerInfoAsync(string containerName, CancellationToken ct = default)
        {
            var state = States.TryGetValue(containerName, out var s) ? s : ContainerState.NotCreated;
            return Task.FromResult(new ContainerRuntimeInfo
            {
                ContainerName = containerName,
                State = state,
                StatusText = state.ToString()
            });
        }

        public Task<List<ContainerRuntimeInfo>> GetAllContainersAsync(CancellationToken ct = default)
        {
            var list = States.Select(kvp => new ContainerRuntimeInfo
            {
                ContainerName = kvp.Key,
                State = kvp.Value,
                StatusText = kvp.Value.ToString()
            }).ToList();
            return Task.FromResult(list);
        }

        public async Task StartContainerAsync(ServiceDefinition service, string profileName, IProgress<string>? progress = null, ProfileRegistryConfig? registryConfig = null, CancellationToken ct = default)
        {
            var name = service.GetEffectiveContainerName();
            StartedOrder.Add(name);
            if (OnStartAsync != null)
            {
                await OnStartAsync(name, ct);
            }
            States[name] = ContainerState.Running;
            CompletedOrder.Add(name);
        }

        public Task StopContainerAsync(string containerName, CancellationToken ct = default)
        {
            StoppedContainers.Add(containerName);
            OnStop?.Invoke(containerName);
            States[containerName] = ContainerState.Exited;
            return Task.CompletedTask;
        }

        public Task RestartContainerAsync(ServiceDefinition service, string profileName, IProgress<string>? progress = null, ProfileRegistryConfig? registryConfig = null, CancellationToken ct = default)
        {
            States[service.GetEffectiveContainerName()] = ContainerState.Running;
            return Task.CompletedTask;
        }

        public Task<ContainerUpdateResult> SafeUpdateContainerAsync(ServiceDefinition service, string profileName, IProgress<string>? progress = null, ProfileRegistryConfig? registryConfig = null, CancellationToken ct = default)
        {
            States[service.GetEffectiveContainerName()] = ContainerState.Running;
            return Task.FromResult(new ContainerUpdateResult(true, "Updated", "old", "new"));
        }

        public Task RemoveContainerAsync(string containerName, bool removeVolumes = false, CancellationToken ct = default)
        {
            States[containerName] = ContainerState.NotCreated;
            return Task.CompletedTask;
        }

        public async Task StreamLogsAsync(string containerName, Action<string> onLineReceived, CancellationToken ct = default)
        {
            onLineReceived($"Log start for {containerName}");
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                // Expected on cancel
            }
        }

        public Task<string?> GetContainerStatsSummaryAsync(string containerName, CancellationToken ct = default)
            => Task.FromResult<string?>(null);

        public Task<(long SpaceReclaimedBytes, string Summary)> PruneUnusedResourcesAsync(CancellationToken ct = default)
            => Task.FromResult((0L, "0 B"));

        public Task<List<ActiveDockerPortInfo>> GetActiveDockerPortsAsync(CancellationToken ct = default)
            => Task.FromResult(new List<ActiveDockerPortInfo>(ActivePorts));

        public Task<ImageInspectResult> InspectAndPullImageConfigAsync(string image, bool requiresAuth = false, IProgress<string>? progress = null, CancellationToken ct = default)
            => Task.FromResult(new ImageInspectResult { Success = true });

        public Task<(bool Success, string Message)> TestRegistryConnectionAsync(RegistryCredential cred, CancellationToken ct = default)
            => Task.FromResult((true, "OK"));

        public Task<bool> CheckImageUpdateAvailableAsync(ServiceDefinition service, ProfileRegistryConfig? registryConfig = null, CancellationToken ct = default)
            => Task.FromResult(false);

        public Task<Stream?> GetContainerArchiveStreamAsync(string containerName, string containerPath, CancellationToken ct = default)
            => Task.FromResult<Stream?>(null);

        public Task<bool> ExtractArchiveToContainerAsync(string containerName, string containerPath, Stream tarStream, CancellationToken ct = default)
            => Task.FromResult(true);
    }

    [Fact]
    public void EditServiceViewModel_ToServiceDefinition_PreservesNetworkModeDependsOnLabelsLastUpdatedAndReadOnly()
    {
        using var env = new TestEnvironmentScope();
        var server = new DockerServerEnvironment
        {
            Id = "vps",
            Name = "VPS",
            HostType = "Tcp",
            TcpUrl = "tcp://10.0.0.2:2375",
            CloudflareDomain = "example.org",
            CloudflareAccountId = "acc_1",
            CloudflareTunnelId = "tun_1"
        };
        env.Settings.Settings.Servers = new List<DockerServerEnvironment> { server };
        env.Settings.Settings.ActiveServerId = "vps";

        var timestamp = new DateTime(2026, 10, 15, 12, 30, 0, DateTimeKind.Utc);
        var existing = new ServiceDefinition
        {
            Id = "worker",
            DisplayName = "Worker",
            ContainerName = "worker",
            Image = "worker:1.0",
            NetworkMode = "host",
            LastUpdatedUtc = timestamp,
            DependsOn = new List<string> { "db", "redis" },
            Labels = new Dictionary<string, string>
            {
                ["custom.role"] = "queue",
                ["traefik.enable"] = "true"
            },
            Volumes = new List<VolumeMapping>
            {
                new VolumeMapping
                {
                    HostPath = "./config.json",
                    ContainerPath = "/app/config.json",
                    IsNamedVolume = false,
                    ReadOnly = true
                }
            }
        };

        var vm = new EditServiceViewModel(existing, null, null, env.Settings, profile: null);

        Assert.True(vm.HasCloudflareConfigured);
        Assert.Equal("example.org", vm.CloudflareDomain);

        // AddVolume should default to a bind mount (IsNamedVolume = false)
        vm.AddVolumeCommand.Execute(null);
        Assert.Equal(2, vm.Volumes.Count);
        Assert.False(vm.Volumes[1].IsNamedVolume);
        Assert.False(vm.Volumes[1].ReadOnly);

        var updated = vm.ToServiceDefinition();

        Assert.Equal("host", updated.NetworkMode);
        Assert.Equal(timestamp, updated.LastUpdatedUtc);
        Assert.Equal(new[] { "db", "redis" }, updated.DependsOn);
        Assert.Equal("queue", updated.Labels["custom.role"]);
        Assert.Equal("true", updated.Labels["traefik.enable"]);
        Assert.Equal(2, updated.Volumes.Count);
        Assert.True(updated.Volumes[0].ReadOnly);
        Assert.False(updated.Volumes[0].IsNamedVolume);
        Assert.False(updated.Volumes[1].IsNamedVolume);
    }

    [Fact]
    public async Task ServiceCardViewModel_StartStopRestartUpdateRemove_RefreshesStatusWhileBusy()
    {
        using var env = new TestEnvironmentScope();
        var fakeDocker = new FakeDockerService();
        var service = new ServiceDefinition
        {
            Id = "api",
            DisplayName = "API",
            ContainerName = "api",
            Image = "api:latest"
        };
        fakeDocker.States["api"] = ContainerState.Exited;

        var card = new ServiceCardViewModel(service, "Default", fakeDocker, env.Settings);

        await card.StartCommand.ExecuteAsync(null);
        Assert.Equal(ContainerState.Running, card.RuntimeInfo.State);
        Assert.True(card.IsRunning);

        await card.StopCommand.ExecuteAsync(null);
        Assert.Equal(ContainerState.Exited, card.RuntimeInfo.State);
        Assert.False(card.IsRunning);
        Assert.True(card.IsStopped);

        await card.RestartCommand.ExecuteAsync(null);
        Assert.Equal(ContainerState.Running, card.RuntimeInfo.State);
        Assert.True(card.IsRunning);

        await card.SafeUpdateCommand.ExecuteAsync(null);
        Assert.Equal(ContainerState.Running, card.RuntimeInfo.State);
        Assert.True(card.IsRunning);

        await card.RemoveCommand.ExecuteAsync(null);
        Assert.Equal(ContainerState.NotCreated, card.RuntimeInfo.State);
        Assert.False(card.IsRunning);
        Assert.True(card.IsStopped);
    }

    [Fact]
    public void SafeClipboardHelper_ApplyPasteToTextBox_ClearsSelectionRespectsMaxLengthAndSingleLine()
    {
        Exception? threadEx = null;
        var thread = new Thread(() =>
        {
            try
            {
                // 1. Single-line TextBox strips newlines, clears selection, and respects MaxLength
                var single = new System.Windows.Controls.TextBox
                {
                    Text = "abXY",
                    AcceptsReturn = false,
                    MaxLength = 6
                };
                single.Select(2, 2); // replaces "XY", max remaining capacity = 4

                SafeClipboardHelper.ApplyPasteToTextBox(single, "12\r\n3456");

                Assert.Equal("ab1234", single.Text);
                Assert.Equal(0, single.SelectionLength);
                Assert.Equal(6, single.SelectionStart);

                // 2. Multiline TextBox preserves newlines
                var multi = new System.Windows.Controls.TextBox
                {
                    Text = "line1",
                    AcceptsReturn = true
                };
                multi.SelectionStart = multi.Text.Length;
                multi.SelectionLength = 0;

                SafeClipboardHelper.ApplyPasteToTextBox(multi, "\r\nline2");

                Assert.Equal("line1\r\nline2", multi.Text);
                Assert.Equal(0, multi.SelectionLength);
                Assert.Equal(multi.Text.Length, multi.SelectionStart);
            }
            catch (Exception ex)
            {
                threadEx = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (threadEx != null)
        {
            throw new Exception("STA clipboard test failed", threadEx);
        }
    }

    [Fact]
    public void CommandLineHelper_FormatAndParse_RoundtripsBackslashesAndPaths()
    {
        var original = new[]
        {
            @"C:\app\config.json",
            @"\\server\share\file name.txt",
            "--flag=val",
            "simple"
        };

        var formatted = CommandLineHelper.FormatCommandLine(original);
        var parsed = CommandLineHelper.ParseCommandLine(formatted);

        Assert.NotNull(parsed);
        Assert.Equal(original, parsed);
    }

    [Fact]
    public async Task MainViewModel_ProfileSwitchCancel_RevertsSelectionAndDoesNotSaveOrStop()
    {
        using var env = new TestEnvironmentScope();
        var fakeDocker = new FakeDockerService();
        var profileService = new GitHubProfileService(env.Settings, env.Credentials);
        var cf = new CloudflareService(env.Settings, env.Credentials);
        var vm = new MainViewModel(env.Settings, env.Credentials, profileService, fakeDocker, cf);

        var p1 = new ProfileModel
        {
            Name = "ProfileOne",
            Services = new List<ServiceDefinition>
            {
                new ServiceDefinition { Id = "svc1", ContainerName = "svc1", Image = "nginx:alpine" }
            }
        };
        var p2 = new ProfileModel
        {
            Name = "ProfileTwo",
            Services = new List<ServiceDefinition>
            {
                new ServiceDefinition { Id = "svc2", ContainerName = "svc2", Image = "redis:alpine" }
            }
        };

        vm.Profiles.Add(p1);
        vm.Profiles.Add(p2);

        env.Settings.Settings.PromptToStopPreviousProfile = true;
        vm.SelectedProfile = p1;
        Assert.Equal("ProfileOne", env.Settings.Settings.LastActiveProfile);

        var promptInvoked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.RequestSwitchProfileConfirmation += _ =>
        {
            promptInvoked.TrySetResult(true);
            return Task.FromResult<bool?>(null); // User clicked Cancel
        };

        vm.SelectedProfile = p2;
        await promptInvoked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(50);

        Assert.Same(p1, vm.SelectedProfile);
        Assert.Equal("ProfileOne", env.Settings.Settings.LastActiveProfile);
        Assert.Empty(fakeDocker.StoppedContainers);
    }

    [Fact]
    public async Task MainViewModel_SortAndWaves_HandlesDuplicateIdsAndOrdersDependencies()
    {
        using var env = new TestEnvironmentScope();
        var tcsDbStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fakeDocker = new FakeDockerService
        {
            OnStartAsync = async (name, _) =>
            {
                if (name == "db")
                {
                    await Task.Delay(30);
                    tcsDbStarted.TrySetResult(true);
                }
            }
        };

        var profileService = new GitHubProfileService(env.Settings, env.Credentials);
        var vm = new MainViewModel(env.Settings, env.Credentials, profileService, fakeDocker);

        var webSvc = new ServiceDefinition { Id = "web", ContainerName = "web", Image = "web:latest", DependsOn = new List<string> { "api" } };
        var apiSvc = new ServiceDefinition { Id = "api", ContainerName = "api", Image = "api:latest", DependsOn = new List<string> { "db" } };
        var dbSvc = new ServiceDefinition { Id = "db", ContainerName = "db", Image = "postgres:16" };
        var dupSvc1 = new ServiceDefinition { Id = "dup", ContainerName = "dup-1", Image = "busybox" };
        var dupSvc2 = new ServiceDefinition { Id = "dup", ContainerName = "dup-2", Image = "busybox" };
        var emptyIdSvc = new ServiceDefinition { Id = "", ContainerName = "unnamed", Image = "busybox" };

        var webCard = new ServiceCardViewModel(webSvc, "P", fakeDocker, env.Settings);
        var apiCard = new ServiceCardViewModel(apiSvc, "P", fakeDocker, env.Settings);
        var dbCard = new ServiceCardViewModel(dbSvc, "P", fakeDocker, env.Settings);
        var dupCard1 = new ServiceCardViewModel(dupSvc1, "P", fakeDocker, env.Settings);
        var dupCard2 = new ServiceCardViewModel(dupSvc2, "P", fakeDocker, env.Settings);
        var emptyCard = new ServiceCardViewModel(emptyIdSvc, "P", fakeDocker, env.Settings);

        vm.ServiceCards.Add(webCard);
        vm.ServiceCards.Add(dupCard1);
        vm.ServiceCards.Add(apiCard);
        vm.ServiceCards.Add(dupCard2);
        vm.ServiceCards.Add(dbCard);
        vm.ServiceCards.Add(emptyCard);

        var sorted = MainViewModel.SortCardsByDependencies(vm.ServiceCards);
        Assert.Equal(6, sorted.Count);
        Assert.True(sorted.IndexOf(dbCard) < sorted.IndexOf(apiCard));
        Assert.True(sorted.IndexOf(apiCard) < sorted.IndexOf(webCard));

        foreach (var c in vm.ServiceCards)
        {
            c.RuntimeInfo = new ContainerRuntimeInfo { ContainerName = c.ContainerName, State = ContainerState.Exited };
        }

        await vm.StartAllCommand.ExecuteAsync(null);
        Assert.True(tcsDbStarted.Task.IsCompletedSuccessfully);

        var idxDbDone = fakeDocker.CompletedOrder.IndexOf("db");
        var idxApiStart = fakeDocker.StartedOrder.IndexOf("api");
        var idxApiDone = fakeDocker.CompletedOrder.IndexOf("api");
        var idxWebStart = fakeDocker.StartedOrder.IndexOf("web");

        Assert.True(idxDbDone >= 0 && idxApiStart >= 0);
        Assert.True(fakeDocker.StartedOrder.IndexOf("db") < idxApiStart);
        Assert.True(fakeDocker.StartedOrder.IndexOf("api") < idxWebStart);
        Assert.True(fakeDocker.CompletedOrder.IndexOf("db") < fakeDocker.CompletedOrder.IndexOf("api"));
        Assert.True(idxApiDone < fakeDocker.CompletedOrder.IndexOf("web"));
    }

    [Fact]
    public async Task SettingsViewModel_CancelDoesNotMutateActiveServer_AndRenameDeleteRecreateWorkCleanly()
    {
        using var env = new TestEnvironmentScope();
        var srvLocal = new DockerServerEnvironment
        {
            Id = "local",
            Name = "Local",
            HostType = "Pipe",
            ProfilesSubfolder = "local",
            CloudflareDomain = "local.example.org"
        };
        var srvVps = new DockerServerEnvironment
        {
            Id = "vps",
            Name = "VPS",
            HostType = "Tcp",
            TcpUrl = "tcp://10.0.0.5:2375",
            ProfilesSubfolder = "vps",
            CloudflareDomain = "vps.example.org"
        };
        env.Settings.Settings.Servers = new List<DockerServerEnvironment> { srvLocal, srvVps };
        env.Settings.Settings.ActiveServerId = "local";
        env.Settings.Save();

        var profileService = new GitHubProfileService(env.Settings, env.Credentials);
        profileService.SaveProfileLocally(new ProfileModel { Name = "OldName" }, "local");
        profileService.SaveProfileLocally(new ProfileModel { Name = "Alpha" }, "local");

        var docker = new FakeDockerService();
        var cf = new CloudflareService(env.Settings, env.Credentials);
        var vm = new SettingsViewModel(env.Settings, env.Credentials, profileService, docker, cf);

        // 1. Cancel must not mutate ActiveServerId
        var vpsTab = vm.Tabs.OfType<ServerSettingsTabViewModel>().First(t => t.Server.Id == "vps");
        vpsTab.MakeActiveServerCommand.Execute(null);
        vm.CancelCommand.Execute(null);
        Assert.Equal("local", env.Settings.Settings.ActiveServerId);

        // 2. RefreshGitHubAuthStatus preserves unsaved edits across tabs
        vm.GeneralTab.GitHubRepoName = "CustomUnsavedRepo";
        vpsTab.Name = "EditedVpsName";
        env.Credentials.SaveGitHubToken("ghp_test_token_123");
        env.Settings.Settings.LoggedInUsername = "testuser";
        vm.RefreshGitHubAuthStatus();
        Assert.Equal("CustomUnsavedRepo", vm.GeneralTab.GitHubRepoName);
        Assert.Equal("EditedVpsName", vpsTab.Name);
        Assert.True(vm.GeneralTab.IsLoggedIn);

        // 3. Rename OldName -> NewName AND Delete Alpha + recreate Alpha
        var localTab = vm.Tabs.OfType<ServerSettingsTabViewModel>().First(t => t.Server.Id == "local");
        var oldProfileVm = localTab.Profiles.First(p => p.OriginalName == "OldName");
        oldProfileVm.Name = "NewName";

        // Verify domain inheritance stays empty when matching server domain, and CopyRegistrySettings works for server source
        var serverSource = oldProfileVm.CopySources.First(s => s.IsServerDefaults);
        oldProfileVm.SelectedCopySource = serverSource;
        oldProfileVm.CopyRegistrySettingsCommand.Execute(null);
        Assert.Equal("ghcr.io", oldProfileVm.RegistryServer);
        Assert.Equal("testuser", oldProfileVm.RegistryUsername);
        Assert.Equal("ghp_test_token_123", oldProfileVm.RegistryPassword);

        var alphaVm = localTab.Profiles.First(p => p.OriginalName == "Alpha");
        localTab.SelectedProfile = alphaVm;
        localTab.DeleteProfileCommand.Execute(null);
        localTab.AddProfileCommand.Execute(null);
        localTab.SelectedProfile!.Name = "Alpha";

        await vm.SaveAsync();

        var localDir = Path.Combine(env.ProfilesDir, "local");
        Assert.False(File.Exists(Path.Combine(localDir, "oldname.json")));
        Assert.True(File.Exists(Path.Combine(localDir, "newname.json")));
        Assert.True(File.Exists(Path.Combine(localDir, "alpha.json")));
        Assert.Equal(string.Empty, oldProfileVm.Profile.Cloudflare.Domain);
    }

    [Fact]
    public async Task PortConflictViewModel_DetectsExternalContainerConflict_SeparatesProtocolsAndRespectsHostIp()
    {
        var fakeDocker = new FakeDockerService();
        fakeDocker.ActivePorts.AddRange(new[]
        {
            new ActiveDockerPortInfo { ContainerName = "rogue-nginx", State = "running", PublicPort = 8080, PrivatePort = 80, Protocol = "tcp", IpAddress = "0.0.0.0" },
            new ActiveDockerPortInfo { ContainerName = "other-tcp", State = "running", PublicPort = 9000, PrivatePort = 9000, Protocol = "tcp", IpAddress = "0.0.0.0" },
            new ActiveDockerPortInfo { ContainerName = "ext-loopback", State = "running", PublicPort = 7070, PrivatePort = 7070, Protocol = "tcp", IpAddress = "127.0.0.1" },
            new ActiveDockerPortInfo { ContainerName = "ext-wildcard", State = "running", PublicPort = 7071, PrivatePort = 7071, Protocol = "tcp", IpAddress = "0.0.0.0" }
        });

        var profile = new ProfileModel
        {
            Name = "Prod",
            Services = new List<ServiceDefinition>
            {
                new ServiceDefinition
                {
                    Id = "my-web",
                    DisplayName = "My Web",
                    ContainerName = "my-web",
                    Ports = new List<PortMapping>
                    {
                        new PortMapping { HostPort = 8080, ContainerPort = 8080, Protocol = "tcp", HostIp = "0.0.0.0" },
                        new PortMapping { HostPort = 9000, ContainerPort = 9000, Protocol = "udp", HostIp = "0.0.0.0" },
                        new PortMapping { HostPort = 7070, ContainerPort = 7070, Protocol = "tcp", HostIp = "192.168.1.10" },
                        new PortMapping { HostPort = 7071, ContainerPort = 7071, Protocol = "tcp", HostIp = "127.0.0.1" }
                    }
                }
            }
        };

        var vm = new PortConflictViewModel(fakeDocker, new[] { profile }, "Prod", "RemoteVPS", isRemoteServer: true);
        await vm.ScanAsync();

        var item8080 = Assert.Single(vm.AllItems, i => i.Port == 8080 && i.Protocol == "tcp");
        Assert.Equal(PortConflictSeverity.Conflict, item8080.Severity);

        var item9000Udp = Assert.Single(vm.AllItems, i => i.Port == 9000 && i.Protocol == "udp");
        Assert.Equal(PortConflictSeverity.Ok, item9000Udp.Severity);

        var item7070 = Assert.Single(vm.AllItems, i => i.Port == 7070 && i.Protocol == "tcp");
        Assert.NotEqual(PortConflictSeverity.Conflict, item7070.Severity);

        var item7071 = Assert.Single(vm.AllItems, i => i.Port == 7071 && i.Protocol == "tcp");
        Assert.Equal(PortConflictSeverity.Conflict, item7071.Severity);
    }

    [Fact]
    public async Task LogViewerViewModel_RapidStartStop_DoesNotThrowRaceExceptions()
    {
        var fakeDocker = new FakeDockerService();
        var vm = new LogViewerViewModel("test-container", fakeDocker);

        for (int i = 0; i < 10; i++)
        {
            var startTask = vm.StartStreamingAsync();
            await Task.Delay(5);
            vm.StopStreaming();
            await startTask;
        }

        Assert.False(vm.IsStreaming);
    }

    [Fact]
    public void ImportComposeAndPublishViewModel_Phase2Fixes_BehaveCorrectly()
    {
        using var env = new TestEnvironmentScope();

        // 1. ImportComposeViewModel re-parses when YamlText changes after initial parse & defaults to new profile when no active profile
        var importer = new DockerComposeImporterService();
        var importVm = new ImportComposeViewModel(importer, string.Empty);
        Assert.False(importVm.HasActiveProfile);
        Assert.True(importVm.TargetIsNewProfile);

        importVm.YamlText = """
            services:
              svc-a:
                image: alpine:latest
            """;
        importVm.ParseYamlCommand.Execute(null);
        Assert.True(importVm.HasParsedServices);
        Assert.Equal("svc-a", Assert.Single(importVm.ParsedServices).Id);

        importVm.YamlText = """
            services:
              svc-b:
                image: nginx:alpine
            """;
        Assert.False(importVm.HasParsedServices);
        importVm.ConfirmImportCommand.Execute(null);
        Assert.True(importVm.IsConfirmed);
        Assert.Equal("svc-b", Assert.Single(importVm.ResultServices).Id);

        // 2. PublishDotnetAppViewModel picks up server-level Cloudflare & syncs subdomain across AppName edits
        env.Settings.Settings.CloudflareDomain = string.Empty;
        env.Settings.Settings.CloudflareAccountId = string.Empty;
        env.Settings.Settings.CloudflareTunnelId = string.Empty;
        env.Settings.Settings.Servers = new List<DockerServerEnvironment>
        {
            new DockerServerEnvironment
            {
                Id = "vps",
                Name = "VPS",
                HostType = "Tcp",
                TcpUrl = "tcp://10.0.0.2:2375",
                CloudflareDomain = "apps.example.org",
                CloudflareAccountId = "acc_vps",
                CloudflareTunnelId = "tun_vps"
            }
        };
        env.Settings.Settings.ActiveServerId = "vps";

        var pubVm = new PublishDotnetAppViewModel(
            new DotnetAppScannerService(),
            new PythonAppScannerService(),
            new DotnetPublisherService(env.Settings, env.Credentials),
            env.Settings,
            env.Credentials,
            new CloudflareService(env.Settings, env.Credentials),
            AppFrameworkType.Dotnet,
            profile: null);

        Assert.True(pubVm.HasCloudflareConfigured);
        Assert.Equal("apps.example.org", pubVm.CloudflareDomain);

        pubVm.AppName = "FirstApp";
        Assert.Equal("firstapp", pubVm.CloudflareSubdomain);
        pubVm.AppName = "SecondApp";
        Assert.Equal("secondapp", pubVm.CloudflareSubdomain);

        pubVm.CloudflareSubdomain = "custom-sub";
        pubVm.AppName = "ThirdApp";
        Assert.Equal("custom-sub", pubVm.CloudflareSubdomain);
    }
}
