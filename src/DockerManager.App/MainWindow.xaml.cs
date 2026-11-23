using System.Reflection;
using System.Windows;
using DockerManager.App.Helpers;
using DockerManager.App.Models;
using DockerManager.App.Services;
using DockerManager.App.ViewModels;
using DockerManager.App.Views;

namespace DockerManager.App;

public partial class MainWindow : Window
{
    private readonly ICredentialService _credentialService;
    private readonly ISettingsService _settingsService;
    private readonly IGitHubProfileService _gitHubProfileService;
    private readonly IDockerService _dockerService;
    private readonly IGitHubAuthService _gitHubAuthService;
    private readonly IBackupService _backupService;
    private readonly IDotnetAppScannerService _dotnetScannerService;
    private readonly IPythonAppScannerService _pythonScannerService;
    private readonly IDotnetPublisherService _dotnetPublisherService;
    private readonly IAppUpdateService _appUpdateService;
    private readonly IDockerComposeImporterService _composeImporterService;
    private readonly IDockerComposeExporterService _composeExporterService;
    private readonly ICloudflareService _cloudflareService;
    private readonly MainViewModel _viewModel;
    private System.Windows.Forms.NotifyIcon? _notifyIcon;

    public MainWindow() : this(
        new CredentialService())
    {
    }

    private MainWindow(ICredentialService credentialService) : this(
        credentialService,
        new SettingsService(credentialService),
        new GitHubAuthService(),
        new DotnetAppScannerService(),
        new PythonAppScannerService(),
        new DockerComposeImporterService(),
        new DockerComposeExporterService())
    {
    }

    private MainWindow(
        ICredentialService credentialService,
        ISettingsService settingsService,
        IGitHubAuthService gitHubAuthService,
        IDotnetAppScannerService dotnetScannerService,
        IPythonAppScannerService pythonScannerService,
        IDockerComposeImporterService composeImporterService,
        IDockerComposeExporterService composeExporterService)
        : this(
            credentialService,
            settingsService,
            new GitHubProfileService(settingsService, credentialService),
            new DockerService(settingsService, credentialService),
            gitHubAuthService,
            new BackupService(settingsService, new DockerService(settingsService, credentialService)),
            dotnetScannerService,
            pythonScannerService,
            new DotnetPublisherService(settingsService, credentialService),
            new AppUpdateService(settingsService, credentialService),
            composeImporterService,
            composeExporterService,
            new CloudflareService(settingsService, credentialService),
            new MainViewModel(
                settingsService,
                credentialService,
                new GitHubProfileService(settingsService, credentialService),
                new DockerService(settingsService, credentialService),
                new CloudflareService(settingsService, credentialService)))
    {
    }

    public MainWindow(
        ICredentialService credentialService,
        ISettingsService settingsService,
        IGitHubProfileService gitHubProfileService,
        IDockerService dockerService,
        IGitHubAuthService gitHubAuthService,
        IBackupService backupService,
        IDotnetAppScannerService dotnetScannerService,
        IPythonAppScannerService pythonScannerService,
        IDotnetPublisherService dotnetPublisherService,
        IAppUpdateService appUpdateService,
        IDockerComposeImporterService composeImporterService,
        IDockerComposeExporterService composeExporterService,
        ICloudflareService cloudflareService,
        MainViewModel viewModel)
    {
        InitializeComponent();

        _credentialService = credentialService;
        _settingsService = settingsService;
        _gitHubProfileService = gitHubProfileService;
        _dockerService = dockerService;
        _gitHubAuthService = gitHubAuthService;
        _backupService = backupService;
        _dotnetScannerService = dotnetScannerService;
        _pythonScannerService = pythonScannerService;
        _dotnetPublisherService = dotnetPublisherService;
        _appUpdateService = appUpdateService;
        _composeImporterService = composeImporterService;
        _composeExporterService = composeExporterService;
        _cloudflareService = cloudflareService;
        _viewModel = viewModel;
        DataContext = _viewModel;

        LocalizationService.Instance.SetLanguage(_settingsService.Settings.Language);

        _viewModel.RequestOpenSettings += () => ShowSettingsDialog();
        _viewModel.RequestOpenProfileSettings += ShowProfileSettingsDialog;
        _viewModel.RequestConfirmDeleteProfile += ConfirmDeleteProfileAsync;
        _viewModel.RequestOpenLogs += ShowLogViewerDialog;
        _viewModel.RequestEditService += ShowEditServiceDialog;
        _viewModel.RequestAddNewService += ShowAddServiceDialog;
        _viewModel.RequestBackupVolumes += HandleBackupVolumesAsync;
        _viewModel.RequestRestoreBackup += HandleRestoreBackupAsync;
        _viewModel.RequestCheckPortConflicts += ShowPortConflictDialog;
        _viewModel.RequestSwitchProfileConfirmation += ShowProfileSwitchDialogAsync;

        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.SelectedServer) || e.PropertyName == nameof(MainViewModel.SelectedProfile))
            {
                UpdateTrayTooltip();
            }
        };

        RestoreWindowBounds();
        InitializeTrayIcon();

        Closing += (_, _) => SaveWindowBounds();
        Loaded += async (_, _) =>
        {
            await _viewModel.InitializeAsync();
            CheckForUpdatesSilentlyOnStartupAsync().SafeFireAndForget(nameof(CheckForUpdatesSilentlyOnStartupAsync));
        };
    }

    private async Task CheckForUpdatesSilentlyOnStartupAsync()
    {
        try
        {
            if (!_settingsService.Settings.AutoCheckUpdatesOnStart) return;
            await Task.Delay(3000); // Wait 3s after startup
            if (!_settingsService.Settings.AutoCheckUpdatesOnStart) return;
            var result = await _appUpdateService.CheckForUpdatesAsync();
            if (result.IsUpdateAvailable && result.UpdateInfo != null)
            {
                _viewModel.StatusNotification = LocalizationService.Instance.Get("Update_StartupNotification", result.NewVersion ?? string.Empty);
            }
        }
        catch { }
    }

    private void PublishDotnetMenu_Click(object sender, RoutedEventArgs e)
    {
        ShowPublishAppDialog(AppFrameworkType.Dotnet);
    }

    private void PublishPythonMenu_Click(object sender, RoutedEventArgs e)
    {
        ShowPublishAppDialog(AppFrameworkType.Python);
    }

    private async void CheckForUpdatesMenu_Click(object sender, RoutedEventArgs e)
    {
        var loc = LocalizationService.Instance;
        try
        {
            _viewModel.StatusNotification = loc.Get("Update_Checking");
            var result = await _appUpdateService.CheckForUpdatesAsync();

            if (result.IsUpdateAvailable && result.UpdateInfo != null)
            {
                var answer = MessageBox.Show(
                    loc.Get("Update_PromptBody", result.CurrentVersion, result.NewVersion ?? string.Empty),
                    loc.Get("Update_PromptTitle"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information);

                if (answer == MessageBoxResult.Yes)
                {
                    _viewModel.StatusNotification = loc.Get("Update_Downloading");
                    var success = await _appUpdateService.DownloadAndApplyUpdateAsync(result.UpdateInfo);
                    if (!success)
                    {
                        MessageBox.Show(loc.Get("Update_FailedBody"), loc.Get("Update_FailedTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
            }
            else
            {
                var upToDateMsg = result.Message ?? loc.Get("Update_UpToDate", result.CurrentVersion);
                _viewModel.StatusNotification = upToDateMsg;
                MessageBox.Show(upToDateMsg, loc.Get("Update_CheckTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(loc.Get("Update_CheckError", ex.Message), loc.Get("Update_ErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ExitMenu_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void AboutMenu_Click(object sender, RoutedEventArgs e)
    {
        var asm = typeof(App).Assembly;
        var infoVer = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0];
        var ver = asm.GetName().Version;
        var versionStr = !string.IsNullOrWhiteSpace(infoVer) ? $"v{infoVer}" : (ver != null ? $"v{ver.Major}.{ver.Minor}.{ver.Build}" : "v1.0.0");

        var isEn = string.Equals(LocalizationService.Instance.CurrentLanguage, "en", StringComparison.OrdinalIgnoreCase);

        var msg = isEn
            ? $"DockerManager — Profile & Container Hub\n" +
              $"Version: {versionStr}\n" +
              $"Author: W. Cornelissen\n" +
              $"Copyright © 2026 W. Cornelissen\n\n" +
              $"License: MIT License (Open Source)\n" +
              $"GitHub: https://github.com/wcornel/DockerManager\n\n" +
              $"Easily manage all your containers, ports, volumes, and profiles."
            : $"DockerManager — Profile & Container Hub\n" +
              $"Versie: {versionStr}\n" +
              $"Auteur: W. Cornelissen\n" +
              $"Copyright © 2026 W. Cornelissen\n\n" +
              $"Licentie: MIT License (Open Source)\n" +
              $"GitHub: https://github.com/wcornel/DockerManager\n\n" +
              $"Beheer eenvoudig al je containers, poorten, volumes en profielen.";

        var title = isEn ? "About DockerManager" : "Over DockerManager";
        MessageBox.Show(msg, title, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async Task HandleBackupVolumesAsync(ProfileModel profile)
    {
        var loc = LocalizationService.Instance;
        var invalidChars = System.IO.Path.GetInvalidFileNameChars();
        var safeProfileName = string.Join("_", (profile.Name ?? "profile").Split(invalidChars, StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (string.IsNullOrWhiteSpace(safeProfileName)) safeProfileName = "profile";
        var defaultFileName = $"Backup_{safeProfileName}_{DateTime.Now:yyyyMMdd_HHmm}.zip";
        var sfd = new Microsoft.Win32.SaveFileDialog
        {
            Title = loc.Get("Backup_SaveDialogTitle", profile.Name ?? safeProfileName),
            Filter = loc.Get("Backup_ZipFilter"),
            FileName = defaultFileName,
            DefaultExt = ".zip"
        };

        if (sfd.ShowDialog(this) == true)
        {
            var zipPath = sfd.FileName;
            _viewModel.StatusNotification = loc.Get("Backup_Creating");

            var progress = new Progress<(string message, double percentage)>(p =>
            {
                _viewModel.StatusNotification = $"💾 {p.message}";
            });

            var result = await _backupService.CreateBackupAsync(profile, zipPath, progress);

            if (result.Success)
            {
                _viewModel.StatusNotification = loc.Get("Backup_ReadyNotification", System.IO.Path.GetFileName(zipPath), result.FilesCount, result.TotalBytes / 1024.0 / 1024.0);

                var warningsBlock = result.Warnings.Count > 0
                    ? $"{loc.Get("Backup_WarningsHeader")}\n{string.Join("\n", result.Warnings.Take(5))}\n\n"
                    : string.Empty;
                var msg = loc.Get("Backup_CompletedBody", profile.Name ?? safeProfileName, zipPath, result.FilesCount, result.TotalBytes / 1024.0 / 1024.0, warningsBlock);

                var res = MessageBox.Show(msg, loc.Get("Backup_CompletedTitle"), MessageBoxButton.YesNo, MessageBoxImage.Information);
                if (res == MessageBoxResult.Yes)
                {
                    try
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = "explorer.exe",
                            Arguments = $"/select,\"{zipPath}\"",
                            UseShellExecute = true
                        });
                    }
                    catch { }
                }
            }
            else
            {
                _viewModel.StatusNotification = loc.Get("Backup_FailedNotification", result.ErrorMessage ?? string.Empty);
                MessageBox.Show(loc.Get("Backup_FailedBody", result.ErrorMessage ?? string.Empty), loc.Get("Backup_FailedTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private async Task HandleRestoreBackupAsync()
    {
        var loc = LocalizationService.Instance;
        var ofd = new Microsoft.Win32.OpenFileDialog
        {
            Title = loc.Get("Restore_OpenDialogTitle"),
            Filter = loc.Get("Backup_ZipFilter"),
            DefaultExt = ".zip"
        };

        if (ofd.ShowDialog(this) == true)
        {
            var zipPath = ofd.FileName;
            var confirm = MessageBox.Show(
                loc.Get("Restore_ConfirmBody", zipPath),
                loc.Get("Restore_ConfirmTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (confirm != MessageBoxResult.Yes) return;

            _viewModel.StatusNotification = loc.Get("Restore_Restoring");

            var progress = new Progress<(string message, double percentage)>(p =>
            {
                _viewModel.StatusNotification = $"📥 {p.message}";
            });

            var result = await _backupService.RestoreBackupAsync(zipPath, _viewModel.SelectedProfile, overwriteExisting: true, progress);

            if (result.Success)
            {
                _viewModel.StatusNotification = loc.Get("Restore_ReadyNotification", result.RestoredFilesCount, result.RestoredBytes / 1024.0 / 1024.0);

                var warningsBlock = result.Warnings.Count > 0
                    ? $"{loc.Get("Backup_WarningsHeader")}\n{string.Join("\n", result.Warnings.Take(5))}\n\n"
                    : string.Empty;
                var msg = loc.Get("Restore_CompletedBody", result.RestoredFilesCount, result.RestoredBytes / 1024.0 / 1024.0, warningsBlock);

                var res = MessageBox.Show(msg, loc.Get("Restore_CompletedTitle"), MessageBoxButton.YesNo, MessageBoxImage.Information);
                if (res == MessageBoxResult.Yes)
                {
                    await _viewModel.RefreshAllCardStatusesAsync();
                }
            }
            else
            {
                _viewModel.StatusNotification = loc.Get("Restore_FailedNotification", result.ErrorMessage ?? string.Empty);
                MessageBox.Show(loc.Get("Restore_FailedBody", result.ErrorMessage ?? string.Empty), loc.Get("Restore_FailedTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private async void ImportComposeMenu_Click(object sender, RoutedEventArgs e)
    {
        var loc = LocalizationService.Instance;
        var currentProfileName = _viewModel.SelectedProfile?.Name ?? string.Empty;
        var vm = new ImportComposeViewModel(_composeImporterService, currentProfileName);
        var dialog = new ImportComposeDialog(vm) { Owner = this };

        if (dialog.ShowDialog() == true && vm.IsConfirmed && vm.ResultServices.Count > 0)
        {
            var subfolder = _viewModel.SelectedServer?.ProfilesSubfolder ?? _settingsService.Settings.GetActiveServer().ProfilesSubfolder;

            if (vm.TargetIsNewProfile || _viewModel.SelectedProfile == null)
            {
                var targetName = string.IsNullOrWhiteSpace(vm.TargetProfileName) ? "Compose Stack" : vm.TargetProfileName;
                var newProfile = new ProfileModel
                {
                    Name = targetName,
                    Description = loc.Get("ImportCompose_ImportedDescription", DateTime.Now.ToString("yyyy-MM-dd HH:mm")),
                    AutoStopPreviousOnSwitch = false
                };
                foreach (var svc in vm.ResultServices)
                {
                    newProfile.Services.Add(svc);
                }

                await _gitHubProfileService.SaveProfileLocallyAsync(newProfile, subfolder);
                await _viewModel.LoadProfilesAsync(forceRefresh: false);
                _viewModel.SelectedProfile = _viewModel.Profiles.FirstOrDefault(p => p.Name.Equals(newProfile.Name, StringComparison.OrdinalIgnoreCase)) ?? _viewModel.Profiles.FirstOrDefault();
                await _viewModel.SaveCurrentProfileAsync();
                _viewModel.StatusNotification = loc.Get("ImportCompose_NewProfileNotification", newProfile.Name, vm.ResultServices.Count);
            }
            else
            {
                var active = _viewModel.SelectedProfile;
                if (active != null)
                {
                    int addedCount = 0;
                    foreach (var svc in vm.ResultServices)
                    {
                        var originalId = svc.Id;
                        int suffix = 1;
                        while (active.Services.Any(s => s.Id.Equals(svc.Id, StringComparison.OrdinalIgnoreCase)))
                        {
                            svc.Id = $"{originalId}_{suffix++}";
                            svc.ContainerName = svc.Id;
                        }

                        active.Services.Add(svc);
                        addedCount++;
                    }

                    await _viewModel.SaveCurrentProfileAsync();
                    _viewModel.RebuildServiceCards();
                    _ = _viewModel.RefreshAllCardStatusesAsync();
                    _viewModel.StatusNotification = loc.Get("ImportCompose_AddedNotification", addedCount, active.Name);
                }
            }
        }
    }

    private void ExportComposeMenu_Click(object sender, RoutedEventArgs e)
    {
        var loc = LocalizationService.Instance;
        var profile = _viewModel.SelectedProfile;
        if (profile == null || profile.Services == null || profile.Services.Count == 0)
        {
            MessageBox.Show(loc["Export_Empty"], loc.Get("Export_DialogTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var saveDialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = loc.Get("Export_DialogTitle"),
            FileName = "docker-compose.yml",
            DefaultExt = ".yml",
            Filter = loc.Get("Export_YamlFilter")
        };

        if (saveDialog.ShowDialog() == true)
        {
            var success = _composeExporterService.ExportToFile(profile, saveDialog.FileName, out var err);
            if (success)
            {
                _viewModel.StatusNotification = loc.Get("Export_SuccessNotification", profile.Name, System.IO.Path.GetFileName(saveDialog.FileName));
                MessageBox.Show(loc["Export_Success"], loc.Get("Export_DialogTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show(loc.Get("Export_ErrorBody", err ?? string.Empty), loc.Get("General_ErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void RestoreWindowBounds()
    {
        try
        {
            var s = _settingsService.Settings;
            if (s.WindowWidth >= MinWidth) Width = s.WindowWidth;
            if (s.WindowHeight >= MinHeight) Height = s.WindowHeight;

            if (s.WindowLeft.HasValue && s.WindowTop.HasValue)
            {
                var virtualLeft = SystemParameters.VirtualScreenLeft;
                var virtualTop = SystemParameters.VirtualScreenTop;
                var virtualWidth = SystemParameters.VirtualScreenWidth;
                var virtualHeight = SystemParameters.VirtualScreenHeight;

                if (s.WindowLeft.Value >= virtualLeft && 
                    s.WindowLeft.Value + 100 <= virtualLeft + virtualWidth &&
                    s.WindowTop.Value >= virtualTop && 
                    s.WindowTop.Value + 100 <= virtualTop + virtualHeight)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual;
                    Left = s.WindowLeft.Value;
                    Top = s.WindowTop.Value;
                }
            }

            if (s.WindowState == "Maximized")
            {
                WindowState = WindowState.Maximized;
            }
        }
        catch { }
    }

    private void SaveWindowBounds()
    {
        try
        {
            var s = _settingsService.Settings;
            if (WindowState == WindowState.Maximized)
            {
                s.WindowState = "Maximized";
                if (RestoreBounds.Width >= MinWidth) s.WindowWidth = RestoreBounds.Width;
                if (RestoreBounds.Height >= MinHeight) s.WindowHeight = RestoreBounds.Height;
                s.WindowLeft = RestoreBounds.Left;
                s.WindowTop = RestoreBounds.Top;
            }
            else if (WindowState == WindowState.Normal)
            {
                s.WindowState = "Normal";
                if (ActualWidth >= MinWidth) s.WindowWidth = ActualWidth;
                if (ActualHeight >= MinHeight) s.WindowHeight = ActualHeight;
                s.WindowLeft = Left;
                s.WindowTop = Top;
            }
            _settingsService.Save();
        }
        catch { }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        DisposeTrayIcon();
        _viewModel.Dispose();
    }
}
