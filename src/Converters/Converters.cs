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
        ModType.LooseAsset => new SolidColorBrush(Color.FromRgb(0xF4, 0x72, 0xB6)), // pink
        ModType.DllPlugin => new SolidColorBrush(Color.FromRgb(0xA7, 0x8B, 0xFA)), // violet
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

/// Conflict cards are colour-coded by how much actually breaks: red when one mod's content is
/// definitively lost, amber when it depends on load order, grey when the mods coexist fine and
/// the card is purely explanatory.
public class ConflictSeverityToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        ConflictSeverity.Critical => new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71)),
        ConflictSeverity.Warning => new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24)),
        _ => new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80))
    };

    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public class ConflictSeverityToLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        ConflictSeverity.Critical => "CONFLICT",
        ConflictSeverity.Warning => "CHECK",
        _ => "COMPATIBLE"
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
///
/// NOTE the direction: this shows its target when the count IS zero. Binding a "you have N
/// things" banner to it displays that banner precisely when there is nothing to say.
public class ZeroCountToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value is int i && i == 0) ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// Collapses anything bound to a null or empty string. Used to hide the per-mod trust tick on
/// mods that publish no update address, where trusting an author would mean nothing.
public class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(value as string)
            ? System.Windows.Visibility.Collapsed
            : System.Windows.Visibility.Visible;

    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// Plain boolean inversion, for IsEnabled bindings. Kept separate from the visibility
/// converters above because those return Visibility, not bool, and binding one to IsEnabled
/// silently gives you "enabled" for every value.
public class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not true;

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => value is not true;
}
