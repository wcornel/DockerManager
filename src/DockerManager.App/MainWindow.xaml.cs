using System.Reflection;
using System.Windows;
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

    public MainWindow()
    {
        InitializeComponent();

        _credentialService = new CredentialService();
        _settingsService = new SettingsService(_credentialService);
        _cloudflareService = new CloudflareService(_settingsService, _credentialService);
        _gitHubProfileService = new GitHubProfileService(_settingsService, _credentialService);
        _dockerService = new DockerService(_settingsService, _credentialService);
        _gitHubAuthService = new GitHubAuthService();
        _backupService = new BackupService(_settingsService);
        _dotnetScannerService = new DotnetAppScannerService();
        _pythonScannerService = new PythonAppScannerService();
        _dotnetPublisherService = new DotnetPublisherService(_settingsService, _credentialService);
        _appUpdateService = new AppUpdateService(_settingsService, _credentialService);
        _composeImporterService = new DockerComposeImporterService();
        _composeExporterService = new DockerComposeExporterService();

        _viewModel = new MainViewModel(_settingsService, _credentialService, _gitHubProfileService, _dockerService, _cloudflareService);
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
            _ = CheckForUpdatesSilentlyOnStartupAsync();
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

    private void ShowAddProfileDialog()
    {
        try
        {
            var existingNames = _viewModel.Profiles.Select(p => p.Name);
            var dialog = new AddProfileDialog(existingNames, _viewModel.Profiles, _settingsService, _credentialService)
            {
                Owner = this
            };

            if (dialog.ShowDialog() == true && dialog.CreatedProfile != null)
            {
                var newProfile = dialog.CreatedProfile;
                _viewModel.Profiles.Add(newProfile);
                _viewModel.SelectedProfile = newProfile;
                _ = _viewModel.SaveCurrentProfileAsync();

                _viewModel.StatusNotification = $"✨ Nieuw profiel '{newProfile.Name}' aangemaakt en geselecteerd!";
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Fout bij aanmaken van profiel: {ex.Message}", "Fout", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void PublishDotnetMenu_Click(object sender, RoutedEventArgs e)
    {
        ShowPublishAppDialog(AppFrameworkType.Dotnet);
    }

    private void PublishPythonMenu_Click(object sender, RoutedEventArgs e)
    {
        ShowPublishAppDialog(AppFrameworkType.Python);
    }

    private void ShowPublishAppDialog(AppFrameworkType framework)
    {
        try
        {
            var publishVm = new PublishDotnetAppViewModel(
                _dotnetScannerService,
                _pythonScannerService,
                _dotnetPublisherService,
                _settingsService,
                _credentialService,
                _cloudflareService,
                framework,
                _viewModel.SelectedProfile);

            var dialog = new PublishDotnetAppDialog(publishVm)
            {
                Owner = this
            };

            if (dialog.ShowDialog() == true && publishVm.CreatedServiceDefinition != null)
            {
                if (_viewModel.SelectedProfile != null)
                {
                    if (publishVm.EnsureCloudflaredInProfile)
                    {
                        _cloudflareService.EnsureCloudflaredServiceInProfile(_viewModel.SelectedProfile);
                    }

                    var existingIdx = _viewModel.SelectedProfile.Services.FindIndex(s => s.Id.Equals(publishVm.CreatedServiceDefinition.Id, StringComparison.OrdinalIgnoreCase));
                    if (existingIdx >= 0)
                    {
                        _viewModel.SelectedProfile.Services[existingIdx] = publishVm.CreatedServiceDefinition;
                    }
                    else
                    {
                        _viewModel.SelectedProfile.Services.Add(publishVm.CreatedServiceDefinition);
                    }

                    _viewModel.RebuildServiceCards();
                    _ = _viewModel.SaveCurrentProfileAsync();
                    _ = _viewModel.RefreshAllCardStatusesAsync();

                    _viewModel.StatusNotification = $"✨ App '{publishVm.CreatedServiceDefinition.DisplayName}' toegevoegd aan profiel '{_viewModel.SelectedProfile.Name}'!";
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Fout bij openen van Publiceren Wizard: {ex.Message}", "Fout", MessageBoxButton.OK, MessageBoxImage.Error);
        }
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
              $"Author: W. Cornelissen\n\n" +
              $"License: MIT License (Open Source)\n" +
              $"GitHub: https://github.com/wcornel/DockerManager\n\n" +
              $"Easily manage all your containers, ports, volumes, and profiles."
            : $"DockerManager — Profile & Container Hub\n" +
              $"Versie: {versionStr}\n" +
              $"Auteur: W. Cornelissen\n\n" +
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

    private void ShowGitHubLoginDialog()
    {
        try
        {
            var loginVm = new GitHubLoginViewModel(_gitHubAuthService, _credentialService, _settingsService);
            var dialog = new GitHubLoginDialog(loginVm)
            {
                Owner = this
            };
            dialog.ShowDialog();

            if (loginVm.LoginSucceeded)
            {
                _viewModel.RefreshAuthStatus();
                _ = _viewModel.LoadProfilesAsync(forceRefresh: true);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Fout bij inloggen met GitHub: {ex.Message}", "Fout", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ShowSettingsDialog(string? targetServerId = null, string? targetProfileName = null)
    {
        try
        {
            var serverId = targetServerId ?? _viewModel.SelectedServer?.Id;
            var profileName = targetProfileName ?? _viewModel.SelectedProfile?.Name;
            var settingsVm = new SettingsViewModel(
                _settingsService,
                _credentialService,
                _gitHubProfileService,
                _dockerService,
                _cloudflareService,
                _gitHubAuthService,
                serverId,
                profileName);
            var dialog = new SettingsDialog(settingsVm, _gitHubAuthService, _credentialService, _settingsService)
            {
                Owner = this
            };

            if (dialog.ShowDialog() == true)
            {
                _viewModel.RefreshAuthStatus();
                _viewModel.LoadServersFromSettings();
                // Settings changed: re-check docker status and refresh profiles
                _ = _viewModel.CheckDockerStatusAsync();
                _ = _viewModel.LoadProfilesAsync(forceRefresh: false);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Fout bij openen van instellingen: {ex.Message}", "Fout", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ShowEditServiceDialog(ServiceCardViewModel cardVm)
    {
        try
        {
            var editVm = new EditServiceViewModel(cardVm.Service, _dockerService, _cloudflareService, _settingsService, _viewModel.SelectedProfile);
            var dialog = new EditServiceDialog(editVm)
            {
                Owner = this
            };

            if (dialog.ShowDialog() == true)
            {
                if (editVm.EnsureCloudflaredInProfile && _viewModel.SelectedProfile != null)
                {
                    _cloudflareService.EnsureCloudflaredServiceInProfile(_viewModel.SelectedProfile);
                }

                var updated = editVm.ToServiceDefinition();
                cardVm.UpdateService(updated);

                // Update in profile model
                if (_viewModel.SelectedProfile != null)
                {
                    var idx = _viewModel.SelectedProfile.Services.FindIndex(s => s.Id == cardVm.Service.Id);
                    if (idx >= 0)
                    {
                        _viewModel.SelectedProfile.Services[idx] = updated;
                    }
                    _viewModel.RebuildServiceCards();
                    _ = _viewModel.SaveCurrentProfileAsync();
                }

                _ = cardVm.RefreshStatusAsync();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Fout bij bewerken van container: {ex.Message}", "Fout", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ShowAddServiceDialog()
    {
        if (_viewModel.SelectedProfile == null)
        {
            MessageBox.Show("Selecteer eerst een profiel om een container aan toe te voegen.", "Geen profiel", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var editVm = new EditServiceViewModel(null, _dockerService, _cloudflareService, _settingsService, _viewModel.SelectedProfile);
            var dialog = new EditServiceDialog(editVm)
            {
                Owner = this
            };

            if (dialog.ShowDialog() == true && _viewModel.SelectedProfile != null)
            {
                if (editVm.EnsureCloudflaredInProfile)
                {
                    _cloudflareService.EnsureCloudflaredServiceInProfile(_viewModel.SelectedProfile);
                }

                var newSvc = editVm.ToServiceDefinition();
                _viewModel.SelectedProfile.Services.Add(newSvc);
                _viewModel.RebuildServiceCards();
                _ = _viewModel.SaveCurrentProfileAsync();
                _ = _viewModel.RefreshAllCardStatusesAsync();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Fout bij toevoegen van container: {ex.Message}", "Fout", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ShowProfileSettingsDialog(ProfileModel profile)
    {
        ShowSettingsDialog(_viewModel.SelectedServer?.Id, profile?.Name);
    }

    private Task<bool> ConfirmDeleteProfileAsync(ProfileModel profile)
    {
        var result = MessageBox.Show(
            $"Weet je zeker dat je profiel '{profile.Name}' definitief wilt verwijderen?\n\nDe actieve containers van dit profiel worden gestopt en het configuratiebestand wordt verwijderd.",
            "Profiel Verwijderen",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        return Task.FromResult(result == MessageBoxResult.Yes);
    }

    private void ShowLogViewerDialog(ServiceCardViewModel cardVm)
    {
        try
        {
            var logVm = new LogViewerViewModel(cardVm.ContainerName, _dockerService, cardVm.ActivityLogs);
            var dialog = new LogViewerDialog(logVm)
            {
                Owner = this
            };
            dialog.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Fout bij openen van log-venster: {ex.Message}", "Fout", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ShowPortConflictDialog()
    {
        try
        {
            var server = _viewModel.SelectedServer;
            var serverName = server?.Name ?? "Lokale Docker Desktop";
            var isRemote = server != null && server.HostType.Equals("Tcp", StringComparison.OrdinalIgnoreCase);
            var conflictVm = new PortConflictViewModel(_dockerService, _viewModel.Profiles, _viewModel.SelectedProfile?.Name ?? "", serverName, isRemote);
            var dialog = new PortConflictDialog(conflictVm)
            {
                Owner = this
            };
            dialog.ShowDialog();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Fout bij openen van poortconflict analyse: {ex.Message}", "Fout", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private Task<bool?> ShowProfileSwitchDialogAsync(string previousProfileName)
    {
        var tcs = new TaskCompletionSource<bool?>();
        Dispatcher.Invoke(() =>
        {
            try
            {
                var dialog = new ProfileSwitchDialog(previousProfileName)
                {
                    Owner = this
                };

                var res = dialog.ShowDialog();
                if (res == true)
                {
                    if (dialog.DontAskAgain)
                    {
                        _settingsService.Settings.PromptToStopPreviousProfile = false;
                        _settingsService.Settings.DefaultKeepRunningOnSwitch = !dialog.ShouldStopPrevious.GetValueOrDefault();
                        _settingsService.Save();
                    }

                    tcs.SetResult(dialog.ShouldStopPrevious);
                }
                else
                {
                    tcs.SetResult(null);
                }
            }
            catch
            {
                tcs.SetResult(null);
            }
        });
        return tcs.Task;
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

    private void InitializeTrayIcon()
    {
        try
        {
            var iconStream = Application.GetResourceStream(new Uri("pack://application:,,,/DockerManager.App;component/app.ico"))?.Stream;
            var icon = iconStream != null ? new System.Drawing.Icon(iconStream) : System.Drawing.SystemIcons.Application;

            _notifyIcon = new System.Windows.Forms.NotifyIcon
            {
                Icon = icon,
                Text = "DockerManager — Profile & Container Hub",
                Visible = true
            };

            BuildTrayContextMenu();
            LocalizationService.Instance.LanguageChanged += BuildTrayContextMenu;
            _notifyIcon.DoubleClick += (_, _) => RestoreWindow();
        }
        catch { }
    }

    private void BuildTrayContextMenu()
    {
        if (_notifyIcon == null) return;
        var contextMenu = new System.Windows.Forms.ContextMenuStrip();

        PopulateTrayContextMenu(contextMenu);

        contextMenu.Opening += (_, _) =>
        {
            PopulateTrayContextMenu(contextMenu);
        };

        _notifyIcon.ContextMenuStrip = contextMenu;
        UpdateTrayTooltip();
    }

    private void PopulateTrayContextMenu(System.Windows.Forms.ContextMenuStrip contextMenu)
    {
        if (_notifyIcon == null) return;
        var loc = LocalizationService.Instance;
        contextMenu.Items.Clear();

        var openItem = new System.Windows.Forms.ToolStripMenuItem(loc.Get("Tray_Open"), null, (_, _) => RestoreWindow());
        openItem.Font = new System.Drawing.Font(openItem.Font, System.Drawing.FontStyle.Bold);
        contextMenu.Items.Add(openItem);

        contextMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

        var currentServer = _viewModel.SelectedServer;
        var currentServerName = currentServer?.Name ?? "Lokaal";

        // Server menu item with dropdown
        var serverMenuItem = new System.Windows.Forms.ToolStripMenuItem($"{loc.Get("Tray_Server")}: {currentServerName}");
        serverMenuItem.Font = new System.Drawing.Font(serverMenuItem.Font, System.Drawing.FontStyle.Bold);

        foreach (var server in _viewModel.Servers)
        {
            var isCurrent = currentServer != null && server.Id.Equals(currentServer.Id, StringComparison.OrdinalIgnoreCase);
            var item = new System.Windows.Forms.ToolStripMenuItem((isCurrent ? "✓ " : "    ") + server.Name, null, (_, _) =>
            {
                if (!isCurrent)
                {
                    _viewModel.SelectedServer = server;
                    UpdateTrayTooltip();
                    _notifyIcon?.ShowBalloonTip(1500, "DockerManager", string.Format(loc.Get("Tray_SwitchedServer"), server.Name), System.Windows.Forms.ToolTipIcon.Info);
                }
            })
            {
                Checked = isCurrent
            };
            serverMenuItem.DropDownItems.Add(item);
        }
        contextMenu.Items.Add(serverMenuItem);

        // Profile menu item with dropdown
        var currentProfile = _viewModel.SelectedProfile;
        var currentProfileName = currentProfile?.Name ?? "(Geen profiel)";

        var profileMenuItem = new System.Windows.Forms.ToolStripMenuItem($"{loc.Get("Tray_Profile")}: {currentProfileName}");
        profileMenuItem.Font = new System.Drawing.Font(profileMenuItem.Font, System.Drawing.FontStyle.Bold);

        foreach (var profile in _viewModel.Profiles)
        {
            var isCurrent = currentProfile != null && profile.Name.Equals(currentProfile.Name, StringComparison.OrdinalIgnoreCase);
            var item = new System.Windows.Forms.ToolStripMenuItem((isCurrent ? "✓ " : "    ") + profile.Name, null, (_, _) =>
            {
                if (!isCurrent)
                {
                    _viewModel.SelectedProfile = profile;
                    UpdateTrayTooltip();
                    _notifyIcon?.ShowBalloonTip(1500, "DockerManager", string.Format(loc.Get("Tray_SwitchedProfile"), profile.Name), System.Windows.Forms.ToolTipIcon.Info);
                }
            })
            {
                Checked = isCurrent
            };
            profileMenuItem.DropDownItems.Add(item);
        }
        contextMenu.Items.Add(profileMenuItem);

        contextMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

        var startText = string.IsNullOrEmpty(currentProfileName) ? loc.Get("Tray_StartAll") : $"{loc.Get("Tray_StartAll")} ({currentProfileName})";
        var stopText = string.IsNullOrEmpty(currentProfileName) ? loc.Get("Tray_StopAll") : $"{loc.Get("Tray_StopAll")} ({currentProfileName})";
        var updateText = string.IsNullOrEmpty(currentProfileName) ? loc.Get("Tray_UpdateAll") : $"{loc.Get("Tray_UpdateAll")} ({currentProfileName})";
        var backupText = string.IsNullOrEmpty(currentProfileName) ? loc.Get("Tray_BackupVolumes") : $"{loc.Get("Tray_BackupVolumes")} ({currentProfileName})";

        contextMenu.Items.Add(new System.Windows.Forms.ToolStripMenuItem(startText, null, (_, _) => _ = _viewModel.StartAllCommand.ExecuteAsync(null)));
        contextMenu.Items.Add(new System.Windows.Forms.ToolStripMenuItem(stopText, null, (_, _) => _ = _viewModel.StopAllCommand.ExecuteAsync(null)));
        contextMenu.Items.Add(new System.Windows.Forms.ToolStripMenuItem(updateText, null, (_, _) => _ = _viewModel.UpdateAllCommand.ExecuteAsync(null)));
        contextMenu.Items.Add(new System.Windows.Forms.ToolStripMenuItem(backupText, null, (_, _) => _ = _viewModel.BackupVolumesCommand.ExecuteAsync(null)));
        contextMenu.Items.Add(new System.Windows.Forms.ToolStripMenuItem(loc.Get("Tray_CheckPorts"), null, (_, _) =>
        {
            RestoreWindow();
            _viewModel.CheckPortConflictsCommand.Execute(null);
        }));

        contextMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

        contextMenu.Items.Add(new System.Windows.Forms.ToolStripMenuItem(loc.Get("Tray_Exit"), null, (_, _) =>
        {
            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
            }
            Application.Current.Shutdown();
        }));

        UpdateTrayTooltip();
    }

    private void UpdateTrayTooltip()
    {
        if (_notifyIcon == null) return;
        var server = _viewModel.SelectedServer?.Name ?? "Docker";
        var profile = _viewModel.SelectedProfile?.Name ?? "Geen profiel";
        var text = $"DockerManager — {server} ({profile})";
        if (text.Length > 63)
        {
            text = text.Substring(0, 60) + "...";
        }
        _notifyIcon.Text = text;
    }

    public void RestoreWindow()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Focus();
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == WindowState.Minimized)
        {
            Hide();
            var tip = LocalizationService.Instance.Get("Tray_MinimizedTip");
            _notifyIcon?.ShowBalloonTip(1500, "DockerManager", tip, System.Windows.Forms.ToolTipIcon.Info);
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