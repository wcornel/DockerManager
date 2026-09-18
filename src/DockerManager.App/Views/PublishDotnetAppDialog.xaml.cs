using System.Windows;
using DockerManager.App.ViewModels;

namespace DockerManager.App.Views;

public partial class PublishDotnetAppDialog : Window
{
    public PublishDotnetAppDialog(PublishDotnetAppViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        viewModel.RequestClose += () =>
        {
            DialogResult = viewModel.IsCompleted;
            Close();
        };
    }
}
