using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace DDS2ModManager.Services;

/// Keeps the curated list of Nexus authors that the Trusted Mods page is built from.
///
/// Same shape as the verified-mods list: published as a raw file in a repository the maintainers
/// control, fetched when the page opens, cached on disk so it survives being offline. A raw file
/// rather than the API means no rate limit and anyone can read what they're being shown.
///
/// It is a browsing recommendation, not a security decision - see TrustedNexusAuthorList for why
/// this is kept apart from ModTrustService rather than bolted onto it. Nothing here can cause a
/// download or an install; the page it feeds only opens Nexus in a browser.
public class TrustedNexusAuthorService
{
    private static readonly Lazy<TrustedNexusAuthorService> _instance = new(() => new TrustedNexusAuthorService());
    public static TrustedNexusAuthorService Instance => _instance.Value;

    private const string ListUrl =
        "https://raw.githubusercontent.com/631Brando/DDS2ModManager/main/trusted-nexus-authors.json";

    /// Long enough that opening the page twice in a session costs one request, short enough that
    /// adding an author reaches people the same day.
    private static readonly TimeSpan RefreshAfter = TimeSpan.FromHours(12);

    private readonly string _cachePath;
    private TrustedNexusAuthorList _authors = TrustedNexusAuthorList.Default;
    private DateTime _lastFetchUtc = DateTime.MinValue;

    private TrustedNexusAuthorService()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DDS2ModManager");
        Directory.CreateDirectory(dir);
        _cachePath = Path.Combine(dir, "trusted-nexus-authors.cache.json");
        LoadCached();
    }

    public TrustedNexusAuthorList Authors => _authors;

    /// Never throws and never leaves the caller with nothing: a failed fetch keeps whatever is
    /// already loaded, which is the cached copy, or the compiled-in defaults on a first run.
    public async Task<TrustedNexusAuthorList> GetAsync(bool forceRefresh = false)
    {
        if (!forceRefresh && DateTime.UtcNow - _lastFetchUtc < RefreshAfter) return _authors;

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("DDS2ModManager");

            var json = await http.GetStringAsync(ListUrl);
            var list = JsonSerializer.Deserialize<TrustedNexusAuthorList>(json);
            if (list == null) return _authors;

            if (list.Schema > TrustedNexusAuthorList.SupportedSchema)
            {
                LoggingService.Instance.Warn(
                    $"The trusted Nexus author list is written for a newer version of this manager "
                    + $"(schema {list.Schema}). Keeping the previous copy rather than misreading it.");
                return _authors;
            }

            // An empty published list is far more likely to be a mistake than an instruction to
            // show nobody, and acting on it would empty the page for everyone at once.
            if (list.Authors.Count == 0)
            {
                LoggingService.Instance.Warn("The trusted Nexus author list came back empty - keeping the previous copy.");
                return _authors;
            }

            _authors = list;
            _lastFetchUtc = DateTime.UtcNow;
            File.WriteAllText(_cachePath, json);
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Info(
                $"Couldn't refresh the trusted Nexus author list ({ex.Message}) - using the copy already loaded.");
        }

        return _authors;
    }

    private void LoadCached()
    {
        try
        {
            if (!File.Exists(_cachePath)) return;
            var list = JsonSerializer.Deserialize<TrustedNexusAuthorList>(File.ReadAllText(_cachePath));
            if (list is { Authors.Count: > 0 } && list.Schema <= TrustedNexusAuthorList.SupportedSchema)
                _authors = list;
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Warn($"Couldn't read the cached trusted Nexus author list: {ex.Message}");
        }
    }
}
