using System.Reflection;

namespace DDS2ModManager.Services;

/// Checks 631Brando/DDS2ModManager's GitHub releases for a newer version and applies it.
/// Expects each release to carry a single asset literally named "DDS2ModManager.exe" - the
/// self-contained single-file publish output (see DDS2ModManager.csproj and
/// .github/workflows/release.yml). The tag can be "1.2.0" or "v1.2.0" - the leading 'v' is
/// stripped either way.
public class AppUpdateService
{
    private const string Owner = "631Brando";
    private const string Repo = "DDS2ModManager";
    private const string AssetName = "DDS2ModManager.exe";

    private readonly GitHubReleaseService _github = new();

    /// Checking "succeeded" (we got a real answer from GitHub) is a different fact from "there's
    /// a newer version" - conflating them previously meant a network failure or a repo with no
    /// releases yet would get reported to the user as "you're on the latest version," which is
    /// simply not something we know in that case.
    public record UpdateCheckResult(bool Succeeded, GitHubReleaseInfo? NewerRelease);

    /// Never throws - a failed check (offline, no releases yet, etc.) comes back as
    /// Succeeded=false rather than blocking or crashing startup. GitHubReleaseService already
    /// logs the specific failure reason.
    public async Task<UpdateCheckResult> CheckForUpdateAsync()
    {
        var release = await _github.GetLatestReleaseAsync(Owner, Repo);
        if (release == null) return new UpdateCheckResult(false, null);

        var latest = ParseVersion(release.TagName);
        if (latest == null || latest <= GetCurrentVersion()) return new UpdateCheckResult(true, null);

        return new UpdateCheckResult(true, FindAsset(release) != null ? release : null);
    }

    public static string GetReleaseUrl(string tagName) =>
        $"https://github.com/{Owner}/{Repo}/releases/tag/{tagName}";

    public static Version GetCurrentVersion() =>
        Normalize(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0));

    public GitHubAsset? FindAsset(GitHubReleaseInfo release) =>
        release.Assets.FirstOrDefault(a => a.Name.Equals(AssetName, StringComparison.OrdinalIgnoreCase));

    /// Downloads the new exe and hands off to SelfReplaceHelper. Caller must shut the app down
    /// right after this returns - the replace/relaunch is already waiting on this process to exit.
    public async Task DownloadAndApplyAsync(GitHubAsset asset, IProgress<double>? progress = null)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), "DDS2MM_update_" + Guid.NewGuid().ToString("N") + ".exe");
        await _github.DownloadAssetAsync(asset.BrowserDownloadUrl, tempPath, progress);
        SelfReplaceHelper.ApplyUpdateAndRestart(tempPath);
    }

    private static Version? ParseVersion(string tag)
    {
        var trimmed = tag.TrimStart('v', 'V');
        return Version.TryParse(trimmed, out var v) ? Normalize(v) : null;
    }

    // System.Version.CompareTo treats an unset component (-1) as less than an explicit 0, so
    // "1.2.0" (Build=0, Revision=-1) would otherwise compare as OLDER than "1.2.0.0"
    // (Revision=0) despite being the same version. Normalize both sides to 4 explicit parts.
    private static Version Normalize(Version v) =>
        new(Math.Max(v.Major, 0), Math.Max(v.Minor, 0), Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
}
