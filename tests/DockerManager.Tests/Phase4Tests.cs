using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using DockerManager.App.Helpers;
using DockerManager.App.Models;
using DockerManager.App.Services;
using DockerManager.App.ViewModels;
using Xunit;

namespace DockerManager.Tests;

[Collection("Localization")]
public class Phase4Tests
{
    private sealed class TempEnv : IDisposable
    {
        public string RootDir { get; }
        public CredentialService Credentials { get; }
        public SettingsService Settings { get; }

        public TempEnv()
        {
            RootDir = Path.Combine(Path.GetTempPath(), $"DockerManager_Phase4_{Guid.NewGuid():N}");
            Directory.CreateDirectory(RootDir);
            Credentials = new CredentialService(RootDir);
            Settings = new SettingsService(Credentials, Path.Combine(RootDir, "settings.json"));
            Settings.Settings.LocalProfilesFolder = Path.Combine(RootDir, "profiles");
            Directory.CreateDirectory(Settings.Settings.LocalProfilesFolder);
            Settings.Save();
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(RootDir))
                {
                    Directory.Delete(RootDir, recursive: true);
                }
            }
            catch { }
        }
    }

    private sealed class StubDockerService : IDockerService
    {
        public List<ContainerRuntimeInfo> Containers { get; } = new();
        public List<string> StartedOrder { get; } = new();
        public List<ActiveDockerPortInfo> ActivePorts { get; } = new();

        public Task<(bool IsConnected, string VersionOrError)> PingDockerAsync(CancellationToken ct = default)
            => Task.FromResult((true, "27.0.0"));

        public Task<DockerStartupDiagnosis> DiagnoseDockerStartupAsync(CancellationToken ct = default)
            => Task.FromResult(new DockerStartupDiagnosis { IsConnected = true, DiagnosisSummary = "Connected" });

        public Task<bool> TryLaunchDockerDesktopAsync() => Task.FromResult(true);

        public Task<ContainerRuntimeInfo> GetContainerInfoAsync(string containerName, CancellationToken ct = default)
        {
            var found = Containers.FirstOrDefault(c => string.Equals(c.ContainerName, containerName, StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(found ?? new ContainerRuntimeInfo { ContainerName = containerName, State = ContainerState.NotCreated });
        }

        public Task<List<ContainerRuntimeInfo>> GetAllContainersAsync(CancellationToken ct = default)
            => Task.FromResult(Containers.ToList());

        public Task StartContainerAsync(ServiceDefinition service, string profileName, IProgress<string>? progress = null, ProfileRegistryConfig? registryConfig = null, string? volumesSubfolder = null, CancellationToken ct = default)
        {
            var name = service.GetEffectiveContainerName();
            StartedOrder.Add(name);
            var existing = Containers.FirstOrDefault(c => string.Equals(c.ContainerName, name, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                existing.State = ContainerState.Running;
            }
            else
            {
                Containers.Add(new ContainerRuntimeInfo { ContainerName = name, State = ContainerState.Running });
            }
            return Task.CompletedTask;
        }

        public Task StopContainerAsync(string containerName, CancellationToken ct = default) => Task.CompletedTask;
        public Task RestartContainerAsync(ServiceDefinition service, string profileName, IProgress<string>? progress = null, ProfileRegistryConfig? registryConfig = null, string? volumesSubfolder = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task<ContainerUpdateResult> SafeUpdateContainerAsync(ServiceDefinition service, string profileName, IProgress<string>? progress = null, ProfileRegistryConfig? registryConfig = null, string? volumesSubfolder = null, CancellationToken ct = default)
            => Task.FromResult(new ContainerUpdateResult(false, string.Empty, null, null));
        public Task RemoveContainerAsync(string containerName, bool removeVolumes = false, CancellationToken ct = default) => Task.CompletedTask;
        public Task StreamLogsAsync(string containerName, Action<string> onLineReceived, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetContainerStatsSummaryAsync(string containerName, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<(long SpaceReclaimedBytes, string Summary)> PruneUnusedResourcesAsync(CancellationToken ct = default) => Task.FromResult((0L, string.Empty));
        public Task<List<ActiveDockerPortInfo>> GetActiveDockerPortsAsync(CancellationToken ct = default) => Task.FromResult(ActivePorts.ToList());
        public Task<ImageInspectResult> InspectAndPullImageConfigAsync(string image, bool requiresAuth = false, IProgress<string>? progress = null, CancellationToken ct = default)
            => Task.FromResult(new ImageInspectResult { Success = true });
        public Task<(bool Success, string Message)> TestRegistryConnectionAsync(RegistryCredential cred, CancellationToken ct = default)
            => Task.FromResult((true, "OK"));
        public Task<bool> CheckImageUpdateAvailableAsync(ServiceDefinition service, ProfileRegistryConfig? registryConfig = null, CancellationToken ct = default)
            => Task.FromResult(false);
        public Task<Stream?> GetContainerArchiveStreamAsync(string containerName, string containerPath, CancellationToken ct = default)
            => Task.FromResult<Stream?>(null);
        public Task<bool> ExtractArchiveToContainerAsync(string containerName, string containerPath, Stream tarStream, CancellationToken ct = default)
            => Task.FromResult(false);
    }

    private sealed class StubGitHubAuthService : IGitHubAuthService
    {
        public string? UsernameToReturn { get; set; } = "personaluser";

        public Task<DeviceCodeResponse?> RequestDeviceCodeAsync(string clientId, string scopes = "repo read:packages write:packages read:user", CancellationToken ct = default)
            => Task.FromResult<DeviceCodeResponse?>(null);

        public Task<string?> PollForTokenAsync(string clientId, string deviceCode, int intervalSeconds, int expiresInSeconds, IProgress<string>? progress = null, CancellationToken ct = default)
            => Task.FromResult<string?>(null);

        public Task<string?> FetchAuthenticatedUsernameAsync(string token, CancellationToken ct = default)
            => Task.FromResult(UsernameToReturn);
    }

    private sealed class SecretHolder : INotifyPropertyChanged
    {
        private string _secret = string.Empty;
        public string Secret
        {
            get => _secret;
            set
            {
                if (!string.Equals(_secret, value, StringComparison.Ordinal))
                {
                    _secret = value;
                    OnPropertyChanged();
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    [Fact]
    public void LocalizationService_NlAndEnDictionaries_HaveExactKeyParity_AndHandleBadFormat()
    {
        var loc = LocalizationService.Instance;
        var prevLang = loc.CurrentLanguage;
        try
        {
            var nlKeys = loc.GetKeysForLanguage("nl");
            var enKeys = loc.GetKeysForLanguage("en");

            Assert.NotEmpty(nlKeys);
            Assert.Empty(nlKeys.Except(enKeys, StringComparer.OrdinalIgnoreCase));
            Assert.Empty(enKeys.Except(nlKeys, StringComparer.OrdinalIgnoreCase));

            loc.SetLanguage("nl");
            Assert.Equal("nl", loc.CurrentLanguage);

            // White-space around lang code is handled cleanly
            loc.SetLanguage(" en ");
            Assert.Equal("en", loc.CurrentLanguage);

            // Insufficient format arguments should not throw FormatException
            var unformatted = loc.Get("Backup_CompletedBody", "only-one-arg");
            Assert.Contains("{1}", unformatted);
        }
        finally
        {
            loc.SetLanguage(prevLang);
        }
    }

    [Fact]
    public async Task MainViewModel_AutoStartServicesAsync_StartsOnlyStoppedAutoStartServicesInDependencyOrder()
    {
        using var env = new TempEnv();
        var profileService = new GitHubProfileService(env.Settings, env.Credentials);
        var docker = new StubDockerService();

        var dbService = new ServiceDefinition { Id = "db", DisplayName = "DB", Image = "postgres:16", AutoStart = true };
        var appService = new ServiceDefinition { Id = "api", DisplayName = "API", Image = "myapi:latest", AutoStart = true, DependsOn = new List<string> { "db" } };
        var alreadyRunningService = new ServiceDefinition { Id = "cache", DisplayName = "Cache", Image = "redis:7", AutoStart = true };
        var manualService = new ServiceDefinition { Id = "worker", DisplayName = "Worker", Image = "worker:latest", AutoStart = false };

        docker.Containers.Add(new ContainerRuntimeInfo { ContainerName = "db", State = ContainerState.Exited });
        docker.Containers.Add(new ContainerRuntimeInfo { ContainerName = "api", State = ContainerState.NotCreated });
        docker.Containers.Add(new ContainerRuntimeInfo { ContainerName = "cache", State = ContainerState.Running });
        docker.Containers.Add(new ContainerRuntimeInfo { ContainerName = "worker", State = ContainerState.Exited });

        var profile = new ProfileModel
        {
            Name = "AutoStartStack",
            Services = new List<ServiceDefinition> { appService, manualService, alreadyRunningService, dbService }
        };

        using var vm = new MainViewModel(env.Settings, env.Credentials, profileService, docker);
        vm.Profiles.Add(profile);
        vm.SelectedProfile = profile;

        await vm.AutoStartServicesAsync();

        Assert.Equal(2, docker.StartedOrder.Count);
        Assert.Equal("db", docker.StartedOrder[0]);
        Assert.Equal("api", docker.StartedOrder[1]);
        Assert.DoesNotContain("cache", docker.StartedOrder);
        Assert.DoesNotContain("worker", docker.StartedOrder);
    }

    [Fact]
    public void CommandLineHelper_ParseCommandLine_HandlesEmptyJsonArrayNullElementsAndDoubleQuoteBackslashes()
    {
        // Empty JSON array must return null instead of ["[]"]
        Assert.Null(CommandLineHelper.ParseCommandLine("[]"));
        Assert.Null(CommandLineHelper.ParseCommandLine("[null, null]"));

        // JSON array with null elements skips nulls cleanly
        var fromJson = CommandLineHelper.ParseCommandLine("[\"nginx\", null, \"-g\"]");
        Assert.Equal(new[] { "nginx", "-g" }, fromJson);

        // POSIX double-quote backslash rules: \p is preserved while \\ and \" unescape
        var parsed = CommandLineHelper.ParseCommandLine("\"hello \\\"world\\\" C:\\path\\\\sub\"");
        Assert.Equal(new[] { "hello \"world\" C:\\path\\sub" }, parsed);
    }

    [Fact]
    public void Converters_CountToVisibility_HandlesLargeLong_AndBrushesAreFrozen()
    {
        var countConverter = new CountToVisibilityConverter();
        Assert.Equal(Visibility.Visible, countConverter.Convert(0x1_0000_0000L, typeof(Visibility), null!, CultureInfo.InvariantCulture));
        Assert.Equal(Visibility.Collapsed, countConverter.Convert(0L, typeof(Visibility), null!, CultureInfo.InvariantCulture));

        var brushConverter = new StateToBrushConverter();
        var runningBrush = Assert.IsAssignableFrom<SolidColorBrush>(
            brushConverter.Convert(ContainerState.Running, typeof(System.Windows.Media.Brush), null!, CultureInfo.InvariantCulture));
        var runningBrushSecond = brushConverter.Convert(ContainerState.Running, typeof(System.Windows.Media.Brush), null!, CultureInfo.InvariantCulture);

        Assert.True(runningBrush.IsFrozen);
        Assert.Same(runningBrush, runningBrushSecond);
    }

    [Fact]
    public void PasswordBoxHelper_TwoWayBinding_SyncsPasswordOnStaThread()
    {
        Exception? staException = null;
        var thread = new Thread(() =>
        {
            try
            {
                var holder = new SecretHolder { Secret = "initial-secret" };
                var box = new PasswordBox();

                BindingOperations.SetBinding(
                    box,
                    PasswordBoxHelper.BoundPasswordProperty,
                    new System.Windows.Data.Binding(nameof(SecretHolder.Secret))
                    {
                        Source = holder,
                        Mode = BindingMode.TwoWay
                    });
                PasswordBoxHelper.SetBindPassword(box, true);

                Assert.Equal("initial-secret", box.Password);

                box.Password = "typed-secret";
                Assert.Equal("typed-secret", holder.Secret);

                holder.Secret = "vm-updated-secret";
                Assert.Equal("vm-updated-secret", box.Password);
            }
            catch (Exception ex)
            {
                staException = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (staException != null)
        {
            throw new Exception("STA PasswordBoxHelper test failed", staException);
        }
    }

    [Fact]
    public async Task GitHubLoginViewModel_PreservesExistingRepoOwner_AndDoesNotPreFillPlaintextToken()
    {
        using var env = new TempEnv();
        env.Credentials.SaveGitHubToken("ghp_existing_secret");
        env.Settings.Settings.GitHubRepoOwner = "existing-org";
        env.Settings.Save();

        var prevLang = LocalizationService.Instance.CurrentLanguage;
        try
        {
            LocalizationService.Instance.SetLanguage("en");
            var auth = new StubGitHubAuthService { UsernameToReturn = "personaluser" };
            var vm = new GitHubLoginViewModel(auth, env.Credentials, env.Settings);

            Assert.Equal(string.Empty, vm.TokenInput);
            Assert.Equal(LocalizationService.Instance.Get("GitHubLogin_ExistingTokenHint"), vm.StatusMessage);

            vm.TokenInput = "ghp_new_secret";
            await vm.ValidateAndSaveTokenCommand.ExecuteAsync(null);

            Assert.True(vm.LoginSucceeded);
            Assert.Equal("ghp_new_secret", env.Credentials.GetGitHubToken());
            Assert.Equal("existing-org", env.Settings.Settings.GitHubRepoOwner);

            // Empty GitHubRepoOwner should be filled automatically from authenticated user
            env.Settings.Settings.GitHubRepoOwner = string.Empty;
            var vmEmptyOwner = new GitHubLoginViewModel(auth, env.Credentials, env.Settings)
            {
                TokenInput = "ghp_another_secret"
            };
            await vmEmptyOwner.ValidateAndSaveTokenCommand.ExecuteAsync(null);
            Assert.Equal("personaluser", env.Settings.Settings.GitHubRepoOwner);
        }
        finally
        {
            LocalizationService.Instance.SetLanguage(prevLang);
        }
    }

    [Fact]
    public void LogViewerViewModel_StreamingStatusText_UsesLocalization()
    {
        var prevLang = LocalizationService.Instance.CurrentLanguage;
        try
        {
            var docker = new StubDockerService();
            var vm = new LogViewerViewModel("web-container", docker);

            LocalizationService.Instance.SetLanguage("nl");
            Assert.Equal(LocalizationService.Instance.Get("LogViewer_StatusStopped"), vm.StreamingStatusText);
            vm.IsStreaming = true;
            Assert.Equal(LocalizationService.Instance.Get("LogViewer_StatusStreaming"), vm.StreamingStatusText);

            LocalizationService.Instance.SetLanguage("en");
            Assert.Equal("Streaming active", vm.StreamingStatusText);
            vm.IsStreaming = false;
            Assert.Equal("Stopped", vm.StreamingStatusText);
        }
        finally
        {
            LocalizationService.Instance.SetLanguage(prevLang);
        }
    }

    [Fact]
    public async Task PortConflictViewModel_LocalizesBadgesAndStatus_InDutchAndEnglish()
    {
        var prevLang = LocalizationService.Instance.CurrentLanguage;
        try
        {
            var docker = new StubDockerService();
            var profile = new ProfileModel
            {
                Name = "WebStack",
                Services =
                {
                    new ServiceDefinition
                    {
                        Id = "web1",
                        DisplayName = "Web 1",
                        Ports = { new PortMapping { HostPort = 8080, ContainerPort = 80, Protocol = "tcp" } }
                    },
                    new ServiceDefinition
                    {
                        Id = "web2",
                        DisplayName = "Web 2",
                        Ports = { new PortMapping { HostPort = 8080, ContainerPort = 80, Protocol = "tcp" } }
                    }
                }
            };

            LocalizationService.Instance.SetLanguage("nl");
            var vmNl = new PortConflictViewModel(docker, new[] { profile }, "WebStack", isRemoteServer: true);
            await vmNl.ScanAsync();
            Assert.Equal("🖥️ Server: Lokale Docker Desktop", vmNl.ServerBadgeText);
            Assert.Equal("📄 Profiel: WebStack", vmNl.ProfileBadgeText);
            Assert.Equal("1 Conflicten", vmNl.ConflictBadgeText);
            Assert.Single(vmNl.AllItems);
            Assert.Equal("🔴 Conflict", vmNl.AllItems[0].SeverityBadgeText);
            Assert.StartsWith("💡 Oplossing:", vmNl.AllItems[0].RecommendationText);

            LocalizationService.Instance.SetLanguage("en");
            var vmEn = new PortConflictViewModel(docker, new[] { profile }, "WebStack", isRemoteServer: true);
            await vmEn.ScanAsync();
            Assert.Equal("🖥️ Server: Local Docker Desktop", vmEn.ServerBadgeText);
            Assert.Equal("📄 Profile: WebStack", vmEn.ProfileBadgeText);
            Assert.Equal("1 Conflicts", vmEn.ConflictBadgeText);
            Assert.Single(vmEn.AllItems);
            Assert.Equal("🔴 Conflict", vmEn.AllItems[0].SeverityBadgeText);
            Assert.StartsWith("💡 Solution:", vmEn.AllItems[0].RecommendationText);
        }
        finally
        {
            LocalizationService.Instance.SetLanguage(prevLang);
        }
    }

    [Fact]
    public void DockerService_BuildPortBindings_PreservesMultipleHostPortsForSameContainerPort()
    {
        var ports = new List<PortMapping>
        {
            new() { HostIp = "127.0.0.1", HostPort = 8080, ContainerPort = 80, Protocol = "TCP" },
            new() { HostIp = " ", HostPort = 8081, ContainerPort = 80, Protocol = string.Empty }
        };

        var (exposedPorts, portBindings) = DockerService.BuildPortBindings(ports);

        Assert.True(exposedPorts.ContainsKey("80/tcp"));
        Assert.True(portBindings.TryGetValue("80/tcp", out var bindings));
        Assert.Equal(2, bindings!.Count);
        Assert.Equal("127.0.0.1", bindings[0].HostIP);
        Assert.Equal("8080", bindings[0].HostPort);
        Assert.Equal("0.0.0.0", bindings[1].HostIP);
        Assert.Equal("8081", bindings[1].HostPort);
    }

    [Fact]
    public void SafeClipboardHelper_ReturnsSafelyOnMtaThread_AndHandlesEmptyCopyCut()
    {
        bool? setResult = null;
        string? getResult = "sentinel";

        var mtaThread = new Thread(() =>
        {
            setResult = SafeClipboardHelper.SetText("should-not-retry");
            getResult = SafeClipboardHelper.GetText();
        });
        mtaThread.SetApartmentState(ApartmentState.MTA);
        mtaThread.Start();
        mtaThread.Join();

        Assert.False(setResult);
        Assert.Null(getResult);

        Exception? staException = null;
        var staThread = new Thread(() =>
        {
            try
            {
                SafeClipboardHelper.RegisterGlobalTextBoxClipboardHandlers();
                var tb = new System.Windows.Controls.TextBox { Text = "secret" };
                tb.SelectionStart = 0;
                tb.SelectionLength = 0;

                Assert.False(ApplicationCommands.Copy.CanExecute(null, tb));
                Assert.False(ApplicationCommands.Cut.CanExecute(null, tb));

                tb.IsReadOnly = true;
                tb.SelectAll();
                Assert.True(ApplicationCommands.Copy.CanExecute(null, tb));
                Assert.False(ApplicationCommands.Cut.CanExecute(null, tb));
            }
            catch (Exception ex)
            {
                staException = ex;
            }
        });
        staThread.SetApartmentState(ApartmentState.STA);
        staThread.Start();
        staThread.Join();

        if (staException != null)
        {
            throw new Exception("STA SafeClipboardHelper test failed", staException);
        }
    }
}
