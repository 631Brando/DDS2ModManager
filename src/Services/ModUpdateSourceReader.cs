using System.Text.Json.Serialization;
using CUE4Parse.FileProvider;

namespace DDS2ModManager.Services;

/// What a mod declares about where its updates come from.
public record ModUpdateDeclaration(string? UpdateUrl, string? Version, ModUpdateSource Source)
{
    public static readonly ModUpdateDeclaration None = new(null, null, ModUpdateSource.None);
}

/// The optional .dds2mod.json a mod can ship. Patch mods and lua mods have no ModActor to
/// carry the ModUpdateUrl variable, so this is their way in.
public class ModManifestFile
{
    [JsonPropertyName("modUpdateUrl")] public string? ModUpdateUrl { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }
}

/// Reads a mod's declared update source.
///
/// Mods are distributed through Nexus, but they declare their own update channel, and the
/// manager checks that instead of the Nexus API. No API key, no 2,500-a-day rate limit, and
/// no premium account needed to download - Nexus only issues download links to premium
/// members through its API, which would have made one-click updates a paid feature.
///
/// Two ways to declare it, because the obvious one only covers two thirds of mods:
///
///   LogicMods  a ModUpdateUrl string variable on the ModActor. Costs no extra file, which
///              is the whole appeal - modders ship the same .pak/.ucas/.utoc as before.
///   Everything a .dds2mod.json next to the mod. Patch mods are defined by NOT having a
///   else       ModActor (see ModAnalyzerService), so there is nowhere else to put it, and
///              lua mods ship folders already so one more file costs them nothing.
///
/// SECURITY. An update fetched from a URL the mod itself supplies has not been through
/// Nexus's virus scanning, which is the one guarantee Nexus actually provides. Three things
/// contain that:
///
///   1. The host allowlist below. An arbitrary URL field is a malware delivery channel; a
///      github.com-only field is a public repository someone can go and read.
///   2. Nothing is ever installed without the user seeing the URL and the release notes
///      (see ModUpdateService and the update prompt).
///   3. The URL is pinned at install time from the Nexus-downloaded copy, and a later
///      version pointing somewhere new is flagged rather than followed - see
///      ModInfo.UpdateUrlChanged.
public static class ModUpdateSourceReader
{
    /// The property name modders set on their ModActor. Fixed by agreement with the SDK's
    /// ModActor template - changing it here breaks every mod already published.
    public const string ModActorUrlProperty = "ModUpdateUrl";

    /// Optional companion, so the manager can tell which version is installed without
    /// guessing from a filename.
    public const string ModActorVersionProperty = "ModVersion";

    public const string ManifestSuffix = ".dds2mod.json";

    private static readonly JsonSerializerOptions ManifestOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// Only GitHub. Deliberately not a configurable allowlist: the point is that anyone can
    /// read the source of an update before it runs on their machine, and that stops being
    /// true the moment arbitrary hosts are permitted. Also requires https - an http update
    /// channel is trivially hijacked on a hostile network.
    public static bool IsAllowedUpdateUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps) return false;

        return uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("www.github.com", StringComparison.OrdinalIgnoreCase);
    }

    /// Pulls owner/repo out of a GitHub URL so GitHubReleaseService can be reused as-is.
    /// Returns false for anything that is not a repository URL.
    public static bool TryParseGitHubRepo(string? url, out string owner, out string repo)
    {
        owner = "";
        repo = "";
        if (!IsAllowedUpdateUrl(url)) return false;

        var segments = new Uri(url!.Trim()).AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2) return false;

        owner = segments[0];
        // Tolerate the URL people actually copy out of the browser: a trailing .git, or a
        // deep link like /owner/repo/releases/tag/v1.2.0.
        repo = segments[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? segments[1][..^4]
            : segments[1];

        return owner.Length > 0 && repo.Length > 0;
    }

    /// Reads ModUpdateUrl off the mod's ModActor.
    ///
    /// The provider must already be mounted against the real game - a mod's IoStore container
    /// cannot resolve anything on its own (see ModAnalyzerService). This is called from inside
    /// the mount that ModAnalyzerService already performs, so it costs no extra work.
    public static ModUpdateDeclaration ReadFromModActor(
        AbstractFileProvider provider, IEnumerable<string> modAssetPaths, string modName)
    {
        var modActorPath = modAssetPaths.FirstOrDefault(p =>
            Path.GetFileNameWithoutExtension(p).Equals("ModActor", StringComparison.OrdinalIgnoreCase));
        if (modActorPath == null) return ModUpdateDeclaration.None;

        try
        {
            var pkg = provider.LoadPackage(modActorPath);

            // A Blueprint's default values live on its class default object, which is one of
            // the exports in the package - so rather than hunting for the CDO specifically,
            // ask every export and take the first that answers. Costs nothing (a ModActor
            // package has a handful of exports) and survives Epic moving the CDO around.
            string? url = null;
            string? version = null;

            foreach (var export in pkg.GetExports())
            {
                if (url == null &&
                    export.TryGetValue<string>(out var u, ModActorUrlProperty) &&
                    !string.IsNullOrWhiteSpace(u))
                {
                    url = u.Trim();
                }

                if (version == null &&
                    export.TryGetValue<string>(out var v, ModActorVersionProperty) &&
                    !string.IsNullOrWhiteSpace(v))
                {
                    version = v.Trim();
                }

                if (url != null && version != null) break;
            }

            if (url == null) return ModUpdateDeclaration.None;

            if (!IsAllowedUpdateUrl(url))
            {
                // Loud, not silent. A rejected URL is either a mistake the author wants to
                // know about or an attempt to point the updater somewhere it should not go,
                // and both are worth a line in the log.
                LoggingService.Instance.Warn(
                    $"'{modName}' declares an update URL that isn't a GitHub address, so it was ignored: {url}");
                return ModUpdateDeclaration.None;
            }

            return new ModUpdateDeclaration(url, version, ModUpdateSource.ModActor);
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Warn($"Couldn't read {ModActorUrlProperty} from '{modName}': {ex.Message}");
            return ModUpdateDeclaration.None;
        }
    }

    /// Re-reads the manifest for a mod that is already installed.
    ///
    /// Needed because declarations are captured at install time, and almost everyone's mods
    /// were installed before this existed - without this, the feature would look broken to
    /// every existing user until they reinstalled everything.
    ///
    /// The awkward part is that "the mod's folder" is not one thing:
    ///
    ///   lua mods  own a directory, and that directory is in InstallFiles.
    ///   pak mods  are loose files SHARING Content\Paks\LogicMods with every other pak mod,
    ///             so a recursive search there would find a neighbour's manifest and cheerfully
    ///             attribute it to the wrong mod - and then offer an update from someone else's
    ///             repository. Hence the name match for those.
    ///
    /// ModActor-declared URLs are not re-read here: that needs a full CUE4Parse mount of the
    /// game, which is what Deep Scan is for.
    public static ModUpdateDeclaration ReadForInstalledMod(ModInfo mod)
    {
        // A directory in InstallFiles means the mod owns it outright - lua mods.
        var ownFolder = mod.InstallFiles.FirstOrDefault(Directory.Exists);
        if (ownFolder != null) return ReadFromManifest(ownFolder);

        if (string.IsNullOrWhiteSpace(mod.InstallPath) || !Directory.Exists(mod.InstallPath))
            return ModUpdateDeclaration.None;

        // Shared folder: only a manifest named after this mod can safely be claimed by it.
        var named = Path.Combine(mod.InstallPath, mod.Name + ManifestSuffix);
        if (!File.Exists(named)) return ModUpdateDeclaration.None;

        return ReadManifestFile(named);
    }

    /// Reads a .dds2mod.json from anywhere inside the mod's folder.
    public static ModUpdateDeclaration ReadFromManifest(string modFolderPath)
    {
        try
        {
            if (!Directory.Exists(modFolderPath)) return ModUpdateDeclaration.None;

            var manifest = Directory
                .EnumerateFiles(modFolderPath, "*" + ManifestSuffix, SearchOption.AllDirectories)
                .FirstOrDefault();
            if (manifest == null) return ModUpdateDeclaration.None;

            return ReadManifestFile(manifest);
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Warn($"Couldn't read a {ManifestSuffix} manifest from '{modFolderPath}': {ex.Message}");
            return ModUpdateDeclaration.None;
        }
    }

    private static ModUpdateDeclaration ReadManifestFile(string manifestPath)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<ModManifestFile>(File.ReadAllText(manifestPath), ManifestOptions);
            if (parsed?.ModUpdateUrl == null) return ModUpdateDeclaration.None;

            if (!IsAllowedUpdateUrl(parsed.ModUpdateUrl))
            {
                LoggingService.Instance.Warn(
                    $"'{Path.GetFileName(manifestPath)}' declares an update URL that isn't a GitHub address, so it was ignored: {parsed.ModUpdateUrl}");
                return ModUpdateDeclaration.None;
            }

            return new ModUpdateDeclaration(parsed.ModUpdateUrl.Trim(), parsed.Version?.Trim(), ModUpdateSource.Manifest);
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Warn($"Couldn't read '{Path.GetFileName(manifestPath)}': {ex.Message}");
            return ModUpdateDeclaration.None;
        }
    }
}
