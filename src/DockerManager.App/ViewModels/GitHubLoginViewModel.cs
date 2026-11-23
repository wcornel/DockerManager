using System.Diagnostics;
using System.Threading;
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
    private CancellationTokenSource? _validationCts;

    [ObservableProperty]
    private string _tokenInput = string.Empty;

    [ObservableProperty]
    private string _statusMessage = LocalizationService.Instance.Get("GitHubLogin_DefaultPrompt");

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
        StatusMessage = !string.IsNullOrWhiteSpace(existingToken)
            ? LocalizationService.Instance.Get("GitHubLogin_ExistingTokenHint")
            : LocalizationService.Instance.Get("GitHubLogin_DefaultPrompt");
    }

    [RelayCommand]
    private void OpenGitHubGenerateTokenPage()
    {
        var loc = LocalizationService.Instance;
        try
        {
            // Opens GitHub new token page with pre-selected scopes for repo and packages!
            var url = "https://github.com/settings/tokens/new?scopes=repo,read:packages,write:packages&description=DockerManager";
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
            StatusMessage = loc.Get("GitHubLogin_BrowserOpened");
        }
        catch (Exception ex)
        {
            StatusMessage = loc.Get("GitHubLogin_BrowserFailed", ex.Message);
        }
    }

    [RelayCommand]
    private async Task ValidateAndSaveTokenAsync()
    {
        var loc = LocalizationService.Instance;
        if (string.IsNullOrWhiteSpace(TokenInput))
        {
            StatusMessage = loc.Get("GitHubLogin_EmptyToken");
            return;
        }

        CancelValidation();
        var cts = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _validationCts, cts);
        if (previous != null)
        {
            try { previous.Cancel(); } catch { }
            try { previous.Dispose(); } catch { }
        }

        var ct = cts.Token;
        IsLoading = true;
        StatusMessage = loc.Get("GitHubLogin_Validating");

        try
        {
            var trimmedToken = TokenInput.Trim();
            var username = await _authService.FetchAuthenticatedUsernameAsync(trimmedToken, ct);
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(username))
            {
                StatusMessage = loc.Get("GitHubLogin_InvalidToken");
                return;
            }

            // Save encrypted token via Windows DPAPI
            _credentialService.SaveGitHubToken(trimmedToken);
            _settingsService.Settings.HasGitHubToken = true;
            _settingsService.Settings.LoggedInUsername = username;
            if (string.IsNullOrWhiteSpace(_settingsService.Settings.GitHubRepoOwner))
            {
                _settingsService.Settings.GitHubRepoOwner = username;
            }
            _settingsService.Save();

            LoggedInUser = username;
            IsSuccess = true;
            LoginSucceeded = true;
            StatusMessage = loc.Get("GitHubLogin_Success", username);

            await Task.Delay(1200, ct);
            RequestClose?.Invoke();
        }
        catch (OperationCanceledException)
        {
            // Validation cancelled via Cancel or window close
        }
        catch (Exception ex)
        {
            StatusMessage = loc.Get("GitHubLogin_Error", ex.Message);
        }
        finally
        {
            if (Interlocked.CompareExchange(ref _validationCts, null, cts) == cts)
            {
                try { cts.Dispose(); } catch { }
            }
            IsLoading = false;
        }
    }

    public void CancelValidation()
    {
        var cts = Interlocked.Exchange(ref _validationCts, null);
        if (cts != null)
        {
            try { cts.Cancel(); } catch { }
            try { cts.Dispose(); } catch { }
        }
    }

    [RelayCommand]
    private void PasteFromClipboard()
    {
        try
        {
            var text = Helpers.SafeClipboardHelper.GetText();
            if (!string.IsNullOrWhiteSpace(text))
            {
                TokenInput = text.Trim();
                StatusMessage = LocalizationService.Instance.Get("GitHubLogin_Pasted");
            }
        }
        catch { }
    }

    [RelayCommand]
    private void Cancel()
    {
        CancelValidation();
        RequestClose?.Invoke();
    }
}
