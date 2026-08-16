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
/// The suffix number becomes the build's fourth version component, so "v1.0.7-exp.2" ships as
/// 1.0.7.2. That orders experimental builds correctly among themselves, but it does NOT make
/// 1.0.7.2 newer than 1.0.7: "v1.0.7-exp.2" is a *preview of* v1.0.7, published before it, and
/// the stable release supersedes it. Ordinary Version.CompareTo gets that backwards - which is
/// what CompareBuilds exists to fix, and why it has to be used for every "is this newer" decision
/// in this class.
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
    /// Channels is filled in on the experimental channel, where the check already has every
    /// release in hand, and is what lets the caller say "experimental is behind stable" instead of
    /// a bare "you're up to date".
    public record UpdateCheckResult(
        bool Succeeded,
        GitHubReleaseInfo? NewerRelease,
        VersionChange Change = VersionChange.Update,
        ChannelStatus? Channels = null);

    /// What accepting the offered release would actually do. A single "is this a downgrade?" flag
    /// can't express this, because the version number and the code can move in opposite
    /// directions: leaving a preview for the release that superseded it reads as 1.1.0.1 -> 1.1.0
    /// on screen while gaining every commit made since the preview.
    public enum VersionChange
    {
        /// Newer code, bigger number. The ordinary case.
        Update,

        /// Newer code, smaller number: moving off a preview onto the stable release it previewed.
        /// Describing this as a downgrade would be wrong - nothing is lost - but so would saying
        /// nothing, because the user watches the number shrink.
        SupersedingPreview,

        /// Genuinely older code. The user is running a build from a line the chosen channel hasn't
        /// reached yet, and going there gives up whatever was only in that line.
        Rollback
    }

    /// Where each channel currently stands. Answers the question a user actually has when they
    /// look at the channel setting - "is experimental ahead of stable right now, or behind it?" -
    /// which the version numbers alone cannot answer, because a preview carries a higher number
    /// than the release that supersedes it.
    public record ChannelStatus(GitHubReleaseInfo? LatestStable, GitHubReleaseInfo? LatestExperimental)
    {
        /// True when the newest experimental build has been overtaken by a stable release: the
        /// experimental line was folded into stable and nothing newer has been published to it
        /// since. Someone who switches to experimental in this state is not getting newer code,
        /// they're getting a preview of what they already have.
        public bool ExperimentalIsBehindStable
        {
            get
            {
                if (LatestStable == null || LatestExperimental == null) return false;
                var stable = ParseVersion(LatestStable.TagName);
                var experimental = ParseVersion(LatestExperimental.TagName);
                return stable != null && experimental != null && CompareBuilds(stable, experimental) > 0;
            }
        }
    }

    /// Never throws - a failed check (offline, no releases yet, etc.) comes back as
    /// Succeeded=false rather than blocking or crashing startup. GitHubReleaseService already
    /// logs the specific failure reason.
    public async Task<UpdateCheckResult> CheckForUpdateAsync(string? channel = null)
    {
        var experimental = UpdateChannels.IsExperimental(channel);

        ChannelStatus? channels = null;
        GitHubReleaseInfo? release;

        if (experimental)
        {
            // One request covers both the candidate and the channel comparison, so telling the
            // user where the channels stand costs nothing against GitHub's 60-an-hour limit.
            channels = await GetChannelStatusAsync();
            release = Installable(channels).OrderByDescending(r => ParseVersion(r.TagName), BuildOrder).FirstOrDefault();
        }
        else
        {
            release = await _github.GetLatestReleaseAsync(Owner, Repo);
        }

        if (release == null) return new UpdateCheckResult(false, null, Channels: channels);

        var candidate = ParseVersion(release.TagName);
        if (candidate == null) return new UpdateCheckResult(true, null, Channels: channels);
        if (FindAsset(release) == null) return new UpdateCheckResult(true, null, Channels: channels);

        var current = GetCurrentVersion();

        // "Newer" has to mean newer in the release line, not a bigger number. A preview carries a
        // higher number than the release it previews, so comparing numbers here would offer
        // v1.1.0-exp.1 to someone already running v1.1.0 - handing them older code, described as
        // an update, which is precisely backwards.
        var order = CompareBuilds(candidate, current);
        if (order == 0) return new UpdateCheckResult(true, null, Channels: channels);

        if (order < 0)
        {
            // On the stable channel an older "latest" is a real outcome, not a no-op: the user is
            // running an experimental build from a line stable hasn't reached yet, and stable is
            // still where they asked to be.
            return experimental
                ? new UpdateCheckResult(true, null, Channels: channels)
                : new UpdateCheckResult(true, release, VersionChange.Rollback, channels);
        }

        // Supersedes what's installed. The number can still shrink on the way - leaving a preview
        // for the release it previewed reads as 1.1.0.1 -> 1.1.0 - which is worth explaining, but
        // is not the same event as being rolled back onto older code.
        return new UpdateCheckResult(
            true,
            release,
            candidate < current ? VersionChange.SupersedingPreview : VersionChange.Update,
            channels);
    }

    /// Where the two channels stand relative to each other. Costs one request; callers that only
    /// want to describe the setting (rather than act on it) can use this on its own.
    public async Task<ChannelStatus> GetChannelStatusAsync()
    {
        var releases = await _github.GetReleasesAsync(Owner, Repo);
        var installable = releases.Where(r => ParseVersion(r.TagName) != null && FindAsset(r) != null).ToList();

        return new ChannelStatus(
            installable.Where(r => !r.IsPrerelease).OrderByDescending(r => ParseVersion(r.TagName), BuildOrder).FirstOrDefault(),
            installable.Where(r => r.IsPrerelease).OrderByDescending(r => ParseVersion(r.TagName), BuildOrder).FirstOrDefault());
    }

    private static IEnumerable<GitHubReleaseInfo> Installable(ChannelStatus channels) =>
        new[] { channels.LatestStable, channels.LatestExperimental }.OfType<GitHubReleaseInfo>();

    private static readonly IComparer<Version?> BuildOrder =
        Comparer<Version?>.Create((a, b) => a == null || b == null ? 0 : CompareBuilds(a, b));

    /// Orders two builds the way the release line actually runs.
    ///
    /// The fourth component is a preview counter, not a patch number: v1.1.0-exp.1 ships as
    /// 1.1.0.1 and is a preview *of* v1.1.0, published before it. So within one base version the
    /// stable release (revision 0) is the newest thing there is, and every preview of it is older
    /// - the opposite of what Version.CompareTo says, since 1.1.0.1 > 1.1.0.0 numerically.
    ///
    /// Getting this wrong is not cosmetic. It offered experimental users a preview of the release
    /// they were already running, and left anyone on a preview stranded there until the *next*
    /// version shipped, because the stable release that superseded them scored lower.
    public static int CompareBuilds(Version a, Version b)
    {
        var line = new Version(a.Major, a.Minor, a.Build).CompareTo(new Version(b.Major, b.Minor, b.Build));
        if (line != 0) return line;

        // Same base version: stable outranks every preview of it, and later previews outrank
        // earlier ones.
        if (a.Revision == b.Revision) return 0;
        if (a.Revision == 0) return 1;
        if (b.Revision == 0) return -1;
        return a.Revision.CompareTo(b.Revision);
    }

    public static string GetReleaseUrl(string tagName) =>
        $"https://github.com/{Owner}/{Repo}/releases/tag/{tagName}";

    /// How a version should be written for a user to read.
    ///
    /// A stable build carries a trailing ".0" that means nothing - the csproj says 1.1.0, so
    /// printing "1.1.0.0" invites people to wonder which of the two is real. A preview keeps all
    /// four parts, because there the fourth component is the preview number and is the point.
    public static string Describe(Version v) => v.Revision <= 0 ? v.ToString(3) : v.ToString();

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
    public static Version? ParseVersion(string tag)
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
