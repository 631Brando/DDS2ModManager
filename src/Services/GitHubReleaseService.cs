using System.Net.Http;
using System.Net.Http.Headers;

namespace DDS2ModManager.Services;

public class GitHubAsset
{
    public string Name { get; set; } = "";
    public string BrowserDownloadUrl { get; set; } = "";
    public long Size { get; set; }
}

public class GitHubReleaseInfo
{
    public string TagName { get; set; } = "";
    public string Name { get; set; } = "";

    /// The release's markdown description - i.e. the changelog. Shown verbatim in the update
    /// prompt so users can see what's actually changing before agreeing to install it.
    public string Body { get; set; } = "";

    /// GitHub's prerelease flag. This is what separates the experimental channel from the stable
    /// one: the release workflow marks anything tagged with a "-exp" suffix as a prerelease, and
    /// GitHub's own /releases/latest endpoint then skips them automatically.
    public bool IsPrerelease { get; set; }

    /// Used to order releases when listing them, since the experimental channel wants "newest
    /// published" rather than whatever GitHub considers latest.
    public DateTimeOffset PublishedAt { get; set; }

    public List<GitHubAsset> Assets { get; set; } = new();
}

public class GitHubReleaseService
{
    private static readonly HttpClient _http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient();
        c.DefaultRequestHeaders.UserAgent.ParseAdd("DDS2ModManager/1.0");
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return c;
    }

    public Task<GitHubReleaseInfo?> GetReleaseByTagAsync(string owner, string repo, string tag) =>
        GetReleaseAsync($"https://api.github.com/repos/{owner}/{repo}/releases/tags/{tag}", $"{owner}/{repo}@{tag}");

    /// The most recent non-prerelease, non-draft release - what GitHub's own "Latest" badge points to.
    public Task<GitHubReleaseInfo?> GetLatestReleaseAsync(string owner, string repo) =>
        GetReleaseAsync($"https://api.github.com/repos/{owner}/{repo}/releases/latest", $"{owner}/{repo}@latest");

    /// Every release, newest first, including prereleases and excluding drafts.
    ///
    /// /releases/latest deliberately skips prereleases, which is exactly right for the stable
    /// channel and useless for the experimental one - hence this.
    public async Task<List<GitHubReleaseInfo>> GetReleasesAsync(string owner, string repo, int limit = 20)
    {
        var url = $"https://api.github.com/repos/{owner}/{repo}/releases?per_page={limit}";
        var results = new List<GitHubReleaseInfo>();

        try
        {
            using var resp = await _http.GetAsync(url);
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());

            foreach (var element in doc.RootElement.EnumerateArray())
            {
                // Drafts are invisible to anyone but the author, so treating one as available
                // would offer an update nobody can actually download.
                if (element.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) continue;
                results.Add(ParseRelease(element));
            }

            return results.OrderByDescending(r => r.PublishedAt).ToList();
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Error($"Failed to list GitHub releases ({owner}/{repo}): {ex.Message}");
            return results;
        }
    }

    private async Task<GitHubReleaseInfo?> GetReleaseAsync(string url, string logLabel)
    {
        var log = LoggingService.Instance;
        try
        {
            using var resp = await _http.GetAsync(url);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            return ParseRelease(doc.RootElement);
        }
        catch (Exception ex)
        {
            log.Error($"Failed to query GitHub ({logLabel}): {ex.Message}");
            return null;
        }
    }

    private static GitHubReleaseInfo ParseRelease(JsonElement root)
    {
        var info = new GitHubReleaseInfo
        {
            TagName = root.GetProperty("tag_name").GetString() ?? "",
            Name = root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                ? n.GetString() ?? ""
                : "",
            // "body" is present but null on releases published with no description, so this
            // has to tolerate both a missing property and an explicit JSON null.
            Body = root.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String
                ? b.GetString() ?? ""
                : "",
            IsPrerelease = root.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True,
            PublishedAt = root.TryGetProperty("published_at", out var p) && p.ValueKind == JsonValueKind.String
                          && DateTimeOffset.TryParse(p.GetString(), out var when)
                ? when
                : DateTimeOffset.MinValue
        };

        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                info.Assets.Add(new GitHubAsset
                {
                    Name = asset.GetProperty("name").GetString() ?? "",
                    BrowserDownloadUrl = asset.GetProperty("browser_download_url").GetString() ?? "",
                    Size = asset.TryGetProperty("size", out var s) ? s.GetInt64() : 0
                });
            }
        }

        return info;
    }

    public async Task DownloadAssetAsync(string url, string destinationPath, IProgress<double>? progress = null)
    {
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();

        var total = resp.Content.Headers.ContentLength ?? -1L;
        await using var stream = await resp.Content.ReadAsStreamAsync();
        await using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

        var buffer = new byte[81920];
        long readTotal = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, read));
            readTotal += read;
            if (total > 0) progress?.Report((double)readTotal / total * 100.0);
        }
    }
}
