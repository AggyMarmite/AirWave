using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AirWave;

public sealed class BoolToVisibility : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool b = value is bool v && v;
        if (Invert) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Scales a 0..1 level to a width (parameter = full width).</summary>
public sealed class LevelToWidth : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double level = value is double d ? d : 0;
        double full = double.TryParse(parameter as string, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : 100;
        // perceptual-ish curve so quiet sounds still move the meter
        return Math.Clamp(Math.Sqrt(Math.Clamp(level, 0, 1)) * full, 0, full);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
