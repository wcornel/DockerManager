using System.Windows;
using System.Windows.Threading;
using DockerManager.App.ViewModels;

namespace DockerManager.App.Views;

public partial class LogViewerDialog : Window
{
    private readonly LogViewerViewModel _viewModel;

    public LogViewerDialog(LogViewerViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;

        _viewModel.OnLineAppended += AppendLogLine;

        Loaded += async (_, _) => await _viewModel.StartStreamingAsync();
        Closed += (_, _) => _viewModel.StopStreaming();
    }

    private void AppendLogLine(string line)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            try
            {
                LogTextBox.AppendText(line + Environment.NewLine);
                if (AutoScrollCheckBox.IsChecked == true)
                {
                    LogTextBox.ScrollToEnd();
                }
            }
            catch { }
        });
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!string.IsNullOrEmpty(LogTextBox.Text))
            {
                Clipboard.SetText(LogTextBox.Text);
            }
        }
        catch { }
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        LogTextBox.Clear();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
