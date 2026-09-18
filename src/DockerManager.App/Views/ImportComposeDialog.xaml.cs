using System.Windows;
using DockerManager.App.ViewModels;

namespace DockerManager.App.Views;

public partial class ImportComposeDialog : Window
{
    public ImportComposeViewModel ViewModel { get; }

    public ImportComposeDialog(ImportComposeViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;

        ViewModel.RequestClose += () =>
        {
            DialogResult = ViewModel.IsConfirmed;
            Close();
        };
    }
}
