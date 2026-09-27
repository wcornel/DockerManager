using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using DockerManager.App.Models;
using DockerManager.App.Services;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace DockerManager.App.Helpers;

public class StateToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is ContainerState state)
        {
            return state switch
            {
                ContainerState.Running => new SolidColorBrush(Color.FromRgb(34, 197, 94)), // Emerald Green
                ContainerState.Paused => new SolidColorBrush(Color.FromRgb(234, 179, 8)), // Amber
                ContainerState.Restarting or ContainerState.Updating => new SolidColorBrush(Color.FromRgb(59, 130, 246)), // Blue
                ContainerState.Exited or ContainerState.NotCreated => new SolidColorBrush(Color.FromRgb(148, 163, 184)), // Slate Gray
                ContainerState.Dead or ContainerState.Error => new SolidColorBrush(Color.FromRgb(239, 68, 68)), // Red
                _ => new SolidColorBrush(Color.FromRgb(100, 116, 139))
            };
        }
        if (value is bool b)
        {
            return b 
                ? new SolidColorBrush(Color.FromRgb(34, 197, 94)) 
                : new SolidColorBrush(Color.FromRgb(239, 68, 68));
        }
        return new SolidColorBrush(Colors.Gray);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class BooleanToBrushConverter : IValueConverter
{
    public Brush TrueBrush { get; set; } = new SolidColorBrush(Color.FromRgb(34, 197, 94));
    public Brush FalseBrush { get; set; } = new SolidColorBrush(Color.FromRgb(239, 68, 68));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is true ? TrueBrush : FalseBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class StateToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var loc = LocalizationService.Instance;
        if (value is ContainerState state)
        {
            return state switch
            {
                ContainerState.Running => loc.Get("Card_Status_Running"),
                ContainerState.Created => loc.Get("Card_Status_Created"),
                ContainerState.Paused => loc.Get("Card_Status_Paused"),
                ContainerState.Restarting => loc.Get("Card_Status_Restarting"),
                ContainerState.Updating => loc.Get("Card_Status_Updating"),
                ContainerState.Exited => loc.Get("Card_Status_Stopped"),
                ContainerState.NotCreated => loc.Get("Card_Status_NotStarted"),
                ContainerState.Dead => loc.Get("Card_Status_Dead"),
                ContainerState.Error => loc.Get("Card_Status_Error"),
                _ => loc.Get("Card_Status_Unknown")
            };
        }
        return loc.Get("Card_Status_Unknown");
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class AppBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is true ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class AppInverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is true ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class StringNullOrEmptyToVisibilityConverter : IValueConverter
{
    public bool Inverse { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var hasText = !string.IsNullOrWhiteSpace(value as string);
        if (Inverse) hasText = !hasText;
        return hasText ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class CountToVisibilityConverter : IValueConverter
{
    public bool Inverse { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        int count = 0;
        if (value is int i) count = i;
        else if (value is long l) count = (int)l;
        else if (value is System.Collections.ICollection c) count = c.Count;

        var visible = count > 0;
        if (Inverse) visible = !visible;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class NullToVisibilityConverter : IValueConverter
{
    public bool Inverse { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var isNull = value == null;
        if (Inverse) isNull = !isNull;
        return isNull ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is bool b ? !b : true;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is bool b ? !b : true;
    }
}
