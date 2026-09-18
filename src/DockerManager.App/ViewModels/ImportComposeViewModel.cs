using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DockerManager.App.Models;
using DockerManager.App.Services;

namespace DockerManager.App.ViewModels;

public partial class ImportComposeViewModel : ObservableObject
{
    private readonly IDockerComposeImporterService _importerService;

    [ObservableProperty]
    private string _filePath = string.Empty;

    [ObservableProperty]
    private string _yamlText = string.Empty;

    [ObservableProperty]
    private bool _importIntoActiveProfile = true;

    [ObservableProperty]
    private bool _createNewProfile;

    [ObservableProperty]
    private string _activeProfileName = "Werk";

    [ObservableProperty]
    private string _newProfileName = "Compose Stack";

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _hasParsedServices;

    public ObservableCollection<ServiceDefinition> ParsedServices { get; } = new();

    public List<ServiceDefinition> ResultServices { get; } = new();
    public bool IsConfirmed { get; private set; }
    public bool TargetIsNewProfile => CreateNewProfile;
    public string TargetProfileName => CreateNewProfile ? NewProfileName.Trim() : ActiveProfileName.Trim();

    public event Action? RequestClose;

    public ImportComposeViewModel(IDockerComposeImporterService importerService, string currentProfileName)
    {
        _importerService = importerService;
        ActiveProfileName = string.IsNullOrWhiteSpace(currentProfileName) ? "Actief Profiel" : currentProfileName;
    }

    [RelayCommand]
    private void BrowseFile()
    {
        using var dialog = new System.Windows.Forms.OpenFileDialog
        {
            Title = "Selecteer docker-compose YAML bestand",
            Filter = "Docker Compose bestanden (*.yml;*.yaml)|*.yml;*.yaml|Alle bestanden (*.*)|*.*",
            Multiselect = false
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            FilePath = dialog.FileName;
            try
            {
                YamlText = File.ReadAllText(FilePath);
                ParseYaml();
            }
            catch (Exception ex)
            {
                StatusMessage = $"❌ Fout bij openen van bestand: {ex.Message}";
            }
        }
    }

    [RelayCommand]
    public void ParseYaml()
    {
        if (string.IsNullOrWhiteSpace(YamlText))
        {
            StatusMessage = "⚠️ Plak of open eerst een geldige docker-compose YAML tekst.";
            HasParsedServices = false;
            ParsedServices.Clear();
            return;
        }

        var result = _importerService.ParseComposeYaml(YamlText, FilePath);
        if (result.Success)
        {
            ParsedServices.Clear();
            foreach (var s in result.ParsedServices)
            {
                ParsedServices.Add(s);
            }

            if (!string.IsNullOrWhiteSpace(result.SuggestedProfileName))
            {
                NewProfileName = result.SuggestedProfileName;
            }

            HasParsedServices = true;
            StatusMessage = $"✅ {result.ParsedServices.Count} container(s) succesvol herkend in YAML!";
        }
        else
        {
            HasParsedServices = false;
            ParsedServices.Clear();
            StatusMessage = $"❌ {result.ErrorMessage}";
        }
    }

    [RelayCommand]
    private void ConfirmImport()
    {
        if (!HasParsedServices || ParsedServices.Count == 0)
        {
            ParseYaml();
            if (!HasParsedServices) return;
        }

        if (CreateNewProfile && string.IsNullOrWhiteSpace(NewProfileName))
        {
            StatusMessage = "⚠️ Vul een geldige naam in voor het nieuwe profiel.";
            return;
        }

        ResultServices.Clear();
        ResultServices.AddRange(ParsedServices);
        IsConfirmed = true;
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private void Cancel()
    {
        IsConfirmed = false;
        RequestClose?.Invoke();
    }
}
