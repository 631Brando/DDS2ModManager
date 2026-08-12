namespace DDS2ModManager.Services;

/// Checks installed mods for new releases at the GitHub repository each mod declares, and
/// downloads the one the user agrees to.
///
/// Deliberately not the Nexus API. Mods are distributed through Nexus, but they carry their own
/// update source (see ModUpdateSourceResolver), which means no API key to enter, no
/// 2,500-request daily cap, and no premium account - Nexus only hands download links to premium
/// members through its API, which would have made updating a paid feature.
///
/// Conservative on purpose, because this ends with executable content on someone's machine:
///
///   - Only mods whose author opted in are ever checked.
///   - Only github.com is accepted as a source (GitHubUrlParser).
///   - A release with an ambiguous set of assets is skipped rather than guessed at.
///   - Nothing is installed here at all. This class reports and downloads; the user decides,
///     having seen the URL and the release notes. That separation is the whole mitigation for
///     updates not having passed through Nexus's virus scanning.
public class ModUpdateService
{
    private readonly GitHubReleaseService _github = new();

    /// How long a successful check stays good for.
    ///
    /// Unauthenticated GitHub allows 60 requests an hour per IP, shared across everything the
    /// app does. A user with thirty mods would spend half that on a single startup, so a
    /// re-check inside this window is skipped unless explicitly forced.
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    /// Checking succeeded (we got a real answer) is a different fact from finding an update -
    /// AppUpdateService learned that the hard way, where conflating them reported "you're up
    /// to date" to someone who was actually just offline. Same distinction here, and it
    /// matters more, because rate limiting makes failure routine rather than exceptional.
    public record ModUpdateCheckResult(bool Succeeded, int Checked, int Skipped, int UpdatesFound, string? Error);

    /// Stop after this many consecutive failures. Past this point the likely cause is being
    /// rate limited or offline, and grinding through another twenty mods produces twenty more
    /// failures, twenty more log lines, and no new information.
    private const int ConsecutiveFailureLimit = 3;

    /// File types that identify "this release is the mod", for the purpose of noticing a new
    /// version. Deliberately WIDER than what can be installed: a bare .pak is a real release of a
    /// real mod, and telling someone their mod has an update they must fetch by hand is far more
    /// use than saying nothing because the packaging isn't to our taste.
    private static readonly string[] DetectableExtensions = { ".zip", ".7z", ".rar", ".pak" };

    /// What the installer can actually unpack. Sourced from ArchiveExtractionService rather than
    /// restated, because ModInstallerService.PrepareInstall THROWS on anything else - the two
    /// lists drifting is what let a .pak release be detected, offered, and then refused.
    private static string[] InstallableExtensions => ArchiveExtractionService.SupportedExtensions;

    /// Whether the installer can take this asset, or the user has to download it themselves.
    /// Callers use this to decide what the update prompt is allowed to offer.
    public static bool CanAutoInstall(GitHubAsset asset) =>
        InstallableExtensions.Contains(Path.GetExtension(asset.Name), StringComparer.OrdinalIgnoreCase);

    /// Releases already fetched this session, keyed owner/repo, so several mods sharing one
    /// repository - or a second check in the same session - don't each cost an API request.
    private readonly Dictionary<string, GitHubReleaseInfo?> _releaseCache = new(StringComparer.OrdinalIgnoreCase);

    /// Checks every mod that declares an update source.
    ///
    /// Never throws. A mod whose check fails keeps its previous LatestVersion, so the grid
    /// still shows what was true at the last successful check rather than going blank.
    public async Task<ModUpdateCheckResult> CheckAllAsync(
        IEnumerable<ModInfo> mods,
        bool force = false,
        IProgress<string>? progress = null,
        CancellationToken cancel = default)
    {
        var log = LoggingService.Instance;
        var candidates = mods.Where(m => m.HasUpdateSource).ToList();

        if (candidates.Count == 0)
            return new ModUpdateCheckResult(true, 0, 0, 0, null);

        int checkedCount = 0, skipped = 0, found = 0, consecutiveFailures = 0;
        string? error = null;

        foreach (var mod in candidates)
        {
            if (cancel.IsCancellationRequested) break;

            if (!force && mod.LastUpdateCheck is { } last && DateTime.Now - last < CheckInterval)
            {
                skipped++;
                continue;
            }

            progress?.Report($"Checking {mod.Name}...");
            var ok = await CheckOneAsync(mod);
            checkedCount++;

            if (ok)
            {
                consecutiveFailures = 0;
                if (mod.UpdateAvailable) found++;
            }
            else if (++consecutiveFailures >= ConsecutiveFailureLimit)
            {
                error = $"Gave up after {consecutiveFailures} checks in a row failed - probably offline, " +
                        "or GitHub is rate limiting this connection (60 requests an hour without an account).";
                log.Warn(error);
                break;
            }
        }

        if (found > 0) log.Success($"{found} mod update(s) available.");
        else if (checkedCount > 0 && error == null) log.Info($"Checked {checkedCount} mod(s) - all up to date.");

        return new ModUpdateCheckResult(error == null, checkedCount, skipped, found, error);
    }

    /// Checks one mod. Returns whether the CHECK worked, not whether an update exists.
    public async Task<bool> CheckOneAsync(ModInfo mod)
    {
        var log = LoggingService.Instance;

        if (mod.UpdateSource is not { IsUsable: true } source) return false;

        var release = await GetLatestReleaseAsync(source);
        if (release == null) return false;   // GitHubReleaseService has already logged why

        var latest = NormalizeVersion(release.TagName);
        mod.LatestVersion = latest;
        mod.LastUpdateCheck = DateTime.Now;

        // An update address that has moved since install is the exact shape of a hijacked update
        // channel. Surface it and offer nothing until the user has re-confirmed the new address.
        if (mod.UpdateUrlChanged)
        {
            ClearPendingUpdate(mod);
            log.Warn($"'{mod.Name}' now points its updates at {mod.ModUpdateUrl}, but it was installed pointing at " +
                     $"{mod.InstalledUpdateUrl}. Not offering an update until you've confirmed that's expected.");
            return true;
        }

        // A mod that never declared its own version gives us nothing to compare against.
        // Reporting "up to date" would be a guess, and reporting "update available" would
        // flag every such mod forever, so report neither and say so once.
        if (string.IsNullOrWhiteSpace(mod.InstalledVersion))
        {
            ClearPendingUpdate(mod);
            log.Info($"'{mod.Name}' doesn't declare a version, so it can't be compared against {latest}. " +
                     "Its author can add one via a ModVersion variable on the ModActor, or a manifest.");
            return true;
        }

        if (!IsNewer(latest, NormalizeVersion(mod.InstalledVersion)))
        {
            ClearPendingUpdate(mod);
            return true;
        }

        // There is something newer - but only offer it if exactly one file in the release can be
        // identified as the download. Installing the wrong asset is worse than installing nothing.
        var asset = PickAsset(release, source);
        if (asset == null)
        {
            ClearPendingUpdate(mod);
            log.Warn($"'{mod.Name}' has a newer release ({release.TagName}) but no single downloadable file could be " +
                     "identified in it, so it's being left alone. The author can name one with the \"asset\" field " +
                     "in their .dds2mod.json.");
            return true;
        }

        mod.UpdateAvailable = true;
        mod.AvailableUpdateTag = release.TagName;
        mod.AvailableUpdateNotes = release.Body;
        mod.AvailableUpdateAssetUrl = asset.BrowserDownloadUrl;

        log.Info($"'{mod.Name}' {mod.InstalledVersion} -> {latest} available at {source.RepositoryUrl}");
        return true;
    }

    /// Clears any previously-found update, so a mod that has since been updated (or whose release
    /// was pulled) doesn't keep offering something that is no longer there.
    private static void ClearPendingUpdate(ModInfo mod)
    {
        mod.UpdateAvailable = false;
        mod.AvailableUpdateTag = null;
        mod.AvailableUpdateNotes = null;
        mod.AvailableUpdateAssetUrl = null;
    }

    private async Task<GitHubReleaseInfo?> GetLatestReleaseAsync(ModUpdateSource source)
    {
        var key = $"{source.Owner}/{source.Repo}";
        if (_releaseCache.TryGetValue(key, out var cached)) return cached;

        var release = await _github.GetLatestReleaseAsync(source.Owner, source.Repo);
        _releaseCache[key] = release;
        return release;
    }

    /// Picks the file this update refers to. A named asset wins; otherwise there has to be exactly
    /// one candidate, because picking the wrong one would install the wrong mod.
    ///
    /// PUBLIC, and the only implementation. The check, the install prompt and the catalog all call
    /// this. They used to each have their own rule, which is how an author's declared "asset" name
    /// came to be honoured when spotting an update and ignored when installing it - the update was
    /// detected via the named file and then installed from whichever archive happened to sort
    /// first. One rule means that cannot happen again.
    ///
    /// A declared name that matches nothing returns null rather than falling back to guessing: the
    /// author named a specific file, and quietly installing a different one is exactly the failure
    /// naming it was meant to prevent.
    public static GitHubAsset? PickAsset(GitHubReleaseInfo release, ModUpdateSource source)
    {
        if (!string.IsNullOrWhiteSpace(source.DeclaredAssetName))
        {
            return release.Assets.FirstOrDefault(a =>
                a.Name.Equals(source.DeclaredAssetName, StringComparison.OrdinalIgnoreCase));
        }

        var candidates = release.Assets
            .Where(a => DetectableExtensions.Contains(Path.GetExtension(a.Name), StringComparer.OrdinalIgnoreCase))
            .ToList();

        return candidates.Count == 1 ? candidates[0] : null;
    }

    /// Downloads an update to a temporary file and hands back the path. Installing it is the
    /// caller's job, through the same installer path a manual install uses - so an update goes
    /// through exactly the same type detection, placement and conflict checking as anything else.
    public async Task<string?> DownloadAsync(string assetUrl, string assetName, IProgress<double>? progress = null)
    {
        var temp = Path.Combine(Path.GetTempPath(), "DDS2MM_modupdate_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);

        var destination = Path.Combine(temp, assetName);

        try
        {
            await _github.DownloadAssetAsync(assetUrl, destination, progress);
            return destination;
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Error($"Couldn't download {assetName}: {ex.Message}");
            return null;
        }
    }

    /// Strips the leading 'v' people put on tags. Everything else is left alone: these strings
    /// are author-authored and mangling them further only makes the comparison less predictable.
    public static string NormalizeVersion(string? raw)
    {
        var v = (raw ?? "").Trim();
        if (v.StartsWith("v", StringComparison.OrdinalIgnoreCase) && v.Length > 1 && char.IsDigit(v[1]))
            v = v[1..];
        return v;
    }

    /// True when latest is genuinely newer than installed.
    ///
    /// Numeric comparison when both sides parse as versions, so 1.10.0 correctly beats 1.9.0 -
    /// a string comparison gets that backwards, and "1.9 is newer than 1.10" is exactly the
    /// kind of bug nobody notices until a user is stuck on an old build.
    ///
    /// When either side will not parse (dates, "beta3", "final-FINAL"), fall back to "different
    /// means newer". The tag came from GitHub's own latest release, so different really does
    /// mean the author published something since - and the cost of being wrong is an update
    /// prompt the user can decline, not a silent install.
    public static bool IsNewer(string latest, string installed)
    {
        if (string.IsNullOrWhiteSpace(latest)) return false;
        if (string.IsNullOrWhiteSpace(installed)) return false;

        if (TryParse(latest, out var l) && TryParse(installed, out var i))
            return l > i;

        return !latest.Equals(installed, StringComparison.OrdinalIgnoreCase);
    }

    /// System.Version needs at least major.minor, so a bare "2" is padded rather than rejected.
    private static bool TryParse(string raw, out Version version)
    {
        version = new Version(0, 0);

        // Keep only the leading numeric-and-dots run: "1.2.3-beta" compares as 1.2.3, which is
        // what an author means by it. Anything with no digits at all falls through to false.
        var span = raw.AsSpan();
        var end = 0;
        while (end < span.Length && (char.IsDigit(span[end]) || span[end] == '.')) end++;
        if (end == 0) return false;

        var numeric = span[..end].ToString().Trim('.');
        if (numeric.Length == 0) return false;
        if (!numeric.Contains('.')) numeric += ".0";

        return Version.TryParse(numeric, out version!);
    }
}
