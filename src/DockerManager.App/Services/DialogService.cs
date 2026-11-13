using System.Windows;

namespace DockerManager.App.Services;

public interface IDialogService
{
    void ShowInfo(string message, string title);
    void ShowWarning(string message, string title);
    void ShowError(string message, string title);
    bool Confirm(string message, string title, bool isWarning = false);
}

public sealed class WpfDialogService : IDialogService
{
    public static WpfDialogService Instance { get; } = new();

    public void ShowInfo(string message, string title) =>
        ShowSafe(message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public void ShowWarning(string message, string title) =>
        ShowSafe(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);

    public void ShowError(string message, string title) =>
        ShowSafe(message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    public bool Confirm(string message, string title, bool isWarning = false)
    {
        try
        {
            var icon = isWarning ? MessageBoxImage.Warning : MessageBoxImage.Question;
            return MessageBox.Show(message, title, MessageBoxButton.YesNo, icon) == MessageBoxResult.Yes;
        }
        catch (Exception ex)
        {
            AppLogger.LogWarning(nameof(WpfDialogService), $"Confirm dialog failed: {title}", ex);
            return false;
        }
    }

    private static void ShowSafe(string message, string title, MessageBoxButton button, MessageBoxImage image)
    {
        try
        {
            MessageBox.Show(message, title, button, image);
        }
        catch (Exception ex)
        {
            AppLogger.LogWarning(nameof(WpfDialogService), $"Message dialog failed: {title}", ex);
        }
    }
}
