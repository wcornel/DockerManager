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

    internal static void OnTextBoxCopy(object sender, ExecutedRoutedEventArgs e)
    {
        if (sender is TextBox tb)
        {
            if (!string.IsNullOrEmpty(tb.SelectedText))
            {
                SetText(tb.SelectedText);
            }
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

    internal static void OnTextBoxCut(object sender, ExecutedRoutedEventArgs e)
    {
        if (sender is TextBox tb)
        {
            if (!tb.IsReadOnly && !string.IsNullOrEmpty(tb.SelectedText) && SetText(tb.SelectedText))
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
            ApplyPasteToTextBox(tb, GetText());
            e.Handled = true;
        }
    }

    internal static void ApplyPasteToTextBox(TextBox tb, string? text)
    {
        if (tb.IsReadOnly || string.IsNullOrEmpty(text))
            return;

        if (!tb.AcceptsReturn)
        {
            text = text.Replace("\r", string.Empty).Replace("\n", string.Empty);
            if (string.IsNullOrEmpty(text))
                return;
        }

        if (tb.MaxLength > 0)
        {
            int currentLengthWithoutSelection = tb.Text.Length - tb.SelectionLength;
            int remaining = tb.MaxLength - currentLengthWithoutSelection;
            if (remaining <= 0)
                return;

            if (text.Length > remaining)
                text = text[..remaining];
        }

        int selectionStart = tb.SelectionStart;
        tb.SelectedText = text;
        tb.SelectionStart = selectionStart + text.Length;
        tb.SelectionLength = 0;
    }

    /// <summary>
    /// Safely sets text on the clipboard with retry logic.
    /// </summary>
    public static bool SetText(string text, int retries = DefaultRetries, int delayMs = DelayBetweenRetriesMs)
    {
        if (string.IsNullOrEmpty(text)) return false;
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA) return false;

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
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA) return null;

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
