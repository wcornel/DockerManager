using System.Windows;
using DockerManager.App.Services;
using DockerManager.App.ViewModels;

namespace DockerManager.App.Views;

public partial class PortConflictDialog : Window
{
    private readonly PortConflictViewModel _viewModel;

    public PortConflictDialog(PortConflictViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;
        _viewModel.RequestClose += () => Close();

        Loaded += async (_, _) =>
        {
            try
            {
                await _viewModel.InitializeAsync();
            }
            catch (Exception ex)
            {
                AppLogger.LogError(nameof(PortConflictDialog), ex, "Port conflict scan failed on dialog load");
            }
        };
    }
}
