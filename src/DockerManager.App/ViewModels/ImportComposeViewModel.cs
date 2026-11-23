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

    private string _lastParsedYaml = string.Empty;

    public bool HasActiveProfile { get; }

    public ObservableCollection<ServiceDefinition> ParsedServices { get; } = new();

    public List<ServiceDefinition> ResultServices { get; } = new();
    public bool IsConfirmed { get; private set; }
    public bool TargetIsNewProfile => !HasActiveProfile || CreateNewProfile;
    public string TargetProfileName => TargetIsNewProfile ? NewProfileName.Trim() : ActiveProfileName.Trim();

    public event Action? RequestClose;

    public ImportComposeViewModel(IDockerComposeImporterService importerService, string currentProfileName)
    {
        _importerService = importerService;
        if (string.IsNullOrWhiteSpace(currentProfileName))
        {
            HasActiveProfile = false;
            ImportIntoActiveProfile = false;
            CreateNewProfile = true;
            ActiveProfileName = string.Empty;
        }
        else
        {
            HasActiveProfile = true;
            ActiveProfileName = currentProfileName;
        }
    }

    partial void OnYamlTextChanged(string value)
    {
        if (!string.Equals(value, _lastParsedYaml, StringComparison.Ordinal))
        {
            HasParsedServices = false;
        }
    }

    [RelayCommand]
    private void BrowseFile()
    {
        var loc = LocalizationService.Instance;
        using var dialog = new System.Windows.Forms.OpenFileDialog
        {
            Title = loc.Get("ImportCompose_SelectFileTitle"),
            Filter = loc.Get("ImportCompose_FileFilter"),
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
                StatusMessage = loc.Get("ImportCompose_OpenError", ex.Message);
            }
        }
    }

    [RelayCommand]
    public void ParseYaml()
    {
        var loc = LocalizationService.Instance;
        if (string.IsNullOrWhiteSpace(YamlText))
        {
            StatusMessage = loc.Get("ImportCompose_EmptyYaml");
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

            _lastParsedYaml = YamlText;
            HasParsedServices = true;
            StatusMessage = loc.Get("ImportCompose_ParsedSuccess", result.ParsedServices.Count);
        }
        else
        {
            _lastParsedYaml = string.Empty;
            HasParsedServices = false;
            ParsedServices.Clear();
            StatusMessage = $"❌ {result.ErrorMessage}";
        }
    }

    [RelayCommand]
    private void ConfirmImport()
    {
        if (!HasParsedServices || ParsedServices.Count == 0 || !string.Equals(YamlText, _lastParsedYaml, StringComparison.Ordinal))
        {
            ParseYaml();
            if (!HasParsedServices) return;
        }

        if (TargetIsNewProfile && string.IsNullOrWhiteSpace(NewProfileName))
        {
            StatusMessage = LocalizationService.Instance.Get("ImportCompose_EmptyProfileName");
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
