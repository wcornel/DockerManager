using System.Windows;
using DockerManager.App.Helpers;
using DockerManager.App.Models;
using DockerManager.App.Services;
using DockerManager.App.ViewModels;
using DockerManager.App.Views;

namespace DockerManager.App;

public partial class MainWindow
{
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

            var result = dialog.ShowDialog();
            _viewModel.RefreshAuthStatus();

            if (result == true)
            {
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
            var isRunning = cardVm.IsRunning;
            var editVm = new EditServiceViewModel(cardVm.Service, _dockerService, _cloudflareService, _settingsService, _viewModel.SelectedProfile)
            {
                IsContainerRunning = isRunning
            };
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
                    _ = _viewModel.SaveCurrentProfileAsync();
                }

                _ = cardVm.RefreshStatusAsync();

                if (isRunning && editVm.HasEnvironmentChanges())
                {
                    if (editVm.RestartRequested)
                    {
                        _ = cardVm.RestartCommand.ExecuteAsync(null);
                    }
                    else
                    {
                        var notice = LocalizationService.Instance.Get("EditService_EnvRestartCardNotice");
                        cardVm.StatusMessage = notice;
                        _viewModel.StatusNotification = $"⚠️ {updated.DisplayName}: {notice}";
                    }
                }
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

}
