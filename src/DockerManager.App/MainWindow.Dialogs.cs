using System.Windows;
using DockerManager.App.Helpers;
using DockerManager.App.Models;
using DockerManager.App.Services;
using DockerManager.App.ViewModels;
using DockerManager.App.Views;

namespace DockerManager.App;

public partial class MainWindow
{
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

            dialog.ShowDialog();
            if (publishVm.IsCompleted && publishVm.CreatedServiceDefinition != null)
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

                    _viewModel.StatusNotification = LocalizationService.Instance.Get("Publish_AddedNotification", publishVm.CreatedServiceDefinition.DisplayName, _viewModel.SelectedProfile.Name);
                }
            }
        }
        catch (Exception ex)
        {
            var loc = LocalizationService.Instance;
            MessageBox.Show(loc.Get("Publish_OpenError", ex.Message), loc.Get("General_ErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
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
            var loc = LocalizationService.Instance;
            MessageBox.Show(loc.Get("Settings_OpenError", ex.Message), loc.Get("General_ErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ShowEditServiceDialog(ServiceCardViewModel cardVm)
    {
        try
        {
            var originalService = cardVm.Service;
            var originalId = originalService.Id;
            var serviceCountBefore = _viewModel.SelectedProfile?.Services.Count ?? 0;
            var isRunning = cardVm.IsRunning;
            var editVm = new EditServiceViewModel(originalService, _dockerService, _cloudflareService, _settingsService, _viewModel.SelectedProfile)
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
                var targetCard = cardVm;

                // Update in profile model
                if (_viewModel.SelectedProfile != null)
                {
                    var services = _viewModel.SelectedProfile.Services;
                    var idx = services.IndexOf(originalService);
                    if (idx < 0)
                    {
                        idx = services.FindIndex(s => string.Equals(s.Id, originalId, StringComparison.OrdinalIgnoreCase));
                    }

                    if (idx >= 0)
                    {
                        services[idx] = updated;
                    }

                    if (services.Count != serviceCountBefore)
                    {
                        _viewModel.RebuildServiceCards();
                        targetCard = _viewModel.ServiceCards.FirstOrDefault(c => ReferenceEquals(c.Service, updated))
                            ?? _viewModel.ServiceCards.FirstOrDefault(c => string.Equals(c.Service.Id, updated.Id, StringComparison.OrdinalIgnoreCase))
                            ?? cardVm;
                        _ = _viewModel.RefreshAllCardStatusesAsync();
                    }
                    else
                    {
                        cardVm.UpdateService(updated);
                        _ = cardVm.RefreshStatusAsync();
                    }

                    _ = _viewModel.SaveCurrentProfileAsync();
                }
                else
                {
                    cardVm.UpdateService(updated);
                    _ = cardVm.RefreshStatusAsync();
                }

                if (isRunning && editVm.HasEnvironmentChanges())
                {
                    if (editVm.RestartRequested)
                    {
                        _ = targetCard.RestartCommand.ExecuteAsync(null);
                    }
                    else
                    {
                        var notice = LocalizationService.Instance.Get("EditService_EnvRestartCardNotice");
                        targetCard.StatusMessage = notice;
                        _viewModel.StatusNotification = $"⚠️ {updated.DisplayName}: {notice}";
                    }
                }
            }
        }
        catch (Exception ex)
        {
            var loc = LocalizationService.Instance;
            MessageBox.Show(loc.Get("EditService_EditError", ex.Message), loc.Get("General_ErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ShowAddServiceDialog()
    {
        if (_viewModel.SelectedProfile == null)
        {
            var loc = LocalizationService.Instance;
            MessageBox.Show(loc.Get("EditService_NoProfileMsg"), loc.Get("EditService_NoProfileTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
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
            var loc = LocalizationService.Instance;
            MessageBox.Show(loc.Get("EditService_AddError", ex.Message), loc.Get("General_ErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ShowProfileSettingsDialog(ProfileModel profile)
    {
        ShowSettingsDialog(_viewModel.SelectedServer?.Id, profile?.Name);
    }

    private Task<bool> ConfirmDeleteProfileAsync(ProfileModel profile)
    {
        var loc = LocalizationService.Instance;
        var result = MessageBox.Show(
            loc.Get("DeleteProfile_ConfirmBody", profile.Name),
            loc.Get("DeleteProfile_ConfirmTitle"),
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
            var loc = LocalizationService.Instance;
            MessageBox.Show(loc.Get("LogViewer_OpenError", ex.Message), loc.Get("General_ErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ShowPortConflictDialog()
    {
        try
        {
            var server = _viewModel.SelectedServer;
            var serverName = server?.Name ?? LocalizationService.Instance.Get("Ports_DefaultLocalServer");
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
            var loc = LocalizationService.Instance;
            MessageBox.Show(loc.Get("Ports_OpenError", ex.Message), loc.Get("General_ErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
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
