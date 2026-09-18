using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DockerManager.App.Services;

namespace DockerManager.App.ViewModels;

public partial class GitHubLoginViewModel : ObservableObject
{
    private readonly IGitHubAuthService _authService;
    private readonly ICredentialService _credentialService;
    private readonly ISettingsService _settingsService;

    [ObservableProperty]
    private string _tokenInput = string.Empty;

    [ObservableProperty]
    private string _statusMessage = "Klik op 'Genereer Token op GitHub' om direct een token met de juiste permissies aan te maken, of plak hieronder een bestaand token:";

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isSuccess;

    [ObservableProperty]
    private string _loggedInUser = string.Empty;

    public event Action? RequestClose;
    public bool LoginSucceeded { get; private set; }

    public GitHubLoginViewModel(
        IGitHubAuthService authService,
        ICredentialService credentialService,
        ISettingsService settingsService)
    {
        _authService = authService;
        _credentialService = credentialService;
        _settingsService = settingsService;

        var existingToken = _credentialService.GetGitHubToken();
        if (!string.IsNullOrWhiteSpace(existingToken))
        {
            TokenInput = existingToken;
        }
    }

    [RelayCommand]
    private void OpenGitHubGenerateTokenPage()
    {
        try
        {
            // Opens GitHub new token page with pre-selected scopes for repo and packages!
            var url = "https://github.com/settings/tokens/new?scopes=repo,read:packages,write:packages&description=DockerManager";
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
            StatusMessage = "GitHub geopend! Klik onderaan op 'Generate token', kopieer het token en plak het hieronder.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Kon browser niet automatisch openen: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ValidateAndSaveTokenAsync()
    {
        if (string.IsNullOrWhiteSpace(TokenInput))
        {
            StatusMessage = "⚠️ Voer eerst een GitHub token in (begint meestal met 'ghp_' of 'github_pat_').";
            return;
        }

        IsLoading = true;
        StatusMessage = "Token controleren bij GitHub...";

        try
        {
            var username = await _authService.FetchAuthenticatedUsernameAsync(TokenInput.Trim());
            if (string.IsNullOrWhiteSpace(username))
            {
                StatusMessage = "❌ Ongeldig token of geen verbinding met GitHub. Controleer of het token juist is gekopieerd.";
                IsLoading = false;
                return;
            }

            // Save encrypted token via Windows DPAPI
            _credentialService.SaveGitHubToken(TokenInput.Trim());
            _settingsService.Settings.HasGitHubToken = true;
            _settingsService.Settings.LoggedInUsername = username;
            _settingsService.Settings.GitHubRepoOwner = username;
            _settingsService.Save();

            LoggedInUser = username;
            IsSuccess = true;
            LoginSucceeded = true;
            StatusMessage = $"✅ Succesvol gekoppeld als @{username}!";

            await Task.Delay(1200);
            RequestClose?.Invoke();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Fout bij valideren: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void PasteFromClipboard()
    {
        try
        {
            var text = Clipboard.GetText();
            if (!string.IsNullOrWhiteSpace(text))
            {
                TokenInput = text.Trim();
                StatusMessage = "Token geplakt! Klik nu op 'Verifieer & Inloggen'.";
            }
        }
        catch { }
    }

    [RelayCommand]
    private void Cancel()
    {
        RequestClose?.Invoke();
    }
}
