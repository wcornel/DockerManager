using System.Windows;
using DockerManager.App.ViewModels;

namespace DockerManager.App.Views;

public partial class EditServiceDialog : Window
{
    private readonly EditServiceViewModel _viewModel;

    public EditServiceDialog(EditServiceViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;

        _viewModel.RequestClose += () =>
        {
            try
            {
                DialogResult = _viewModel.IsSaved;
            }
            catch
            {
                Close();
            }
        };
    }
}
