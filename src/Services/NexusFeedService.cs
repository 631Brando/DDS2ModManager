using System.Net.Http;
using System.Text;

namespace DDS2ModManager.Services;

/// Finds mods newly published on Nexus for a game, so the manager can say "there are three new
/// DDS2 mods since you last looked".
///
/// NO API KEY. The v2 GraphQL endpoint answers this query unauthenticated - Nexus's own Discord
/// bot calls its equivalent with auth explicitly disabled (`this.headers(true)`), and that was
/// confirmed against the live API before this was written. That matters: an API key would mean
/// every user pasting one into Settings before the feature did anything, for what is only a
/// "look what's new" banner.
///
/// Scope is deliberately narrow - DISCOVERY only. Nothing here downloads or installs, and mod
/// updates do not come from here: those use each mod's own ModUpdateUrl (ModUpdateService).
/// Nexus's "updated mods" query does require OAuth, so keeping the two apart is what keeps this
/// keyless.
public class NexusFeedService
{
    private const string Endpoint = "https://api.nexusmods.com/v2/graphql";

    /// Page size is capped at 50 by the API (default 20). A banner never needs more, so this
    /// deliberately does not paginate - "and 40 others" is not a useful thing to tell someone.
    private const int MaxResults = 50;

    private static readonly HttpClient _http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("DDS2ModManager/1.0");
        return c;
    }

    /// pictureUrl was added for the hover card, and is requested here too so the banner and the
    /// card share one query shape. It comes back as a staticdelivery.nexusmods.com URL that ends
    /// in .png but is actually served as image/webp - see NexusImageCache, which has to cope.
    private const string Query = @"
query LatestMods($filter: ModsFilter, $sort: [ModsSort!]) {
  mods(filter: $filter, sort: $sort) {
    nodes {
      uid name summary modId adult createdAt pictureUrl
      game { domainName }
      uploader { name }
    }
    totalCount
  }
}";

    /// Mods published for this game since `since`. Never throws - the banner is a nicety, and a
    /// Nexus outage must not produce an error in the log every launch.
    public async Task<List<NexusModPost>> GetNewModsAsync(
        string gameDomain, DateTime sinceUtc, bool includeAdult = false, CancellationToken cancel = default)
    {
        var results = new List<NexusModPost>();

        try
        {
            var sinceUnix = ((DateTimeOffset)DateTime.SpecifyKind(sinceUtc, DateTimeKind.Utc)).ToUnixTimeSeconds();

            var variables = new
            {
                filter = new
                {
                    gameDomainName = new[] { new { value = gameDomain, op = "EQUALS" } },
                    createdAt = new { value = sinceUnix.ToString(), op = "GT" }
                },
                sort = new[] { new { createdAt = new { direction = "DESC" } } }
            };

            var payload = JsonSerializer.Serialize(new { query = Query, variables });
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(Endpoint, content, cancel);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cancel);
            using var doc = JsonDocument.Parse(json);

            // GraphQL reports failures in a 200 response body, so a successful status code is
            // not the same as a successful query.
            if (doc.RootElement.TryGetProperty("errors", out var errors) && errors.GetArrayLength() > 0)
            {
                var first = errors[0].TryGetProperty("message", out var m) ? m.GetString() : "unknown";
                LoggingService.Instance.Warn($"Nexus feed query was rejected: {first}");
                return results;
            }

            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("mods", out var mods) ||
                !mods.TryGetProperty("nodes", out var nodes))
                return results;

            foreach (var n in nodes.EnumerateArray())
            {
                var post = new NexusModPost
                {
                    Uid = Str(n, "uid"),
                    Name = Str(n, "name"),
                    Summary = Str(n, "summary"),
                    ModId = n.TryGetProperty("modId", out var id) && id.TryGetInt32(out var i) ? i : 0,
                    Adult = n.TryGetProperty("adult", out var a) && a.ValueKind == JsonValueKind.True,
                    CreatedAt = n.TryGetProperty("createdAt", out var c) && c.TryGetDateTime(out var dt)
                        ? dt
                        : DateTime.MinValue,
                    GameDomain = n.TryGetProperty("game", out var g) ? Str(g, "domainName") : gameDomain,
                    Uploader = n.TryGetProperty("uploader", out var u) ? Str(u, "name") : "",
                    PictureUrl = Str(n, "pictureUrl") is { Length: > 0 } pic ? pic : null
                };

                if (!includeAdult && post.Adult) continue;
                if (post.ModId == 0) continue;

                results.Add(post);
                if (results.Count >= MaxResults) break;
            }
        }
        catch (OperationCanceledException) { /* shutting down, or the caller gave up */ }
        catch (Exception ex)
        {
            // Warn, not Error. Being unable to see what's new on Nexus is not a problem with
            // the user's install, and shouldn't read like one.
            LoggingService.Instance.Warn($"Couldn't check Nexus for new mods: {ex.Message}");
        }

        return results;
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
