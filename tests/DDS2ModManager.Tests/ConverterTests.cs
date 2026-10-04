using System.Globalization;
using System.Windows;
using DDS2ModManager.Converters;

namespace DDS2ModManager.Tests;

/// Value converters fail silently in XAML - a wrong answer is just an element that never shows -
/// so the ones with a non-obvious rule are pinned here.
public class ConverterTests
{
    private static Visibility NullToCollapsed(object? value) =>
        (Visibility)new NullToCollapsedConverter().Convert(value, typeof(Visibility), null, CultureInfo.InvariantCulture);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Null_or_blank_collapses(string? value) =>
        Assert.Equal(Visibility.Collapsed, NullToCollapsed(value));

    [Fact]
    public void A_non_blank_string_shows() =>
        Assert.Equal(Visibility.Visible, NullToCollapsed("https://example.com/mod.json"));

    /// The banner binds the open game's entry - an object, not a string. Read "as string" it came
    /// out null, so an open game hid its own box art, chips and folder.
    [Fact]
    public void Any_object_that_exists_shows() =>
        Assert.Equal(Visibility.Visible, NullToCollapsed(new object()));
}
