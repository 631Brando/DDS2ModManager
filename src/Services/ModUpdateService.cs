namespace DDS2ModManager.Services;

/// What an update check found for one mod.
public record ModUpdateCheck(ModInfo Mod, bool Succeeded, string? Tag, string? Notes, GitHubAsset? Asset)
{
    public bool HasUpdate => Tag != null && Asset != null;
}

/// Checks mods that declared an update source for newer releases, and downloads them.
///
/// Deliberately conservative, because this ends with executable content on someone's machine:
///
///   - Only mods whose author opted in are ever checked.
///   - Only github.com is accepted as a source (GitHubUrlParser).
///   - Nothing is installed without the user agreeing, whatever their trust settings say. Trust
///     changes how much the prompt has to explain, not whether there is one.
///   - A release with an ambiguous set of assets is skipped rather than guessed at.
///
/// GitHub's unauthenticated API allows 60 requests an hour per address, and each mod costs one
/// request, so checks are spaced out and results are cached for the rest of the session. A user
/// with thirty mods checking on every launch would otherwise exhaust the limit and start getting
/// failures that look like the feature being broken.
public class ModUpdateService
{
    private readonly GitHubReleaseService _github = new();

    /// Archive types the installer can actually handle - see ModInstallerService.
    private static readonly string[] InstallableExtensions = { ".zip", ".7z", ".rar", ".pak" };

    /// Results from this session, keyed by owner/repo, so several mods from one repository - or a
    /// second check in the same session - don't each cost an API request.
    private readonly Dictionary<string, GitHubReleaseInfo?> _releaseCache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<List<ModUpdateCheck>> CheckAllAsync(IEnumerable<ModInfo> mods, IProgress<string>? progress = null)
    {
        var results = new List<ModUpdateCheck>();
        var checkable = mods.Where(m => m.HasUpdateSource).ToList();

        if (checkable.Count == 0) return results;

        LoggingService.Instance.Info($"Checking {checkable.Count} mod(s) for updates...");

        foreach (var mod in checkable)
        {
            progress?.Report($"Checking {mod.Name}...");
            results.Add(await CheckAsync(mod));
        }

        var found = results.Count(r => r.HasUpdate);
        LoggingService.Instance.Info(found == 0
            ? "No mod updates available."
            : $"{found} mod update(s) available.");

        return results;
    }

    public async Task<ModUpdateCheck> CheckAsync(ModInfo mod)
    {
        var source = mod.UpdateSource;
        if (source is not { IsUsable: true }) return new ModUpdateCheck(mod, false, null, null, null);

        var release = await GetLatestReleaseAsync(source);
        if (release == null) return new ModUpdateCheck(mod, false, null, null, null);

        if (!IsNewerThanInstalled(mod, source, release.TagName))
            return new ModUpdateCheck(mod, true, null, null, null);

        var asset = PickAsset(release, mod);
        if (asset == null)
        {
            LoggingService.Instance.Warn(
                $"'{mod.Name}' has a newer release ({release.TagName}) but no single downloadable file could be " +
                "identified in it, so it's being left alone. The author can name one with the \"asset\" field in " +
                "their .dds2mod.json.");
            return new ModUpdateCheck(mod, true, null, null, null);
        }

        return new ModUpdateCheck(mod, true, release.TagName, release.Body, asset);
    }

    private async Task<GitHubReleaseInfo?> GetLatestReleaseAsync(ModUpdateSource source)
    {
        var key = $"{source.Owner}/{source.Repo}";
        if (_releaseCache.TryGetValue(key, out var cached)) return cached;

        var release = await _github.GetLatestReleaseAsync(source.Owner, source.Repo);
        _releaseCache[key] = release;
        return release;
    }

    /// Compares the mod's declared version against the release tag when both are parseable, and
    /// otherwise falls back to "is this a tag we haven't already installed".
    ///
    /// The fallback matters: plenty of mods won't declare a version at all, and refusing to check
    /// those would make the feature useless for exactly the authors least likely to set it up.
    private static bool IsNewerThanInstalled(ModInfo mod, ModUpdateSource source, string tag)
    {
        var releaseVersion = AppUpdateService.ParseVersion(tag);
        var installedVersion = AppUpdateService.ParseVersion(source.Version);

        if (releaseVersion != null && installedVersion != null) return releaseVersion > installedVersion;

        // No usable version on one side or the other: treat a tag we've never recorded as newer,
        // and let the user decide from the release notes.
        return !string.Equals(tag, mod.AvailableUpdateTag, StringComparison.OrdinalIgnoreCase);
    }

    /// Picks the file to download. Named assets win; otherwise there has to be exactly one
    /// installable archive, because picking the wrong one would install the wrong mod.
    private static GitHubAsset? PickAsset(GitHubReleaseInfo release, ModInfo mod)
    {
        var named = mod.UpdateSource?.DeclaredAssetName;
        if (!string.IsNullOrWhiteSpace(named))
        {
            return release.Assets.FirstOrDefault(a =>
                a.Name.Equals(named, StringComparison.OrdinalIgnoreCase));
        }

        var installable = release.Assets
            .Where(a => InstallableExtensions.Contains(Path.GetExtension(a.Name), StringComparer.OrdinalIgnoreCase))
            .ToList();

        return installable.Count == 1 ? installable[0] : null;
    }

    /// Downloads an update to a temporary file and hands back the path. Installing it is the
    /// caller's job, through the same installer path a manual install uses - so an update goes
    /// through exactly the same type detection and conflict checking as anything else.
    public async Task<string?> DownloadAsync(GitHubAsset asset, IProgress<double>? progress = null)
    {
        var temp = Path.Combine(Path.GetTempPath(), "DDS2MM_modupdate_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);

        var destination = Path.Combine(temp, asset.Name);

        try
        {
            await _github.DownloadAssetAsync(asset.BrowserDownloadUrl, destination, progress);
            return destination;
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Error($"Couldn't download {asset.Name}: {ex.Message}");
            return null;
        }
    }
}
