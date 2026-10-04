namespace DDS2ModManager.Services;

/// Paths to a game's artwork in Steam's local library cache. Any of them can be null.
public sealed record GameArtPaths(string? Capsule, string? Header, string? Hero, string? HeroBlur, string? Logo)
{
    public static readonly GameArtPaths None = new(null, null, null, null, null);

    public bool HasAny => Capsule != null || Header != null || Hero != null || Logo != null;
}

/// Finds the artwork Steam already keeps on disk for every game in the library.
///
/// This is what lets the game selector show real box art without a network request: the Steam
/// client caches each owned game's capsule, header, hero banner and logo under
/// appcache\librarycache. Nothing is downloaded and nothing is written - the files are only read,
/// and if Steam hasn't cached a game's art the selector falls back to the game's own icon.
///
/// The cache has had three layouts, and a single machine can hold all of them at once because Steam
/// only rewrites an entry when it refreshes that game:
///
///   librarycache\1708850\library_600x900.jpg           flat, per-app folder (current)
///   librarycache\682990\&lt;sha1&gt;\library_600x900.jpg      per-app folder, one hash subfolder per asset
///   librarycache\1708850_library_600x900.jpg           legacy flat files
///
/// so every lookup searches the per-app folder recursively and then tries the legacy names.
public static class SteamLibraryArt
{
    // Several names per asset: Steam has renamed some over time ("header.jpg" vs
    // "library_header.jpg"), and the first one that exists wins.
    private static readonly string[] CapsuleNames = ["library_600x900.jpg", "library_600x900_2x.jpg"];
    private static readonly string[] HeaderNames = ["library_header.jpg", "header.jpg"];
    private static readonly string[] HeroNames = ["library_hero.jpg"];
    private static readonly string[] HeroBlurNames = ["library_hero_blur.jpg"];
    private static readonly string[] LogoNames = ["logo.png"];

    public static string? LibraryCacheFolder(string steamRoot) =>
        string.IsNullOrWhiteSpace(steamRoot) ? null : Path.Combine(steamRoot, "appcache", "librarycache");

    public static GameArtPaths Find(string? steamRoot, uint appId)
    {
        if (appId == 0 || steamRoot == null) return GameArtPaths.None;

        var cache = LibraryCacheFolder(steamRoot);
        if (cache == null || !Directory.Exists(cache)) return GameArtPaths.None;

        // One directory walk per game, then every asset is matched against that list - rather than
        // a walk per asset, which would read the same folder five times.
        var files = ListAppFiles(cache, appId);

        return new GameArtPaths(
            Capsule: Pick(files, cache, appId, CapsuleNames),
            Header: Pick(files, cache, appId, HeaderNames),
            Hero: Pick(files, cache, appId, HeroNames),
            HeroBlur: Pick(files, cache, appId, HeroBlurNames),
            Logo: Pick(files, cache, appId, LogoNames));
    }

    private static List<string> ListAppFiles(string cache, uint appId)
    {
        var folder = Path.Combine(cache, appId.ToString());
        try
        {
            return Directory.Exists(folder)
                ? Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories).ToList()
                : new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }

    private static string? Pick(List<string> appFiles, string cache, uint appId, string[] names)
    {
        foreach (var name in names)
        {
            // Newest first: when a hashed layout holds more than one copy of an asset, the most
            // recently written is the one Steam is currently showing.
            var hit = appFiles
                .Where(f => Path.GetFileName(f).Equals(name, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(SafeWriteTime)
                .FirstOrDefault(IsUsableImage);
            if (hit != null) return hit;

            var legacy = Path.Combine(cache, $"{appId}_{name}");
            if (IsUsableImage(legacy)) return legacy;
        }

        return null;
    }

    /// Steam leaves zero-byte and near-empty placeholders in the cache while art is pending;
    /// handing one of those to an Image produces a decode error instead of a fallback.
    private static bool IsUsableImage(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length > 256;
        }
        catch
        {
            return false;
        }
    }

    private static DateTime SafeWriteTime(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch { return DateTime.MinValue; }
    }
}
