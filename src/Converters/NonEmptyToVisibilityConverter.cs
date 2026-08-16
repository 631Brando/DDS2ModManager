using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DDS2ModManager.Converters;

/// Shows an element only when the bound value is a non-empty string. Used for the parts of a Nexus
/// card that some mods leave blank, so a card never shows a stray label with nothing after it.
public class NonEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string s && !string.IsNullOrWhiteSpace(s) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
