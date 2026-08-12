using System.Net.Http;

namespace DDS2ModManager.Services;

/// Fetches and caches a published mod catalog.
///
/// The catalog URL is a constant rather than something a mod can influence: it's the maintainers'
/// own list, not something discovered from installed content. Everything it offers is still
/// checked through GitHubUrlParser before anything is downloaded, because "our own list" is a
/// reason to fetch it, not a reason to stop validating it.
///
/// Cached on disk so the page still works offline, and so a rate limit or an outage shows the last
/// known list rather than an empty screen.
public class ModCatalogService
{
    /// Where the catalog lives. Points at the manager's own repository until the real mod
    /// repository exists - swapping this constant is the only change needed to go live.
    public const string CatalogUrl =
        "https://raw.githubusercontent.com/631Brando/DDS2ModManager/main/mods-catalog.json";

    private readonly string _cachePath;

    public ModCatalogService()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DDS2ModManager");
        Directory.CreateDirectory(dir);
        _cachePath = Path.Combine(dir, "mods-catalog.cache.json");
    }

    /// True when the fetch came from the network rather than the cache, so the UI can be honest
    /// about showing a possibly-stale list.
    public bool LastFetchWasLive { get; private set; }

    public async Task<ModCatalog?> LoadAsync()
    {
        LastFetchWasLive = false;

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("DDS2ModManager");

            var json = await http.GetStringAsync(CatalogUrl);
            var catalog = Parse(json);
            if (catalog != null)
            {
                File.WriteAllText(_cachePath, json);
                LastFetchWasLive = true;
                return catalog;
            }
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Info($"Couldn't fetch the mod catalog ({ex.Message}) - trying the cached copy.");
        }

        return LoadCached();
    }

    public ModCatalog? LoadCached()
    {
        try
        {
            return File.Exists(_cachePath) ? Parse(File.ReadAllText(_cachePath)) : null;
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Warn($"Couldn't read the cached mod catalog: {ex.Message}");
            return null;
        }
    }

    private static ModCatalog? Parse(string json)
    {
        var catalog = JsonSerializer.Deserialize<ModCatalog>(json);
        if (catalog == null) return null;

        if (catalog.Schema > ModCatalog.SupportedSchema)
        {
            LoggingService.Instance.Warn(
                $"The mod catalog is written for a newer version of this manager (schema {catalog.Schema}). " +
                "Ignoring it rather than misreading it.");
            return null;
        }

        // Drop entries that don't name a usable repository. Better to show a shorter list than to
        // offer something that will fail the moment it's clicked.
        catalog.Mods = catalog.Mods
            .Where(m => !string.IsNullOrWhiteSpace(m.Name)
                        && GitHubUrlParser.TryParse(m.Repo, out _, out _))
            .ToList();

        return catalog;
    }
}
