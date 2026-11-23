using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DockerManager.App.Models;
using DockerManager.App.Services;

namespace DockerManager.App.ViewModels;

public partial class GeneralSettingsTabViewModel : SettingsTabViewModel
{
    private readonly ISettingsService _settingsService;
    private readonly ICredentialService _credentialService;
    private readonly IGitHubAuthService _gitHubAuthService;
    private readonly IGitHubProfileService _gitHubProfileService;

    [ObservableProperty]
    private string _selectedLanguage = "auto";

    partial void OnSelectedLanguageChanged(string value)
    {
        OnPropertyChanged(nameof(SelectedLanguageDisplay));
    }

    public string[] AvailableLanguages { get; } = new[] { "Systeemtaal / System Default", "Nederlands (NL)", "English (EN)" };

    public string SelectedLanguageDisplay
    {
        get => SelectedLanguage switch
        {
            "nl" => "Nederlands (NL)",
            "en" => "English (EN)",
            _ => "Systeemtaal / System Default"
        };
        set
        {
            SelectedLanguage = value switch
            {
                "Nederlands (NL)" => "nl",
                "English (EN)" => "en",
                _ => "auto"
            };
            OnPropertyChanged();
        }
    }

    [ObservableProperty]
    private bool _autoCheckUpdatesOnStart = true;

    [ObservableProperty]
    private bool _autoCheckImageUpdates = true;

    [ObservableProperty]
    private string _updateCheckFrequency = "Dagelijks";

    public string[] AvailableFrequencies { get; } = new[] { "Dagelijks", "Wekelijks", "Handmatig" };

    [ObservableProperty]
    private bool _defaultKeepRunningOnSwitch = true;

    [ObservableProperty]
    private bool _promptToStopPreviousProfile;

    // GitHub
    [ObservableProperty]
    private string _loggedInUsername = string.Empty;

    [ObservableProperty]
    private bool _isLoggedIn;

    [ObservableProperty]
    private string _gitHubRepoOwner = string.Empty;

    [ObservableProperty]
    private string _gitHubRepoName = string.Empty;

    [ObservableProperty]
    private string _gitHubTestStatus = string.Empty;

    [ObservableProperty]
    private bool _isGitHubTesting;

    public event Action? RequestOpenGitHubLogin;

    public GeneralSettingsTabViewModel(
        ISettingsService settingsService,
        ICredentialService credentialService,
        IGitHubAuthService gitHubAuthService,
        IGitHubProfileService gitHubProfileService)
    {
        _settingsService = settingsService;
        _credentialService = credentialService;
        _gitHubAuthService = gitHubAuthService;
        _gitHubProfileService = gitHubProfileService;

        Header = LocalizationService.Instance.Get("Settings_TabGeneral");

        LoadFromSettings();
    }

    public void LoadFromSettings()
    {
        var s = _settingsService.Settings;
        SelectedLanguage = s.Language;
        AutoCheckUpdatesOnStart = s.AutoCheckUpdatesOnStart;
        AutoCheckImageUpdates = s.AutoCheckImageUpdates;
        UpdateCheckFrequency = s.UpdateCheckFrequency;
        DefaultKeepRunningOnSwitch = s.DefaultKeepRunningOnSwitch;
        PromptToStopPreviousProfile = s.PromptToStopPreviousProfile;

        GitHubRepoOwner = s.GitHubRepoOwner;
        GitHubRepoName = s.GitHubRepoName;

        RefreshLoginStatus();
    }

    public void RefreshLoginStatus()
    {
        var token = _credentialService.GetGitHubToken();
        IsLoggedIn = !string.IsNullOrEmpty(token);
        LoggedInUsername = !string.IsNullOrWhiteSpace(_settingsService.Settings.LoggedInUsername)
            ? _settingsService.Settings.LoggedInUsername
            : (IsLoggedIn ? LocalizationService.Instance.Get("Settings_GitHubUserFallback") : string.Empty);
    }

    [RelayCommand]
    private void OpenGitHubLogin()
    {
        RequestOpenGitHubLogin?.Invoke();
    }

    [RelayCommand]
    private void LogoutGitHub()
    {
        _credentialService.SaveGitHubToken(string.Empty);
        _settingsService.Settings.HasGitHubToken = false;
        _settingsService.Settings.LoggedInUsername = string.Empty;
        _settingsService.Save();
        RefreshLoginStatus();
        GitHubTestStatus = LocalizationService.Instance.Get("Settings_GitHubLoggedOut");
    }

    [RelayCommand]
    private async Task TestGitHubConnectionAsync()
    {
        if (IsGitHubTesting) return;
        IsGitHubTesting = true;
        GitHubTestStatus = LocalizationService.Instance.Get("Settings_TestingConnection");

        try
        {
            var (success, message) = await _gitHubProfileService.TestGitHubConnectionAsync(
                overrideOwner: GitHubRepoOwner,
                overrideRepo: GitHubRepoName);
            GitHubTestStatus = success ? $"✅ {message}" : $"❌ {message}";
        }
        catch (Exception ex)
        {
            GitHubTestStatus = $"❌ {ex.Message}";
        }
        finally
        {
            IsGitHubTesting = false;
        }
    }

    public void ApplyToSettings()
    {
        var s = _settingsService.Settings;
        s.Language = SelectedLanguage;
        s.AutoCheckUpdatesOnStart = AutoCheckUpdatesOnStart;
        s.AutoCheckImageUpdates = AutoCheckImageUpdates;
        s.UpdateCheckFrequency = UpdateCheckFrequency;
        s.DefaultKeepRunningOnSwitch = DefaultKeepRunningOnSwitch;
        s.PromptToStopPreviousProfile = PromptToStopPreviousProfile;

        s.GitHubRepoOwner = GitHubRepoOwner.Trim();
        s.GitHubRepoName = GitHubRepoName.Trim();
    }
}
