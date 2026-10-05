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

    private void ContainerNameTextBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Space)
        {
            e.Handled = true;
            if (sender is System.Windows.Controls.TextBox tb)
            {
                var caret = tb.SelectionStart;
                tb.SelectedText = "-";
                tb.CaretIndex = caret + 1;
            }
        }
    }
}
