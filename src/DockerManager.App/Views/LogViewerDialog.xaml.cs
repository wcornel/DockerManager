using System.Windows;
using System.Windows.Threading;
using DockerManager.App.ViewModels;

namespace DockerManager.App.Views;

public partial class LogViewerDialog : Window
{
    private readonly LogViewerViewModel _viewModel;
    private bool _isClosed;
    private const int MaxLogChars = 250_000;
    private const int TrimTargetChars = 200_000;

    public LogViewerDialog(LogViewerViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;

        _viewModel.OnLineAppended += AppendLogLine;

        Loaded += async (_, _) => await _viewModel.StartStreamingAsync();
        Closed += (_, _) =>
        {
            _isClosed = true;
            _viewModel.OnLineAppended -= AppendLogLine;
            _viewModel.StopStreaming();
        };
    }

    private void AppendLogLine(string line)
    {
        if (_isClosed) return;

        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (_isClosed) return;

            try
            {
                LogTextBox.AppendText(line + Environment.NewLine);
                if (LogTextBox.Text.Length > MaxLogChars)
                {
                    var text = LogTextBox.Text;
                    int cutStart = Math.Max(0, text.Length - TrimTargetChars);
                    int nextNewline = text.IndexOf('\n', cutStart);
                    int trimIndex = nextNewline >= 0 && nextNewline + 1 < text.Length ? nextNewline + 1 : cutStart;
                    LogTextBox.Text = text[trimIndex..];
                }
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
                Helpers.SafeClipboardHelper.SetText(LogTextBox.Text);
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
