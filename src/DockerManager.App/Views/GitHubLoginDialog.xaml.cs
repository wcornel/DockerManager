using System.Windows;
using DockerManager.App.ViewModels;

namespace DockerManager.App.Views;

public partial class GitHubLoginDialog : Window
{
    private readonly GitHubLoginViewModel _viewModel;

    public GitHubLoginDialog(GitHubLoginViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;

        _viewModel.RequestClose += () => Dispatcher.Invoke(Close);
    }
}
