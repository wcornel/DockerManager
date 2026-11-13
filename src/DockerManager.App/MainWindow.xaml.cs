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
        _viewModel.RequestOpenGitHubLogin += ShowGitHubLoginDialog;
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
            await Task.Delay(3000); // Wait 3s after startup
            var result = await _appUpdateService.CheckForUpdatesAsync();
            if (result.IsUpdateAvailable && result.UpdateInfo != null)
            {
                _viewModel.StatusNotification = $"✨ Update {result.NewVersion} beschikbaar! Ga naar 'Help ➔ Zoeken naar Updates...' om te installeren.";
            }
        }
        catch { }
    }

    private void AddNewProfileMenu_Click(object sender, RoutedEventArgs e)
    {
        ShowAddProfileDialog();
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
        try
        {
            _viewModel.StatusNotification = "🔍 Zoeken naar nieuwe DockerManager updates...";
            var result = await _appUpdateService.CheckForUpdatesAsync();

            if (result.IsUpdateAvailable && result.UpdateInfo != null)
            {
                var answer = MessageBox.Show(
                    $"Er is een nieuwe versie van DockerManager beschikbaar!\n\n" +
                    $"Huidige versie: {result.CurrentVersion}\n" +
                    $"Nieuwe versie: {result.NewVersion}\n\n" +
                    $"Wil je de update nu downloaden en de app herstarten naar de nieuwste versie?",
                    "Update Beschikbaar",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information);

                if (answer == MessageBoxResult.Yes)
                {
                    _viewModel.StatusNotification = "🚀 Bezig met downloaden en toepassen van update...";
                    var success = await _appUpdateService.DownloadAndApplyUpdateAsync(result.UpdateInfo);
                    if (!success)
                    {
                        MessageBox.Show("Het downloaden of toepassen van de update is mislukt. Probeer het later opnieuw.", "Update Mislukt", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
            }
            else
            {
                _viewModel.StatusNotification = result.Message ?? "DockerManager is up-to-date.";
                MessageBox.Show(result.Message ?? "DockerManager is al up-to-date.", "Zoeken naar Updates", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Fout bij controleren op updates: {ex.Message}", "Update Fout", MessageBoxButton.OK, MessageBoxImage.Error);
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

    private void OpenActionsMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.ContextMenu != null)
        {
            element.ContextMenu.PlacementTarget = element;
            element.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            element.ContextMenu.IsOpen = true;
        }
    }

    private async Task HandleBackupVolumesAsync(ProfileModel profile)
    {
        var defaultFileName = $"Backup_{profile.Name}_{DateTime.Now:yyyyMMdd_HHmm}.zip";
        var sfd = new Microsoft.Win32.SaveFileDialog
        {
            Title = $"Back-up Opslaan voor Profiel '{profile.Name}'",
            Filter = "ZIP-archief (*.zip)|*.zip|Alle bestanden (*.*)|*.*",
            FileName = defaultFileName,
            DefaultExt = ".zip"
        };

        if (sfd.ShowDialog(this) == true)
        {
            var zipPath = sfd.FileName;
            _viewModel.StatusNotification = "💾 Back-up maken...";

            var progress = new Progress<(string message, double percentage)>(p =>
            {
                _viewModel.StatusNotification = $"💾 {p.message}";
            });

            var result = await _backupService.CreateBackupAsync(profile, zipPath, progress);

            if (result.Success)
            {
                _viewModel.StatusNotification = $"✅ Back-up gereed: {System.IO.Path.GetFileName(zipPath)} ({result.FilesCount} bestanden, {result.TotalBytes / 1024.0 / 1024.0:F2} MB)";

                var msg = $"De back-up van profiel '{profile.Name}' is succesvol opgeslagen!\n\n" +
                          $"Locatie: {zipPath}\n" +
                          $"Aantal bestanden: {result.FilesCount}\n" +
                          $"Omvang: {result.TotalBytes / 1024.0 / 1024.0:F2} MB\n\n" +
                          (result.Warnings.Count > 0 ? $"Waarschuwingen:\n{string.Join("\n", result.Warnings.Take(5))}\n\n" : "") +
                          "Wil je de map met de back-up nu openen in Windows Verkenner?";

                var res = MessageBox.Show(msg, "Back-up Voltooid", MessageBoxButton.YesNo, MessageBoxImage.Information);
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
                _viewModel.StatusNotification = $"❌ Fout bij back-up: {result.ErrorMessage}";
                MessageBox.Show($"Fout bij het maken van de back-up:\n\n{result.ErrorMessage}", "Back-up Mislukt", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private async Task HandleRestoreBackupAsync()
    {
        var ofd = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Kies een ZIP Back-up om terug te zetten",
            Filter = "ZIP-archief (*.zip)|*.zip|Alle bestanden (*.*)|*.*",
            DefaultExt = ".zip"
        };

        if (ofd.ShowDialog(this) == true)
        {
            var zipPath = ofd.FileName;
            var confirm = MessageBox.Show(
                $"Weet je zeker dat je de bestanden uit deze back-up wilt terugzetten?\n\n" +
                $"Bestand: {zipPath}\n\n" +
                $"Let op: Bestaande configuratie- en databestanden van de containers kunnen worden overschreven.",
                "Back-up Terugzetten Bevestigen",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (confirm != MessageBoxResult.Yes) return;

            _viewModel.StatusNotification = "📥 Back-up terugzetten...";

            var progress = new Progress<(string message, double percentage)>(p =>
            {
                _viewModel.StatusNotification = $"📥 {p.message}";
            });

            var result = await _backupService.RestoreBackupAsync(zipPath, _viewModel.SelectedProfile, overwriteExisting: true, progress);

            if (result.Success)
            {
                _viewModel.StatusNotification = $"✅ Terugzetten voltooid: {result.RestoredFilesCount} bestanden teruggezet ({result.RestoredBytes / 1024.0 / 1024.0:F2} MB)";

                var msg = $"De back-up is succesvol teruggezet!\n\n" +
                          $"Aantal herstelde bestanden: {result.RestoredFilesCount}\n" +
                          $"Omvang: {result.RestoredBytes / 1024.0 / 1024.0:F2} MB\n\n" +
                          (result.Warnings.Count > 0 ? $"Waarschuwingen:\n{string.Join("\n", result.Warnings.Take(5))}\n\n" : "") +
                          "Wil je de containerstatussen nu verversen?";

                var res = MessageBox.Show(msg, "Back-up Teruggezet", MessageBoxButton.YesNo, MessageBoxImage.Information);
                if (res == MessageBoxResult.Yes)
                {
                    await _viewModel.RefreshAllCardStatusesAsync();
                }
            }
            else
            {
                _viewModel.StatusNotification = $"❌ Fout bij terugzetten: {result.ErrorMessage}";
                MessageBox.Show($"Fout bij het terugzetten van de back-up:\n\n{result.ErrorMessage}", "Terugzetten Mislukt", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private async void ImportComposeMenu_Click(object sender, RoutedEventArgs e)
    {
        var currentProfileName = _viewModel.SelectedProfile?.Name ?? "Werk";
        var vm = new ImportComposeViewModel(_composeImporterService, currentProfileName);
        var dialog = new ImportComposeDialog(vm) { Owner = this };

        if (dialog.ShowDialog() == true && vm.IsConfirmed && vm.ResultServices.Count > 0)
        {
            if (vm.TargetIsNewProfile)
            {
                var newProfile = new ProfileModel
                {
                    Name = vm.TargetProfileName,
                    Description = $"Geïmporteerd vanuit Docker Compose ({DateTime.Now:yyyy-MM-dd HH:mm})",
                    AutoStopPreviousOnSwitch = false
                };
                foreach (var svc in vm.ResultServices)
                {
                    newProfile.Services.Add(svc);
                }

                await _gitHubProfileService.SaveProfileLocallyAsync(newProfile);
                await _viewModel.LoadProfilesAsync(forceRefresh: true);
                _viewModel.SelectedProfile = _viewModel.Profiles.FirstOrDefault(p => p.Name.Equals(newProfile.Name, StringComparison.OrdinalIgnoreCase)) ?? _viewModel.Profiles.FirstOrDefault();
                _viewModel.StatusNotification = $"🎉 Nieuw profiel '{newProfile.Name}' aangemaakt met {vm.ResultServices.Count} container(s)!";
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

                    await _gitHubProfileService.SaveProfileLocallyAsync(active);
                    _viewModel.RebuildServiceCards();
                    _ = _viewModel.RefreshAllCardStatusesAsync();
                    _viewModel.StatusNotification = $"🎉 {addedCount} container(s) toegevoegd aan profiel '{active.Name}'!";
                }
            }
        }
    }

    private void ExportComposeMenu_Click(object sender, RoutedEventArgs e)
    {
        var profile = _viewModel.SelectedProfile;
        if (profile == null || profile.Services == null || profile.Services.Count == 0)
        {
            MessageBox.Show(LocalizationService.Instance["Export_Empty"], "Docker Compose Export", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var saveDialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Docker Compose exporteren",
            FileName = "docker-compose.yml",
            DefaultExt = ".yml",
            Filter = "YAML bestanden (*.yml;*.yaml)|*.yml;*.yaml|Alle bestanden (*.*)|*.*"
        };

        if (saveDialog.ShowDialog() == true)
        {
            var success = _composeExporterService.ExportToFile(profile, saveDialog.FileName, out var err);
            if (success)
            {
                _viewModel.StatusNotification = $"💾 Profiel '{profile.Name}' succesvol geëxporteerd naar '{System.IO.Path.GetFileName(saveDialog.FileName)}'!";
                MessageBox.Show(LocalizationService.Instance["Export_Success"], "Docker Compose Export", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show($"Fout bij exporteren: {err}", "Fout", MessageBoxButton.OK, MessageBoxImage.Error);
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
        SaveWindowBounds();
        base.OnClosed(e);
        if (_notifyIcon != null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }
    }
}
