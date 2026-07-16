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

    public async Task<GitHubReleaseInfo?> GetReleaseByTagAsync(string owner, string repo, string tag)
    {
        var log = LoggingService.Instance;
        var url = $"https://api.github.com/repos/{owner}/{repo}/releases/tags/{tag}";
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
                Name = root.GetProperty("name").GetString() ?? ""
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
            log.Error($"Failed to query GitHub ({owner}/{repo}@{tag}): {ex.Message}");
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
