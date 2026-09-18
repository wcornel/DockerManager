using System.IO;
using System.Windows;
using DockerManager.App.Models;

namespace DockerManager.App.Views;

public partial class AddProfileDialog : Window
{
    private readonly IEnumerable<string> _existingNames;

    public ProfileModel? CreatedProfile { get; private set; }

    public AddProfileDialog(IEnumerable<string> existingProfileNames)
    {
        InitializeComponent();
        _existingNames = existingProfileNames;
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

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
