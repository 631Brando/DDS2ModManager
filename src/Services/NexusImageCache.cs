using System.IO;
using System.Net.Http;
using System.Windows.Media.Imaging;

namespace DDS2ModManager.Services;

/// Fetches and caches the thumbnail Nexus publishes for a mod, for the hover card.
///
/// Three things make this less trivial than "download a png":
///
///   1. The URL ends in .png and the bytes are WebP. Nexus serves image/webp with a RIFF/WEBP
///      header from a .png address. WPF decodes it fine on Windows 11, which ships a WebP WIC
///      codec, but that codec is a Store component and is NOT guaranteed present - on a machine
///      without it BitmapImage throws while decoding. That has to degrade to "no picture", never
///      to a crash, because this is decoration on a tooltip.
///   2. Decoding happens on the UI thread when a tooltip opens. A 1322x1413 source scaled into a
///      ~260px card is wasted work repeated on every hover, so images are decoded once at a
///      bounded width and the frozen result is kept.
///   3. It is a nicety. Every failure here is a Warn at most, and an absent picture, because
///      nothing about the user's install is wrong when Nexus is unreachable.
///
/// Nothing here is called unless the user has the hover card enabled - see
/// AppSettings.ShowNexusModDetails.
public class NexusImageCache
{
    private static readonly Lazy<NexusImageCache> _instance = new(() => new NexusImageCache());
    public static NexusImageCache Instance => _instance.Value;

    /// Cards are small; there is no reason to hold a 1322px source in memory for one.
    private const int DecodeWidth = 320;

    /// A thumbnail that cannot be decoded on this machine is remembered as a failure so a hover
    /// doesn't retry the decode - and re-log - every time the pointer crosses the row.
    private readonly Dictionary<string, BitmapImage?> _decoded = new(StringComparer.OrdinalIgnoreCase);

    private readonly string _cacheDir;
    private static readonly HttpClient _http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("DDS2ModManager/1.0");
        return c;
    }

    private NexusImageCache()
    {
        _cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DDS2ModManager", "NexusImages");

        try { Directory.CreateDirectory(_cacheDir); }
        catch (Exception ex) { LoggingService.Instance.Warn($"Couldn't create the Nexus image cache folder: {ex.Message}"); }
    }

    /// True once this machine has been shown to have no usable WebP decoder, so the rest of the
    /// session stops downloading pictures it cannot display. One log line, not one per mod.
    private bool _decoderMissing;

    /// An already-decoded picture, without starting any work. Lets a tooltip show its image
    /// instantly on a second hover instead of going through the async path again.
    public bool TryGetDecoded(int modId, out BitmapImage? image) =>
        _decoded.TryGetValue(modId.ToString(), out image);

    /// Returns a decoded thumbnail, or null if there isn't one and there is nothing to be done
    /// about it. Never throws.
    ///
    /// Cached on disk by mod id, so a second launch shows the card instantly and offline.
    public async Task<BitmapImage?> GetAsync(int modId, string? pictureUrl, CancellationToken cancel = default)
    {
        if (modId <= 0 || string.IsNullOrWhiteSpace(pictureUrl)) return null;

        var key = modId.ToString();
        if (_decoded.TryGetValue(key, out var already)) return already;
        if (_decoderMissing) return null;

        try
        {
            var path = Path.Combine(_cacheDir, key + ".img");

            if (!File.Exists(path))
            {
                if (!await TryDownloadAsync(pictureUrl!, path, cancel)) return Remember(key, null);
            }

            var image = Decode(path);
            return Remember(key, image);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            LoggingService.Instance.Warn($"Couldn't load the Nexus picture for mod {modId}: {ex.Message}");
            return Remember(key, null);
        }
    }

    private BitmapImage? Remember(string key, BitmapImage? value)
    {
        _decoded[key] = value;
        return value;
    }

    private async Task<bool> TryDownloadAsync(string url, string destination, CancellationToken cancel)
    {
        // Only Nexus's own CDN. The URL arrives from an API response rather than from inside a
        // mod, so this is a smaller worry than the update-URL allowlist - but a picture is still
        // a file fetched onto someone's machine, and there is no reason for it to come from
        // anywhere else.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.EndsWith("nexusmods.com", StringComparison.OrdinalIgnoreCase))
        {
            LoggingService.Instance.Warn($"Ignoring a mod picture that isn't on the Nexus CDN: {url}");
            return false;
        }

        try
        {
            var bytes = await _http.GetByteArrayAsync(uri, cancel);
            if (bytes.Length == 0) return false;

            // Write beside then move, so an interrupted download can't leave a truncated file
            // that gets treated as cached forever.
            var temp = destination + ".part";
            await File.WriteAllBytesAsync(temp, bytes, cancel);
            File.Move(temp, destination, overwrite: true);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            LoggingService.Instance.Warn($"Couldn't download a mod picture: {ex.Message}");
            return false;
        }
    }

    /// Decodes whatever was cached. The format is whatever Nexus served - WebP in practice, which
    /// WPF only reads if this machine has the codec.
    private BitmapImage? Decode(string path)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path);
            image.DecodePixelWidth = DecodeWidth;

            // OnLoad, so the file handle is released immediately and the cache stays deletable
            // while the app is running.
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();

            // Frozen: decoded once here, then usable from any thread and never re-rendered.
            image.Freeze();
            return image;
        }
        catch (Exception ex)
        {
            // The expected failure on a machine with no WebP codec. Said once, then the feature
            // quietly stops asking - a tooltip is not worth a log full of the same line.
            _decoderMissing = true;
            LoggingService.Instance.Info(
                "Mod pictures can't be shown on this PC - Windows has no WebP image support installed. " +
                $"Everything else works normally. (\"Webp Image Extensions\" from the Microsoft Store adds it.) [{ex.GetType().Name}]");
            return null;
        }
    }

    /// Total size of the cached pictures, for Settings to show and for the reset path to clear.
    public long CacheSizeBytes()
    {
        try
        {
            if (!Directory.Exists(_cacheDir)) return 0;
            return new DirectoryInfo(_cacheDir).EnumerateFiles().Sum(f => f.Length);
        }
        catch { return 0; }
    }

    public void Clear()
    {
        _decoded.Clear();
        _decoderMissing = false;

        try
        {
            if (!Directory.Exists(_cacheDir)) return;
            foreach (var f in Directory.EnumerateFiles(_cacheDir)) File.Delete(f);
        }
        catch (Exception ex) { LoggingService.Instance.Warn($"Couldn't clear the Nexus image cache: {ex.Message}"); }
    }
}
