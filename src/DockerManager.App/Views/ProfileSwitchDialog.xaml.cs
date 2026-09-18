using System.Windows;

namespace DockerManager.App.Views;

public partial class ProfileSwitchDialog : Window
{
    public bool? ShouldStopPrevious { get; private set; } = false;

    public ProfileSwitchDialog(string previousProfileName)
    {
        InitializeComponent();
        var template = Services.LocalizationService.Instance.CurrentLanguage == "en"
            ? "Active containers are currently running in profile '{0}'. Would you like to stop these containers or keep them running in the background?"
            : "Er draaien actieve containers in het profiel '{0}'. Wil je deze containers stoppen of op de achtergrond laten doordraaien?";
        MessageTextBlock.Text = string.Format(template, previousProfileName);
    }

    private void KeepRunning_Click(object sender, RoutedEventArgs e)
    {
        ShouldStopPrevious = false;
        DialogResult = true;
    }

    private void StopAll_Click(object sender, RoutedEventArgs e)
    {
        ShouldStopPrevious = true;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        ShouldStopPrevious = null;
        DialogResult = false;
    }
}
