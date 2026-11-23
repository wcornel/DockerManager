using System.Windows;
using System.Windows.Controls;

namespace DockerManager.App.Helpers;

public static class PasswordBoxHelper
{
    public static readonly DependencyProperty BindPasswordProperty =
        DependencyProperty.RegisterAttached(
            "BindPassword",
            typeof(bool),
            typeof(PasswordBoxHelper),
            new PropertyMetadata(false, OnBindPasswordChanged));

    public static readonly DependencyProperty BoundPasswordProperty =
        DependencyProperty.RegisterAttached(
            "BoundPassword",
            typeof(string),
            typeof(PasswordBoxHelper),
            new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnBoundPasswordChanged));

    private static readonly DependencyProperty UpdatingPasswordProperty =
        DependencyProperty.RegisterAttached(
            "UpdatingPassword",
            typeof(bool),
            typeof(PasswordBoxHelper),
            new PropertyMetadata(false));

    public static bool GetBindPassword(DependencyObject obj) => (bool)obj.GetValue(BindPasswordProperty);
    public static void SetBindPassword(DependencyObject obj, bool value) => obj.SetValue(BindPasswordProperty, value);

    public static string GetBoundPassword(DependencyObject obj) => (string)obj.GetValue(BoundPasswordProperty);
    public static void SetBoundPassword(DependencyObject obj, string value) => obj.SetValue(BoundPasswordProperty, value ?? string.Empty);

    private static bool GetUpdatingPassword(DependencyObject obj) => (bool)obj.GetValue(UpdatingPasswordProperty);
    private static void SetUpdatingPassword(DependencyObject obj, bool value) => obj.SetValue(UpdatingPasswordProperty, value);

    private static void OnBindPasswordChanged(DependencyObject dp, DependencyPropertyChangedEventArgs e)
    {
        if (dp is not PasswordBox box) return;

        if ((bool)e.OldValue)
        {
            box.PasswordChanged -= HandlePasswordChanged;
        }

        if ((bool)e.NewValue)
        {
            box.PasswordChanged += HandlePasswordChanged;
            var current = GetBoundPassword(box) ?? string.Empty;
            if (!string.Equals(box.Password, current, StringComparison.Ordinal))
            {
                SetUpdatingPassword(box, true);
                try
                {
                    box.Password = current;
                }
                finally
                {
                    SetUpdatingPassword(box, false);
                }
            }
        }
    }

    private static void OnBoundPasswordChanged(DependencyObject dp, DependencyPropertyChangedEventArgs e)
    {
        if (dp is not PasswordBox box) return;
        if (!GetBindPassword(box)) return;

        box.PasswordChanged -= HandlePasswordChanged;
        try
        {
            var newPassword = (e.NewValue as string) ?? string.Empty;
            if (!GetUpdatingPassword(box) && !string.Equals(box.Password, newPassword, StringComparison.Ordinal))
            {
                box.Password = newPassword;
            }
        }
        finally
        {
            box.PasswordChanged += HandlePasswordChanged;
        }
    }

    private static void HandlePasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not PasswordBox box) return;
        SetUpdatingPassword(box, true);
        try
        {
            SetBoundPassword(box, box.Password);
        }
        finally
        {
            SetUpdatingPassword(box, false);
        }
    }
}
