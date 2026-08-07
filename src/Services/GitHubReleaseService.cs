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

    private async Task<GitHubReleaseInfo?> GetReleaseAsync(string url, string logLabel)
    {
        var log = LoggingService.Instance;
        try
        {
            using var resp = await _http.GetAsync(url);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var info = new GitHubReleaseInfo
            {
                TagName = root.GetProperty("tag_name").GetString() ?? "",
                Name = root.GetProperty("name").GetString() ?? "",
                // "body" is present but null on releases published with no description, so this
                // has to tolerate both a missing property and an explicit JSON null.
                Body = root.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String
                    ? b.GetString() ?? ""
                    : ""
            };

            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                info.Assets.Add(new GitHubAsset
                {
                    Name = asset.GetProperty("name").GetString() ?? "",
                    BrowserDownloadUrl = asset.GetProperty("browser_download_url").GetString() ?? "",
                    Size = asset.TryGetProperty("size", out var s) ? s.GetInt64() : 0
                });
            }

            return info;
        }
        catch (Exception ex)
        {
            log.Error($"Failed to query GitHub ({logLabel}): {ex.Message}");
            return null;
        }
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
