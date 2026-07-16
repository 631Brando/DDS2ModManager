using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace DDS2ModManager.Converters;

public class ModTypeToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        ModType.LogicMod => new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80)),  // green
        ModType.PatchMod => new SolidColorBrush(Color.FromRgb(0x60, 0xA5, 0xFA)),  // blue
        ModType.LuaMod => new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24)),    // amber
        _ => new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF))                  // gray
    };

    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public class BoolToEnabledTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? "Enabled" : "Disabled";

    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public class LogLevelToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        LogLevel.Error => new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71)),
        LogLevel.Warning => new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24)),
        LogLevel.Success => new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80)),
        _ => new SolidColorBrush(Color.FromRgb(0xD1, 0xD5, 0xDB))
    };

    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value is true) ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value is true) ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// Visible only when the bound count is zero - used for "empty state" placeholder text
/// (e.g. "No conflicts detected"). A plain int-to-bool coercion isn't reliable in XAML,
/// so this handles the int explicitly.
public class ZeroCountToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value is int i && i == 0) ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}
