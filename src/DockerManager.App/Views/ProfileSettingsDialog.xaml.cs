using System.Windows;
using DockerManager.App.ViewModels;

namespace DockerManager.App.Views;

public partial class ProfileSettingsDialog : Window
{
    private readonly ProfileSettingsViewModel _viewModel;

    public ProfileSettingsDialog(ProfileSettingsViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;
        _viewModel.RequestClose += () => DialogResult = _viewModel.IsSaved;
    }
}
