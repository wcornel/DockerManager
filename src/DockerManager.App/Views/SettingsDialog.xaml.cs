using System.Windows;
using DockerManager.App.Services;
using DockerManager.App.ViewModels;

namespace DockerManager.App.Views;

public partial class SettingsDialog : Window
{
    private readonly SettingsViewModel _viewModel;
    private readonly IGitHubAuthService _gitHubAuthService;
    private readonly ICredentialService _credentialService;
    private readonly ISettingsService _settingsService;

    public SettingsDialog(
        SettingsViewModel viewModel,
        IGitHubAuthService gitHubAuthService,
        ICredentialService credentialService,
        ISettingsService settingsService)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _gitHubAuthService = gitHubAuthService;
        _credentialService = credentialService;
        _settingsService = settingsService;
        DataContext = _viewModel;
        _viewModel.RequestClose += () => DialogResult = _viewModel.IsSaved;
        _viewModel.RequestOpenGitHubLogin += OpenGitHubLoginDialog;
    }

    private void OpenGitHubLoginDialog()
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
                _viewModel.LoadFromSettings();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Fout bij starten van GitHub Login: {ex.Message}", "Fout", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _viewModel.SaveCommand.Execute(null);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Fout bij opslaan: {ex.Message}", "Fout", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
