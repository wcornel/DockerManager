using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Input;

namespace DockerManager.App.Helpers;

/// <summary>
/// Provides resilient clipboard operations for WPF controls, preventing crashes
/// caused by transient Windows clipboard locks (CLIPBRD_E_CANT_OPEN / 0x800401D0).
/// </summary>
public static class SafeClipboardHelper
{
    private const int DefaultRetries = 10;
    private const int DelayBetweenRetriesMs = 30;

    /// <summary>
    /// Registers class-level command bindings on TextBox for Copy, Cut, and Paste
    /// to ensure clipboard operations are safely retried and never throw unhandled exceptions.
    /// </summary>
    public static void RegisterGlobalTextBoxClipboardHandlers()
    {
        // Copy
        CommandManager.RegisterClassCommandBinding(
            typeof(TextBox),
            new CommandBinding(ApplicationCommands.Copy, OnTextBoxCopy, OnCanExecuteTextBoxCopy)
        );

        // Cut
        CommandManager.RegisterClassCommandBinding(
            typeof(TextBox),
            new CommandBinding(ApplicationCommands.Cut, OnTextBoxCut, OnCanExecuteTextBoxCut)
        );

        // Paste
        CommandManager.RegisterClassCommandBinding(
            typeof(TextBox),
            new CommandBinding(ApplicationCommands.Paste, OnTextBoxPaste, OnCanExecuteTextBoxPaste)
        );
    }

    private static void OnCanExecuteTextBoxCopy(object sender, CanExecuteRoutedEventArgs e)
    {
        if (sender is TextBox tb)
        {
            e.CanExecute = tb.SelectionLength > 0;
            e.Handled = true;
        }
    }

    private static void OnTextBoxCopy(object sender, ExecutedRoutedEventArgs e)
    {
        if (sender is TextBox tb && !string.IsNullOrEmpty(tb.SelectedText))
        {
            SetText(tb.SelectedText);
            e.Handled = true;
        }
    }

    private static void OnCanExecuteTextBoxCut(object sender, CanExecuteRoutedEventArgs e)
    {
        if (sender is TextBox tb)
        {
            e.CanExecute = !tb.IsReadOnly && tb.SelectionLength > 0;
            e.Handled = true;
        }
    }

    private static void OnTextBoxCut(object sender, ExecutedRoutedEventArgs e)
    {
        if (sender is TextBox tb && !tb.IsReadOnly && !string.IsNullOrEmpty(tb.SelectedText))
        {
            if (SetText(tb.SelectedText))
            {
                tb.SelectedText = string.Empty;
            }
            e.Handled = true;
        }
    }

    private static void OnCanExecuteTextBoxPaste(object sender, CanExecuteRoutedEventArgs e)
    {
        if (sender is TextBox tb)
        {
            e.CanExecute = !tb.IsReadOnly;
            e.Handled = true;
        }
    }

    private static void OnTextBoxPaste(object sender, ExecutedRoutedEventArgs e)
    {
        if (sender is TextBox tb && !tb.IsReadOnly)
        {
            var text = GetText();
            if (!string.IsNullOrEmpty(text))
            {
                tb.SelectedText = text;
            }
            e.Handled = true;
        }
    }

    /// <summary>
    /// Safely sets text on the clipboard with retry logic.
    /// </summary>
    public static bool SetText(string text, int retries = DefaultRetries, int delayMs = DelayBetweenRetriesMs)
    {
        if (string.IsNullOrEmpty(text)) return false;

        for (int i = 0; i < retries; i++)
        {
            try
            {
                Clipboard.SetDataObject(text, true);
                return true;
            }
            catch (COMException)
            {
                if (i == retries - 1) return false;
                Thread.Sleep(delayMs);
            }
            catch (ExternalException)
            {
                if (i == retries - 1) return false;
                Thread.Sleep(delayMs);
            }
            catch
            {
                if (i == retries - 1) return false;
                Thread.Sleep(delayMs);
            }
        }

        return false;
    }

    /// <summary>
    /// Safely gets text from the clipboard with retry logic.
    /// </summary>
    public static string? GetText(int retries = DefaultRetries, int delayMs = DelayBetweenRetriesMs)
    {
        for (int i = 0; i < retries; i++)
        {
            try
            {
                return Clipboard.GetText();
            }
            catch
            {
                if (i == retries - 1) return null;
                Thread.Sleep(delayMs);
            }
        }

        return null;
    }
}
