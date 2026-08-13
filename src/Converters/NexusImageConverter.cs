using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace DDS2ModManager.Converters;

/// Supplies a mod's Nexus thumbnail to the hover card.
///
/// A tooltip opens on the UI thread, and the first time a given mod is hovered its picture may
/// still need downloading. Doing that synchronously would freeze the window for the length of an
/// HTTP request, so this returns null immediately and fills the image in when it arrives - the
/// card simply appears without a picture and then gains one.
///
/// After the first time it is a dictionary hit in NexusImageCache, so re-hovering is instant.
public class NexusImageConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not NexusModPost post) return null;

        var url = post.CardImageUrl;
        if (string.IsNullOrWhiteSpace(url)) return null;

        var cache = NexusImageCache.Instance;

        // Already decoded this session - hand it straight back so the picture is there the
        // instant the card opens.
        if (cache.TryGetDecoded(post.ModId, out var ready)) return ready;

        // Otherwise kick the fetch off and let the binding update when it completes. The holder
        // is what the card binds to; it raises a change notification once the image lands.
        var holder = new PendingImage();
        _ = holder.LoadAsync(post.ModId, url);
        return holder;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// A one-shot box for an image that is still being fetched.
///
/// Exists so the tooltip can bind to something now and show a picture later, without the card
/// itself needing to know anything about downloads.
public partial class PendingImage : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private BitmapImage? image;

    public async Task LoadAsync(int modId, string url)
    {
        try { Image = await NexusImageCache.Instance.GetAsync(modId, url); }
        catch { /* the cache already logs; a missing picture is not worth a second line */ }
    }
}

/// Shows an element only when the bound value is a non-empty string. Used for the parts of the
/// card that some mods leave blank, so a card never shows a stray label with nothing after it.
public class NonEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string s && !string.IsNullOrWhiteSpace(s) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
