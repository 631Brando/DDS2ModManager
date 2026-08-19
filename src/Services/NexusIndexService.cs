using System.IO;
using System.Net.Http;
using System.Text;

namespace DDS2ModManager.Services;

/// A local copy of every mod published for the game on Nexus, so the manager can show a mod's
/// picture and description without calling Nexus each time someone hovers a row.
///
/// NO API KEY, same as NexusFeedService - the v2 GraphQL endpoint answers this anonymously, which
/// was confirmed against the live API. The whole DDS2 catalogue is 99 mods and about 77 KB, so
/// caching all of it is cheaper and better behaved than a lookup per mod: hovering does no
/// network work at all, and the cards keep working offline.
///
/// THE TRAP, measured: `count` is silently CLAMPED to 80. Asking for 200 returns 80 with no error
/// and no warning, so a single-shot fetch would quietly miss everything past the first 80 and look
/// like it had worked. Paging by offset until totalCount is reached is not optional.
public class NexusIndexService
{
    private const string Endpoint = "https://api.nexusmods.com/v2/graphql";

    /// The API's real ceiling. Verified by sweep: 79 -> 79, 80 -> 80, 81 -> 80, 500 -> 80. It is a
    /// global cap, not a DDS2 one - another game's catalogue clamps identically.
    private const int PageSize = 80;

    /// A backstop against an API change turning the paging loop into an infinite one.
    private const int MaxPages = 40;

    /// How long a cached copy stays good. The catalogue gains a mod every few days at most, and a
    /// stale picture is not a problem worth a network call on every launch.
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromDays(3);

    private const int CacheSchema = 1;

    private static readonly HttpClient _http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("DDS2ModManager/1.0");
        return c;
    }

    /// adultContent, not adult: `adult` still resolves but introspection reports it deprecated in
    /// favour of this. thumbnailUrl, not pictureUrl: pictureUrl is the full-size image (measured
    /// at 1322x1413 for one mod), which is absurd to download for a tooltip.
    private const string Query = @"
query GameMods($filter: ModsFilter, $sort: [ModsSort!], $offset: Int, $count: Int) {
  mods(filter: $filter, sort: $sort, offset: $offset, count: $count) {
    totalCount
    nodes {
      uid modId name summary version adultContent
      pictureUrl thumbnailUrl createdAt updatedAt
      downloads endorsements
      game { domainName }
      uploader { name }
    }
  }
}";

    private readonly string _root;

    /// One cache FILE PER GAME. A single shared file was discarded and re-fetched in full on every
    /// game switch, because ReadCache rejects a cache whose stored domain doesn't match - so two
    /// games meant paging the whole catalogue over the network again each time the user moved
    /// between them.
    private string CachePathFor(string gameDomain)
    {
        var safe = new string(gameDomain
            .Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
        return Path.Combine(_root, $"nexus-index.{safe}.cache.json");
    }

    public NexusIndexService()
    {
        _root = AppPaths.EnsureRoot();
    }

    private sealed class CacheFile
    {
        public int Schema { get; set; } = CacheSchema;
        public string GameDomain { get; set; } = "";
        public DateTime FetchedUtc { get; set; }
        public List<NexusModPost> Mods { get; set; } = new();
    }

    /// The cached catalogue, refreshing it first if it is missing or stale.
    ///
    /// Never throws. A Nexus outage means the previous copy is used, or an empty list if there has
    /// never been one - and an empty list simply means no hover cards, which is the correct way for
    /// decoration to fail.
    public async Task<List<NexusModPost>> GetAsync(
        string gameDomain, bool forceRefresh = false, CancellationToken cancel = default)
    {
        var cached = ReadCache(gameDomain);

        if (!forceRefresh && cached != null && DateTime.UtcNow - cached.FetchedUtc < RefreshInterval)
            return cached.Mods;

        var fetched = await FetchAllAsync(gameDomain, cancel);

        if (fetched.Count == 0)
        {
            // Keep whatever we had. Replacing a good cache with nothing because Nexus was briefly
            // unreachable would silently blank every card until the next successful refresh.
            return cached?.Mods ?? new List<NexusModPost>();
        }

        WriteCache(new CacheFile { GameDomain = gameDomain, FetchedUtc = DateTime.UtcNow, Mods = fetched });
        return fetched;
    }

    /// Pages through the whole catalogue. Returns an empty list rather than a partial one if
    /// anything goes wrong mid-way: half a catalogue would show cards for some mods and not
    /// others, which reads as the feature being broken rather than as a network problem.
    private async Task<List<NexusModPost>> FetchAllAsync(string gameDomain, CancellationToken cancel)
    {
        var collected = new List<NexusModPost>();
        var seen = new HashSet<int>();

        try
        {
            var total = int.MaxValue;

            for (var page = 0; page < MaxPages && collected.Count < total; page++)
            {
                var (nodes, totalCount) = await FetchPageAsync(gameDomain, page * PageSize, cancel);
                if (page == 0) total = totalCount;

                // A page with nothing in it means the end, whatever totalCount claimed.
                if (nodes.Count == 0) break;

                foreach (var mod in nodes)
                {
                    if (mod.ModId > 0 && seen.Add(mod.ModId)) collected.Add(mod);
                }
            }

            if (total != int.MaxValue && collected.Count < total)
            {
                LoggingService.Instance.Warn(
                    $"Nexus listed {total} mods for {gameDomain} but only {collected.Count} could be read. " +
                    "Some mod details may be missing.");
            }
        }
        catch (OperationCanceledException) { return new List<NexusModPost>(); }
        catch (Exception ex)
        {
            LoggingService.Instance.Warn($"Couldn't read the Nexus mod list: {ex.Message}");
            return new List<NexusModPost>();
        }

        return collected;
    }

    private async Task<(List<NexusModPost> Nodes, int TotalCount)> FetchPageAsync(
        string gameDomain, int offset, CancellationToken cancel)
    {
        var results = new List<NexusModPost>();

        var variables = new
        {
            filter = new { gameDomainName = new[] { new { value = gameDomain, op = "EQUALS" } } },
            sort = new[] { new { createdAt = new { direction = "DESC" } } },
            offset,
            count = PageSize
        };

        var payload = JsonSerializer.Serialize(new { query = Query, variables });
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(Endpoint, content, cancel);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancel));

        // GraphQL reports failures inside a 200 body, so a good status code proves nothing.
        if (doc.RootElement.TryGetProperty("errors", out var errors) && errors.GetArrayLength() > 0)
        {
            var first = errors[0].TryGetProperty("message", out var m) ? m.GetString() : "unknown";
            LoggingService.Instance.Warn($"Nexus mod list query was rejected: {first}");
            return (results, 0);
        }

        if (!doc.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("mods", out var mods))
            return (results, 0);

        var totalCount = mods.TryGetProperty("totalCount", out var tc) && tc.TryGetInt32(out var t) ? t : 0;

        if (!mods.TryGetProperty("nodes", out var nodes)) return (results, totalCount);

        foreach (var n in nodes.EnumerateArray())
        {
            var post = new NexusModPost
            {
                Uid = Str(n, "uid"),
                Name = Str(n, "name"),
                Summary = Str(n, "summary"),
                Version = Str(n, "version"),
                ModId = n.TryGetProperty("modId", out var id) && id.TryGetInt32(out var i) ? i : 0,
                Adult = n.TryGetProperty("adultContent", out var a) && a.ValueKind == JsonValueKind.True,
                PictureUrl = Str(n, "pictureUrl") is { Length: > 0 } pic ? pic : null,
                ThumbnailUrl = Str(n, "thumbnailUrl") is { Length: > 0 } thumb ? thumb : null,
                CreatedAt = n.TryGetProperty("createdAt", out var c) && c.TryGetDateTime(out var cd) ? cd : DateTime.MinValue,
                UpdatedAt = n.TryGetProperty("updatedAt", out var u) && u.TryGetDateTime(out var ud) ? ud : DateTime.MinValue,
                Downloads = n.TryGetProperty("downloads", out var dl) && dl.TryGetInt32(out var d) ? d : 0,
                Endorsements = n.TryGetProperty("endorsements", out var en) && en.TryGetInt32(out var e) ? e : 0,
                GameDomain = n.TryGetProperty("game", out var g) ? Str(g, "domainName") : gameDomain,
                Uploader = n.TryGetProperty("uploader", out var up) ? Str(up, "name") : ""
            };

            if (post.ModId > 0) results.Add(post);
        }

        return (results, totalCount);
    }

    private CacheFile? ReadCache(string gameDomain)
    {
        try
        {
            var path = CachePathFor(gameDomain);
            if (!File.Exists(path)) return null;

            var parsed = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(path));
            if (parsed == null) return null;

            // A cache written by a newer build may mean fields this one misreads, and a cache for
            // a different game is simply not ours. Either way, refetch rather than guess.
            if (parsed.Schema > CacheSchema) return null;
            if (!string.Equals(parsed.GameDomain, gameDomain, StringComparison.OrdinalIgnoreCase)) return null;

            return parsed;
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Warn($"Couldn't read the cached Nexus mod list: {ex.Message}");
            return null;
        }
    }

    private void WriteCache(CacheFile cache)
    {
        try
        {
            File.WriteAllText(CachePathFor(cache.GameDomain),
                JsonSerializer.Serialize(cache, new JsonSerializerOptions { WriteIndented = false }));
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Warn($"Couldn't cache the Nexus mod list: {ex.Message}");
        }
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
