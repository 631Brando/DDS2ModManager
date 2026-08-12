using System.Reflection;

namespace DDS2ModManager.Services;

/// Checks 631Brando/DDS2ModManager's GitHub releases for a newer version and applies it.
/// Expects each release to carry a single asset literally named "DDS2ModManager.exe" - the
/// self-contained single-file publish output (see DDS2ModManager.csproj and
/// .github/workflows/release.yml).
///
/// Two channels, both publishing ordinary GitHub releases so the update path is identical:
///
///   Stable        tags like "v1.0.7"        -> published normally
///   Experimental  tags like "v1.0.7-exp.2"  -> published as a GitHub prerelease
///
/// The suffix number becomes the build's fourth version component, so an experimental build
/// always sorts above the stable release it came from (1.0.7.2 > 1.0.7.0) and below the next
/// stable one (1.0.7.2 &lt; 1.0.8.0). That means ordinary version comparison handles both channels
/// without special cases, and someone on experimental is moved onto stable automatically once
/// stable catches up.
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
    ///
    /// IsDowngrade covers switching from experimental back to stable: the stable build on offer
    /// is older than what's installed, which is still the right thing to move to, but the user
    /// should be told that's what's happening rather than seeing it called an update.
    public record UpdateCheckResult(bool Succeeded, GitHubReleaseInfo? NewerRelease, bool IsDowngrade = false);

    /// Never throws - a failed check (offline, no releases yet, etc.) comes back as
    /// Succeeded=false rather than blocking or crashing startup. GitHubReleaseService already
    /// logs the specific failure reason.
    public async Task<UpdateCheckResult> CheckForUpdateAsync(string? channel = null)
    {
        var experimental = UpdateChannels.IsExperimental(channel);
        var release = experimental ? await GetLatestAnyAsync() : await _github.GetLatestReleaseAsync(Owner, Repo);
        if (release == null) return new UpdateCheckResult(false, null);

        var candidate = ParseVersion(release.TagName);
        if (candidate == null) return new UpdateCheckResult(true, null);
        if (FindAsset(release) == null) return new UpdateCheckResult(true, null);

        var current = GetCurrentVersion();
        if (candidate == current) return new UpdateCheckResult(true, null);

        // On the stable channel an older "latest" is a real outcome, not a no-op: it means the
        // user is running an experimental build and has switched back, so stable is where they
        // should end up even though the number goes down.
        if (candidate < current)
        {
            return experimental
                ? new UpdateCheckResult(true, null)
                : new UpdateCheckResult(true, release, IsDowngrade: true);
        }

        return new UpdateCheckResult(true, release);
    }

    /// Newest release of any kind. The experimental channel takes whichever came out most
    /// recently, so a stable release still reaches experimental users the day it ships.
    private async Task<GitHubReleaseInfo?> GetLatestAnyAsync()
    {
        var releases = await _github.GetReleasesAsync(Owner, Repo);
        return releases
            .Where(r => ParseVersion(r.TagName) != null && FindAsset(r) != null)
            .OrderByDescending(r => ParseVersion(r.TagName))
            .FirstOrDefault();
    }

    public static string GetReleaseUrl(string tagName) =>
        $"https://github.com/{Owner}/{Repo}/releases/tag/{tagName}";

    public static Version GetCurrentVersion() =>
        Normalize(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0));

    /// Whether the running build came from the experimental channel, judged by its own version
    /// rather than the setting - so a user who installed an experimental build and then flipped
    /// the setting back still sees an accurate description of what they're running.
    public static bool IsRunningExperimentalBuild() => GetCurrentVersion().Revision > 0;

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

    /// Accepts "v1.0.7", "1.0.7" and "v1.0.7-exp.2". The experimental suffix becomes the fourth
    /// component, matching what the release workflow builds the exe with.
    internal static Version? ParseVersion(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;

        var trimmed = tag.Trim().TrimStart('v', 'V');

        var suffix = 0;
        var dash = trimmed.IndexOf('-');
        if (dash >= 0)
        {
            // "-exp.2" -> 2. A suffix we don't recognise is treated as .0 rather than rejected,
            // so an oddly-tagged release still resolves to its base version.
            var tail = trimmed[(dash + 1)..];
            trimmed = trimmed[..dash];

            var dot = tail.LastIndexOf('.');
            if (dot >= 0 && int.TryParse(tail[(dot + 1)..], out var n) && n >= 0) suffix = n;
        }

        if (!Version.TryParse(trimmed, out var v)) return null;

        var normalized = Normalize(v);
        return suffix > 0
            ? new Version(normalized.Major, normalized.Minor, normalized.Build, suffix)
            : normalized;
    }

    // System.Version.CompareTo treats an unset component (-1) as less than an explicit 0, so
    // "1.2.0" (Build=0, Revision=-1) would otherwise compare as OLDER than "1.2.0.0"
    // (Revision=0) despite being the same version. Normalize both sides to 4 explicit parts.
    private static Version Normalize(Version v) =>
        new(Math.Max(v.Major, 0), Math.Max(v.Minor, 0), Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
}
