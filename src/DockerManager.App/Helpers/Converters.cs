using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using DockerManager.App.Models;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace DockerManager.App.Helpers;

public class StateToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush RunningBrush = CreateFrozenBrush(Color.FromRgb(34, 197, 94));
    private static readonly SolidColorBrush PausedBrush = CreateFrozenBrush(Color.FromRgb(234, 179, 8));
    private static readonly SolidColorBrush ActiveTransitionBrush = CreateFrozenBrush(Color.FromRgb(59, 130, 246));
    private static readonly SolidColorBrush NeutralBrush = CreateFrozenBrush(Color.FromRgb(148, 163, 184));
    private static readonly SolidColorBrush ErrorBrush = CreateFrozenBrush(Color.FromRgb(239, 68, 68));
    private static readonly SolidColorBrush UnknownBrush = CreateFrozenBrush(Color.FromRgb(100, 116, 139));
    private static readonly SolidColorBrush GrayBrush = CreateFrozenBrush(Colors.Gray);

    internal static SolidColorBrush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is ContainerState state)
        {
            return state switch
            {
                ContainerState.Running => RunningBrush,
                ContainerState.Paused => PausedBrush,
                ContainerState.Restarting or ContainerState.Updating => ActiveTransitionBrush,
                ContainerState.Exited or ContainerState.NotCreated => NeutralBrush,
                ContainerState.Dead or ContainerState.Error => ErrorBrush,
                _ => UnknownBrush
            };
        }
        if (value is bool b)
        {
            return b ? RunningBrush : ErrorBrush;
        }
        return GrayBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class BooleanToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush DefaultTrueBrush = StateToBrushConverter.CreateFrozenBrush(Color.FromRgb(34, 197, 94));
    private static readonly SolidColorBrush DefaultFalseBrush = StateToBrushConverter.CreateFrozenBrush(Color.FromRgb(239, 68, 68));

    public Brush TrueBrush { get; set; } = DefaultTrueBrush;
    public Brush FalseBrush { get; set; } = DefaultFalseBrush;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is true ? TrueBrush : FalseBrush;
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
        long count = 0;
        if (value is int i) count = i;
        else if (value is long l) count = l;
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
