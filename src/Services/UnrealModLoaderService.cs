namespace DDS2ModManager.Services;

/// Installs, updates and removes UnrealModLoader (github.com/RussellJerome/UnrealModLoader) - the
/// loader DDS1's mod scene runs, and the classic Blueprint/C++ mod loader for UE4 games generally.
///
/// UML can load itself two ways: a launcher started every time, or its "AutoInjector" - a proxy
/// xinput1_3.dll the game loads by itself, which reads ModLoaderInfo.ini beside the game exe for the
/// ABSOLUTE path of UnrealEngineModLoader.dll. This installs the AutoInjector with the loader flat in
/// Binaries\Win64, because that is the one layout everything in UML agrees on:
///   - the loader reads ModLoaderInfo.ini from its own folder as well ([DEBUG] UseConsole), so with
///     both in Win64 one file carries both sections;
///   - it reads Profiles\&lt;exe name&gt;.profile beside itself, and without one does nothing at all;
///   - it loads DLL core mods from &lt;project&gt;\Content\CoreMods, derived from the game exe's path,
///     and creates that folder itself on first launch.
///
/// The launcher is deliberately NOT copied into the game: it is an .exe, and an extra .exe in
/// Binaries\Win64 is exactly what finding "the game's executable" has to guess between.
public class UnrealModLoaderService
{
    public const string Owner = "RussellJerome";
    public const string Repo = "UnrealModLoader";

    public const string LoaderInfoFileName = "ModLoaderInfo.ini";
    public const string LoaderDllName = "UnrealEngineModLoader.dll";
    public const string ProxyDllName = "xinput1_3.dll";
    public const string ProfilesFolderName = "Profiles";
    public const string ManifestFileName = ".dds2modmanager_uml.json";

    private readonly GitHubReleaseService _github = new();

    public Task<GitHubReleaseInfo?> GetLatestReleaseAsync() => _github.GetLatestReleaseAsync(Owner, Repo);

    /// UML's releases carry one archive, named UnrealModLoader_V&lt;version&gt;.rar - and only one served
    /// from UML's own release page (github.com/RussellJerome/UnrealModLoader/releases) is taken.
    public static GitHubAsset? FindAsset(GitHubReleaseInfo release) =>
        release.Assets.FirstOrDefault(a =>
            a.Name.StartsWith("UnrealModLoader", StringComparison.OrdinalIgnoreCase)
            && ArchiveExtractionService.IsSupportedArchive(a.Name)
            && IsOfficialDownload(a.BrowserDownloadUrl));

    /// Whether a URL is a release download from UnrealModLoader's official repository. What gets
    /// installed is a DLL injected into the game, so the address is checked exactly - scheme, host
    /// and path - rather than trusted because an API response carried it.
    public static bool IsOfficialDownload(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
        && uri.IsDefaultPort
        && string.IsNullOrEmpty(uri.UserInfo)
        && uri.AbsolutePath.StartsWith($"/{Owner}/{Repo}/releases/download/", StringComparison.OrdinalIgnoreCase);

    public static string ManifestPath(GameInstallation game) => Path.Combine(game.Win64Path, ManifestFileName);

    public static UnrealModLoaderManifest? ReadManifest(GameInstallation game)
    {
        try
        {
            var path = ManifestPath(game);
            return File.Exists(path)
                ? JsonSerializer.Deserialize<UnrealModLoaderManifest>(File.ReadAllText(path))
                : null;
        }
        catch
        {
            return null; // a corrupt manifest reads as "not ours", which only ever refuses
        }
    }

    /// The profile name UML looks for: the RUNNING executable's name without its extension. UML takes
    /// it from GetModuleFileName(NULL), which is the Win64 shipping exe, not the launcher stub above it.
    public static string? ProfileNameFor(GameInstallation game) =>
        game.ExecutablePath is { } exe ? Path.GetFileNameWithoutExtension(exe) + ".profile" : null;

    public static UnrealModLoaderStatus GetStatus(GameInstallation game)
    {
        var manifest = ReadManifest(game);
        var installed = File.Exists(Path.Combine(game.Win64Path, LoaderInfoFileName));
        var choice = LoaderCompatibility.ForUnrealModLoader(game.Profile);

        return new UnrealModLoaderStatus
        {
            IsInstalled = installed,
            IsManagedByUs = installed && manifest != null,
            InstalledVersion = manifest?.Version,
            CanInstall = choice.Available,
            InstallBlockedReason = choice.BlockedReason,
            Warning = choice.Warning
        };
    }

    /// Why installing (or updating) can't go ahead on this install right now, or null when it can.
    /// Refused on the service's own account, like UE4SS: a hidden button is presentation.
    public static string? WhyNot(GameInstallation game)
    {
        var choice = LoaderCompatibility.ForUnrealModLoader(game.Profile);
        if (!choice.Available) return choice.BlockedReason;

        if (!game.IsInstalled)
            return $"{game.RootPath} doesn't contain an installed game (no executable or game paks) - it looks like " +
                   "files left behind after an uninstall.";

        if (ProfileNameFor(game) == null)
            return "The game's executable wasn't found in Binaries\\Win64, and UnrealModLoader needs its name to find its profile.";

        var manifest = ReadManifest(game);

        // Someone else's working setup. Its LoaderPath may point at a loader elsewhere on disk, and
        // overwriting the ini would quietly swap it for ours.
        if (manifest == null && File.Exists(Path.Combine(game.Win64Path, LoaderInfoFileName)))
            return "UnrealModLoader is already set up here (ModLoaderInfo.ini exists), but not by this manager, so it's " +
                   "left exactly as it is.";

        // The launcher route leaves no ini - just UML's files beside the game. Installing over them
        // would put them on this manager's record, and Remove would then delete a setup that worked.
        if (manifest == null && (File.Exists(Path.Combine(game.Win64Path, LoaderDllName))
                                 || File.Exists(Path.Combine(game.Win64Path, "UnrealEngineModLauncher.exe"))))
            return "UnrealModLoader's files are already in Binaries\\Win64 (set up to run through its launcher), but " +
                   "not by this manager, so they're left exactly as they are.";

        // xinput1_3.dll is a common proxy name. If it isn't the one we put there, another tool owns it.
        var proxy = Path.Combine(game.Win64Path, ProxyDllName);
        var ours = manifest?.Files.Contains(ProxyDllName, StringComparer.OrdinalIgnoreCase) == true;
        if (File.Exists(proxy) && !ours)
            return $"Binaries\\Win64 already has an {ProxyDllName} that this manager didn't install - another tool loads " +
                   "itself that way. Installing UnrealModLoader would replace it.";

        if (IsLocked(Path.Combine(game.Win64Path, LoaderDllName)) || IsLocked(proxy))
            return "UnrealModLoader's files are in use - close the game first.";

        return null;
    }

    public async Task<bool> InstallAsync(GameInstallation game, GitHubReleaseInfo release, GitHubAsset asset,
        IProgress<double>? progress = null)
    {
        var log = LoggingService.Instance;

        if (WhyNot(game) is { } refusal)
        {
            log.Error($"Refused to install UnrealModLoader: {refusal}");
            return false;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "DDS2MM_UML_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var archivePath = Path.Combine(tempDir, asset.Name);
            log.Info($"Downloading {asset.Name} ({asset.Size / 1024.0:F0} KB)...");
            await _github.DownloadAssetAsync(asset.BrowserDownloadUrl, archivePath, progress);

            var extractDir = Path.Combine(tempDir, "extracted");
            ArchiveExtractionService.ExtractToDirectory(archivePath, extractDir);

            return ApplyExtracted(game, extractDir, release.TagName, asset.Name);
        }
        catch (Exception ex)
        {
            log.Error($"UnrealModLoader install failed: {ex.Message}");
            return false;
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    /// Puts an extracted UML release onto the game. Split from the download so the file layout can be
    /// tested against an archive built on disk.
    public static bool ApplyExtracted(GameInstallation game, string extractDir, string tag, string assetName)
    {
        var log = LoggingService.Instance;

        if (WhyNot(game) is { } refusal)
        {
            log.Error($"Refused to install UnrealModLoader: {refusal}");
            return false;
        }

        // Found by name rather than by fixed position, so a release that adds a folder around
        // its contents still installs.
        var loaderDll = Directory.GetFiles(extractDir, LoaderDllName, SearchOption.AllDirectories).FirstOrDefault();
        var proxyDll = Directory.GetFiles(extractDir, ProxyDllName, SearchOption.AllDirectories).FirstOrDefault();
        if (loaderDll == null || proxyDll == null)
        {
            log.Error($"{assetName} didn't contain the expected {LoaderDllName} and AutoInjector {ProxyDllName}. " +
                      "UnrealModLoader's release layout may have changed - install it by hand and report this.");
            return false;
        }

        var previous = ReadManifest(game);
        var written = new List<string>();
        Directory.CreateDirectory(game.Win64Path);

        File.Copy(loaderDll, Path.Combine(game.Win64Path, LoaderDllName), true);
        written.Add(LoaderDllName);
        File.Copy(proxyDll, Path.Combine(game.Win64Path, ProxyDllName), true);
        written.Add(ProxyDllName);

        // Every profile UML ships, so a game it already knows uses its author's tuned profile - but
        // never over one already on disk. A profile is the file a player edits to make mods spawn,
        // and a Reinstall that quietly put the stock one back would undo exactly that fix. One that
        // was here before any install of ours isn't put on record either, so Remove can't take it.
        var profilesDest = Path.Combine(game.Win64Path, ProfilesFolderName);
        Directory.CreateDirectory(profilesDest);
        var profileName = ProfileNameFor(game)!;
        var profilePath = Path.Combine(profilesDest, profileName);
        var gameProfileExisted = File.Exists(profilePath);

        bool OnRecord(string relative) =>
            previous?.Files.Contains(relative, StringComparer.OrdinalIgnoreCase) == true;

        var shippedProfiles = Path.Combine(Path.GetDirectoryName(loaderDll)!, ProfilesFolderName);
        var shipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(shippedProfiles))
        {
            foreach (var file in Directory.GetFiles(shippedProfiles, "*.profile"))
            {
                var name = Path.GetFileName(file);
                var relative = Path.Combine(ProfilesFolderName, name);
                shipped.Add(name);

                if (File.Exists(Path.Combine(profilesDest, name)))
                {
                    if (OnRecord(relative)) written.Add(relative);
                    continue;
                }

                File.Copy(file, Path.Combine(profilesDest, name));
                written.Add(relative);
            }
        }

        var gameProfile = Path.Combine(ProfilesFolderName, profileName);
        var generated = previous?.GeneratedProfile;

        if (gameProfileExisted)
        {
            // Ours from an earlier install, or one the player wrote - kept either way, because a
            // profile someone tuned by hand is the most valuable file in the folder.
            log.Info($"Kept the existing {profileName}.");
            if (OnRecord(gameProfile) && !written.Contains(gameProfile, StringComparer.OrdinalIgnoreCase))
                written.Add(gameProfile);
        }
        else if (shipped.Contains(profileName))
        {
            log.Info($"UnrealModLoader ships a profile for this game ({profileName}), so that one is used.");
            generated = null;
        }
        else
        {
            var (major, minor) = LoaderCompatibility.VersionOf(game.Profile.EngineVersion);
            File.WriteAllText(profilePath, LoaderCompatibility.BuildUnrealModLoaderProfile(major, minor));
            written.Add(gameProfile);
            generated = profileName;
            log.Info($"UnrealModLoader ships no profile for this game, so one was written for UE {major}.{minor}: " +
                     $"Binaries\\Win64\\{ProfilesFolderName}\\{profileName}.");
        }

        var infoPath = Path.Combine(game.Win64Path, LoaderInfoFileName);
        File.WriteAllText(infoPath, BuildLoaderInfo(Path.Combine(game.Win64Path, LoaderDllName), ReadUseConsole(infoPath)));
        written.Add(LoaderInfoFileName);

        var manifest = new UnrealModLoaderManifest
        {
            Version = tag,
            AssetName = assetName,
            InstalledAt = DateTime.Now,
            Files = written,
            GeneratedProfile = generated
        };
        File.WriteAllText(ManifestPath(game), JsonSerializer.Serialize(manifest));

        log.Success($"UnrealModLoader {tag} installed. Launch the game normally - it loads itself " +
                    $"through {ProxyDllName}. Press F1 in game for its menu.");
        return true;
    }

    /// Takes out exactly what InstallAsync wrote. LogicMods and Content\CoreMods are the player's
    /// mods, not the loader, and are never touched.
    public static bool Remove(GameInstallation game)
    {
        var log = LoggingService.Instance;
        var manifest = ReadManifest(game);

        if (manifest == null)
        {
            log.Error("UnrealModLoader wasn't installed by this manager, so its full file list isn't known and it " +
                      "isn't removed automatically. Remove it the way you installed it.");
            return false;
        }

        if (IsLocked(Path.Combine(game.Win64Path, LoaderDllName)) || IsLocked(Path.Combine(game.Win64Path, ProxyDllName)))
        {
            log.Error("UnrealModLoader's files are in use - close the game first.");
            return false;
        }

        // This game's own profile stays: it may hold the one flag that made mods spawn, it is a few
        // hundred bytes, and a reinstall picks it straight back up.
        var keep = ProfileNameFor(game) is { } own ? Path.Combine(ProfilesFolderName, own) : null;

        var ok = true;
        foreach (var relative in manifest.Files)
        {
            if (keep != null && string.Equals(relative, keep, StringComparison.OrdinalIgnoreCase)) continue;

            if (ResolveInside(game.Win64Path, relative) is not { } path)
            {
                log.Warn($"Skipped '{relative}' from UnrealModLoader's file list - it points outside Binaries\\Win64.");
                continue;
            }

            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) { ok = false; log.Warn($"Couldn't remove {relative}: {ex.Message}"); }
        }

        try
        {
            var profiles = Path.Combine(game.Win64Path, ProfilesFolderName);
            if (Directory.Exists(profiles) && !Directory.EnumerateFileSystemEntries(profiles).Any())
                Directory.Delete(profiles);
            if (ok) File.Delete(ManifestPath(game));
        }
        catch (Exception ex) { log.Warn($"Couldn't finish tidying up: {ex.Message}"); }

        if (ok) log.Success("Removed UnrealModLoader. Your logic mods and core mods were left where they are" +
                            (keep != null ? $", and so was its profile for this game ({keep})." : "."));
        return ok;
    }

    /// The single ModLoaderInfo.ini both halves read: the AutoInjector's [INFO] and the loader's [DEBUG].
    /// UseConsole defaults off - UML ships it on, which opens a console window beside every game launch.
    public static string BuildLoaderInfo(string loaderDllPath, bool useConsole) => string.Join("\r\n",
        "[INFO]",
        $"LoaderPath={loaderDllPath}",
        "",
        "[DEBUG]",
        "#Enables the default console, used for debugging and finding errors, Set to 1 for true",
        $"UseConsole={(useConsole ? 1 : 0)}",
        "");

    /// The player's console choice from an earlier install, so updating doesn't reset it.
    private static bool ReadUseConsole(string infoPath)
    {
        try
        {
            if (!File.Exists(infoPath)) return false;
            return File.ReadLines(infoPath)
                .Select(l => l.Trim())
                .Any(l => l.StartsWith("UseConsole", StringComparison.OrdinalIgnoreCase)
                          && l.Split('=', 2) is [_, var v] && v.Trim() == "1");
        }
        catch { return false; }
    }

    /// A manifest entry resolved under Win64, or null when it escapes it or names the game itself.
    private static string? ResolveInside(string win64, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) return null;

        var root = Path.GetFullPath(win64).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;

        return full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? null : full;
    }

    /// Whether a file is held open by a running game. A missing file is not locked.
    public static bool IsLocked(string path)
    {
        if (!File.Exists(path)) return false;
        try
        {
            using var _ = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return false; } // read-only is a different problem, reported by the copy
    }
}
