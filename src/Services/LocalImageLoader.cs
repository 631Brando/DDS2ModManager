using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DDS2ModManager.Services;

/// Loads pictures from local files for the game selector, without blocking the window.
///
/// Two rules every image here follows, because breaking either is invisible until it hurts:
///
///  - Decoded off the UI thread and Frozen. A frozen bitmap can be handed across threads; an
///    unfrozen one throws the moment the UI touches it. Decoding Steam's 600x900 capsules and
///    1920-wide hero banners on the UI thread would stutter the window every time it opened.
///  - CacheOption.OnLoad, so the file is read once and released. The default holds the file open
///    for as long as the image lives, which would lock Steam out of its own cache.
///
/// DecodePixelWidth matters as much: a hero banner shown 900px wide does not need to sit in memory
/// at 3840px, and decoding at display size cuts both the time and the memory to a fraction.
public static class LocalImageLoader
{
    public static Task<BitmapSource?> LoadAsync(string? path, int decodeWidth) =>
        string.IsNullOrWhiteSpace(path) ? Task.FromResult<BitmapSource?>(null) : Task.Run(() => Load(path, decodeWidth));

    public static BitmapSource? Load(string path, int decodeWidth)
    {
        try
        {
            if (!File.Exists(path)) return null;

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.UriSource = new Uri(path, UriKind.Absolute);
            if (decodeWidth > 0) image.DecodePixelWidth = decodeWidth;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            // A corrupt or half-written cache file is not worth a log line - the card just falls
            // back to the game's icon.
            return null;
        }
    }

    /// The game's own icon from its executable, for games Steam has no cached art for: anything
    /// added by folder, Epic and GOG installs, or a Steam game that has never been shown in the
    /// library. Asks for a large size so it stays crisp on a card rather than blowing up a 32px icon.
    public static Task<BitmapSource?> LoadExeIconAsync(string? exePath, int size = 128) =>
        string.IsNullOrWhiteSpace(exePath) ? Task.FromResult<BitmapSource?>(null) : Task.Run(() => LoadExeIcon(exePath, size));

    public static BitmapSource? LoadExeIcon(string exePath, int size)
    {
        if (!File.Exists(exePath)) return null;

        var icons = new IntPtr[1];
        var ids = new uint[1];
        try
        {
            var got = PrivateExtractIcons(exePath, 0, size, size, icons, ids, 1, 0);
            if (got == 0 || icons[0] == IntPtr.Zero) return null;

            var source = Imaging.CreateBitmapSourceFromHIcon(icons[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
        finally
        {
            // CreateBitmapSourceFromHIcon copies the pixels, so the handle is ours to release -
            // and leaking one per game per scan would eventually exhaust the process's GDI quota.
            if (icons[0] != IntPtr.Zero) DestroyIcon(icons[0]);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint PrivateExtractIcons(string lpszFile, int nIconIndex, int cxIcon, int cyIcon,
        IntPtr[] phicon, uint[] piconid, uint nIcons, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    /// A stable colour for a game with no art at all, derived from its name so the same game is
    /// always the same colour - a placeholder that changed on every launch would read as a bug.
    public static Color PlaceholderColor(string name)
    {
        // FNV-1a rather than string.GetHashCode, which .NET randomises per process.
        uint hash = 2166136261;
        foreach (var c in name.ToLowerInvariant()) hash = (hash ^ c) * 16777619;

        // Hue from the hash; saturation and lightness fixed low so it sits on the dark theme
        // instead of shouting over it.
        var hue = hash % 360 / 360.0;
        return FromHsl(hue, 0.42, 0.30);
    }

    private static Color FromHsl(double h, double s, double l)
    {
        double Channel(double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
            var p = 2 * l - q;
            if (t < 1.0 / 6) return p + (q - p) * 6 * t;
            if (t < 0.5) return q;
            if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
            return p;
        }

        return Color.FromRgb(
            (byte)Math.Round(Channel(h + 1.0 / 3) * 255),
            (byte)Math.Round(Channel(h) * 255),
            (byte)Math.Round(Channel(h - 1.0 / 3) * 255));
    }
}
