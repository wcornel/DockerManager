using System.IO;
using System.Linq;
using System.Windows;
using DockerManager.App.Models;
using DockerManager.App.Services;

namespace DockerManager.App.Views;

public class ProfileTemplateOption
{
    public string DisplayName { get; set; } = string.Empty;
    public ProfileModel? SourceProfile { get; set; }
    public bool IsGlobalDefaults { get; set; }
    public override string ToString() => DisplayName;
}

public partial class AddProfileDialog : Window
{
    private readonly IEnumerable<string> _existingNames;
    private readonly ISettingsService? _settingsService;
    private readonly ICredentialService? _credentialService;

    public ProfileModel? CreatedProfile { get; private set; }

    public AddProfileDialog(
        IEnumerable<string> existingProfileNames,
        IEnumerable<ProfileModel>? existingProfiles = null,
        ISettingsService? settingsService = null,
        ICredentialService? credentialService = null)
    {
        InitializeComponent();
        _existingNames = existingProfileNames ?? Enumerable.Empty<string>();
        _settingsService = settingsService;
        _credentialService = credentialService;

        var options = new List<ProfileTemplateOption>
        {
            new() { DisplayName = "📄 Schoon profiel (Geen instellingen)", SourceProfile = null, IsGlobalDefaults = false }
        };

        if (_settingsService != null)
        {
            options.Add(new() { DisplayName = "⚙️ Globale Standaard Instellingen (App)", SourceProfile = null, IsGlobalDefaults = true });
        }

        if (existingProfiles != null)
        {
            foreach (var ep in existingProfiles)
            {
                options.Add(new() { DisplayName = $"📄 Kopiëren van: {ep.Name}", SourceProfile = ep, IsGlobalDefaults = false });
            }
        }

        InheritSettingsComboBox.ItemsSource = options;
        InheritSettingsComboBox.SelectedIndex = options.Count > 1 ? 1 : 0;

        Loaded += (_, _) => ProfileNameTextBox.Focus();
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        var name = ProfileNameTextBox.Text.Trim();
        var desc = DescriptionTextBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show("Voer een geldige profielnaam in.", "Naam vereist", MessageBoxButton.OK, MessageBoxImage.Warning);
            ProfileNameTextBox.Focus();
            return;
        }

        var invalidChars = Path.GetInvalidFileNameChars();
        if (name.IndexOfAny(invalidChars) >= 0)
        {
            MessageBox.Show("De profielnaam mag geen ongeldige bestandstekens bevatten (zoals / \\ : * ? < > |).", "Ongeldige tekens", MessageBoxButton.OK, MessageBoxImage.Warning);
            ProfileNameTextBox.Focus();
            return;
        }

        if (_existingNames.Any(n => n.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show($"Er bestaat al een profiel met de naam '{name}'. Kies een andere naam.", "Profiel bestaat al", MessageBoxButton.OK, MessageBoxImage.Warning);
            ProfileNameTextBox.Focus();
            return;
        }

        CreatedProfile = new ProfileModel
        {
            Name = name,
            Description = string.IsNullOrWhiteSpace(desc) ? $"Profiel {name}" : desc,
            Services = new List<ServiceDefinition>()
        };

        if (InheritSettingsComboBox.SelectedItem is ProfileTemplateOption selected)
        {
            if (selected.IsGlobalDefaults && _settingsService != null)
            {
                var s = _settingsService.Settings;
                CreatedProfile.Cloudflare = new ProfileCloudflareConfig
                {
                    AccountId = s.CloudflareAccountId,
                    TunnelId = s.CloudflareTunnelId,
                    Domain = s.CloudflareDomain,
                    ApiToken = _credentialService?.GetCloudflareApiToken() ?? string.Empty,
                    TunnelToken = s.CloudflareTunnelToken
                };
                CreatedProfile.Registry = new ProfileRegistryConfig
                {
                    Server = "ghcr.io",
                    Namespace = !string.IsNullOrWhiteSpace(s.LoggedInUsername) ? s.LoggedInUsername : s.GitHubRepoOwner,
                    Username = s.LoggedInUsername,
                    Password = _credentialService?.GetGitHubToken() ?? string.Empty
                };
            }
            else if (selected.SourceProfile != null)
            {
                if (selected.SourceProfile.Cloudflare != null)
                {
                    CreatedProfile.Cloudflare = selected.SourceProfile.Cloudflare.Clone();
                }
                if (selected.SourceProfile.Registry != null)
                {
                    CreatedProfile.Registry = selected.SourceProfile.Registry.Clone();
                }
            }
        }

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
