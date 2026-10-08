namespace DDS2ModManager.Services;

/// Handles detecting, installing and updating UE4SS, from either release line.
///
/// Experimental ("experimental-latest") is recommended everywhere and preselected. Stable is v3.0.1,
/// which ships the OLDER layout - UE4SS.dll, its settings and Mods\ loose in Binaries\Win64 rather
/// than under ue4ss\ - so it is installed that way, with a manifest of every file put there, and a
/// later move to experimental carries the player's mods across into ue4ss\Mods.
public class UE4SSManagerService
{
    private const string Owner = "UE4SS-RE";
    private const string Repo = "RE-UE4SS";
    private const string Tag = "experimental-latest";

    /// The archive of every experimental build ever published, as opposed to the rolling
    /// "experimental-latest" tag whose four assets are replaced in place.
    ///
    /// Nearly 900 assets, ~300 builds per channel for the 3.0.1 line alone. This is what makes
    /// going back to a specific build possible at all: a user broken by an update can be returned
    /// to the exact build they were on, by name, even if they never kept a copy of it.
    private const string ArchiveTag = "experimental";

    private readonly GitHubReleaseService _github = new();

    public Task<GitHubReleaseInfo?> GetLatestExperimentalReleaseAsync() =>
        _github.GetReleaseByTagAsync(Owner, Repo, Tag);

    /// Stable is fetched by its TAG, not as GitHub's "latest". Everything said about it - the range
    /// it supports, the older layout, the DDS2 crash, the label - is about v3.0.1, so whatever
    /// UE4SS-RE marks as latest next must not be installed under that description.
    public Task<GitHubReleaseInfo?> GetLatestReleaseAsync(UE4SSChannel channel) =>
        channel == UE4SSChannel.Stable
            ? _github.GetReleaseByTagAsync(Owner, Repo, LoaderCompatibility.StableVersion)
            : GetLatestExperimentalReleaseAsync();

    /// Every experimental build that can actually be installed, newest first.
    ///
    /// Filtered rather than listed wholesale: the archive also carries much older asset shapes from
    /// an era with a different layout, and this installer expects dwmapi.dll beside a ue4ss\ folder.
    /// A build that cannot install is worse than one that is absent - it fails after the download.
    public async Task<IReadOnlyList<UE4SSBuild>> GetArchivedBuildsAsync()
    {
        var release = await _github.GetReleaseByTagAsync(Owner, Repo, ArchiveTag);
        if (release == null) return [];

        var builds = release.Assets
            .Select(a => UE4SSBuild.FromAssetName(a.Name, a.BrowserDownloadUrl, a.Size))
            .Where(b => b != null)
            .Select(b => b!)
            .ToList();

        builds.Sort(UE4SSBuild.Newest);
        return builds;
    }

    /// Installs one specific build from the archive.
    ///
    /// Goes through exactly the same path as an ordinary update - same preserve list, same settings
    /// merge, same copy-aside of what it replaces - so going back is as recoverable as going
    /// forward, and a rollback that turns out to be wrong can itself be undone.
    public Task<bool> InstallSpecificBuildAsync(GameInstallation game, UE4SSBuild build,
        IProgress<double>? progress = null)
    {
        var release = new GitHubReleaseInfo { TagName = ArchiveTag, Name = build.AssetName };
        var asset = new GitHubAsset
        {
            Name = build.AssetName,
            BrowserDownloadUrl = build.DownloadUrl,
            Size = build.Size
        };

        return InstallOrUpdateAsync(game, release, asset, progress);
    }

    /// The release always ships 6 assets: the real UE4SS_v*.zip, zCustomGameConfigs.zip,
    /// zDEV-UE4SS_v*.zip, zMapGenBP.zip, and two source archives. This is the standard build -
    /// starts with "UE4SS_" (not "z...") and ends in .zip. No console window opens with this one.
    public GitHubAsset? FindMainAsset(GitHubReleaseInfo release) =>
        release.Assets.FirstOrDefault(a =>
            a.Name.StartsWith("UE4SS_", StringComparison.OrdinalIgnoreCase) &&
            a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

    /// The "zDEV-UE4SS_v*.zip" asset - functionally identical for mods, but opens a console
    /// window showing live UE4SS logs while the game runs. The build picker (shown before every
    /// install/update) is what tells the user about that difference - this method just finds it.
    public GitHubAsset? FindDevAsset(GitHubReleaseInfo release) =>
        release.Assets.FirstOrDefault(a =>
            a.Name.StartsWith("zDEV-UE4SS_", StringComparison.OrdinalIgnoreCase) &&
            a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

    public GitHubAsset? FindAsset(GitHubReleaseInfo release, bool devBuild) =>
        devBuild ? FindDevAsset(release) : FindMainAsset(release);

    public UE4SSInstallInfo GetCurrentStatus(GameInstallation game)
    {
        var info = new UE4SSInstallInfo();

        // Detection covers both layouts. The old check looked only for Binaries\Win64\ue4ss, so a
        // perfectly working UE4SS in the older layout read as "not installed" - which lit up the
        // Install button and would have dropped a second, incompatible copy on top of it.
        var detected = new ModLoaderService().Detect(game, ModLoaders.UE4SS);
        info.IsInstalled = detected is { IsInstalled: true };
        info.Layout = detected?.Layout ?? LoaderLayout.None;
        info.DetectedVersion = detected?.Version;

        info.CanInstall = MayInstall(game);
        if (!info.CanInstall)
            info.InstallBlockedReason = game.Profile.IsBuiltIn
                ? $"{game.Profile.DisplayName} needs a UE4SS build made for its engine version, and that build " +
                  "isn't published as a download - the standard ones crash this game on startup." +
                  (game.Profile.InstallableLoaders.HasFlag(ModLoaders.UnrealModLoader)
                      ? " UnrealModLoader, which this game's mods are made for, can be installed instead."
                      : " Install it yourself if you need it; this manager works with whatever is already there.")
                // A generic game is gated by the engine version read from its files.
                : LoaderCompatibility.ForUE4SS(game.Profile, UE4SSChannel.Experimental).BlockedReason
                  ?? "This manager doesn't install UE4SS on this game.";

        // The modern manifest describes the modern install; only without one is the older layout's
        // consulted, so a migration that left both can never report the stale one.
        var manifest = ReadManifest(GetManifestPath(game))
                       ?? (info.Layout == LoaderLayout.Legacy ? ReadLegacyManifest(game) : null);

        // An older-layout UE4SS installed by hand is read as-is and never replaced, so nothing may be
        // offered for it - otherwise its "update available" never clears and every Update downloads
        // a build only to refuse to install it.
        if (info.Layout == LoaderLayout.Legacy && manifest == null && info.CanInstall)
        {
            info.CanInstall = false;
            info.InstallBlockedReason =
                "UE4SS is installed here in its older layout (UE4SS.dll directly in Binaries\\Win64), by hand. It's used " +
                "as it is and never replaced - remove it yourself first if you want this manager to install one.";
        }
        if (manifest != null)
        {
            info.IsManagedByUs = true;
            info.Channel = manifest.Channel;
            info.IsConfirmedExperimental = manifest.Channel == UE4SSChannel.Experimental;
            info.InstalledVersionTag = manifest.InstalledTag;
            info.InstalledAssetName = manifest.InstalledAssetName;
            info.InstalledAt = manifest.InstalledAt;
        }

        return info;
    }

    private static string GetManifestPath(GameInstallation game) =>
        Path.Combine(game.UE4SSRootPath, ModLoaderService.ManifestFileName);

    /// Where the manifest of a stable (older-layout) install lives: beside the files it lists.
    private static string LegacyManifestPath(GameInstallation game) =>
        Path.Combine(game.Win64Path, ModLoaderService.ManifestFileName);

    private static UE4SSManifest? ReadManifest(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<UE4SSManifest>(File.ReadAllText(path)) : null;
        }
        catch
        {
            return null; // corrupt manifest - treat as unmanaged
        }
    }

    /// The manifest of a stable install this manager made in the older layout, if there is one.
    public static UE4SSManifest? ReadLegacyManifest(GameInstallation game) => ReadManifest(LegacyManifestPath(game));

    /// The files a stable install put in Binaries\Win64, as absolute paths - plus the manifest itself.
    ///
    /// Each entry is checked to stay inside Win64 and never to be an executable, so a damaged or
    /// hand-edited manifest can't turn "remove UE4SS" into "remove the game".
    public static string[] LegacyFilesOnRecord(GameInstallation game, UE4SSManifest manifest)
    {
        var root = Path.GetFullPath(game.Win64Path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var files = new List<string>();

        foreach (var relative in manifest.Files)
        {
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) continue;
            var full = Path.GetFullPath(Path.Combine(root, relative));
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
            if (full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
            files.Add(full);
        }

        files.Add(LegacyManifestPath(game));
        return files.ToArray();
    }

    /// The one permission to put UE4SS on a game, enforced HERE as well as in the view model.
    ///
    /// The view-model checks are presentation: a hidden button, a guard on a command. This is the
    /// method that actually writes a DLL injector into a game, so it refuses on its own account -
    /// any future caller, or a status that hasn't loaded yet, must not be enough to get past it.
    public static bool MayInstall(GameInstallation game) =>
        game.Profile.InstallableLoaders.HasFlag(ModLoaders.UE4SS);

    /// MayInstall, plus: the game has to really be installed. Writing UE4SS into the husk an
    /// uninstall left behind would "succeed" into a folder with no game in it.
    private static bool RefuseIfNotPermitted(GameInstallation game, string what)
    {
        if (!MayInstall(game))
        {
            LoggingService.Instance.Error(
                $"Refused to {what} UE4SS on {game.Profile.DisplayName}: this manager doesn't install UE4SS on that game.");
            return true;
        }

        if (!game.IsInstalled)
        {
            LoggingService.Instance.Error(
                $"Refused to {what} UE4SS: {game.RootPath} doesn't contain an installed game " +
                "(no executable or game paks) - it looks like files left behind after an uninstall.");
            return true;
        }

        return false;
    }

    /// Also refuses while the game is running: UE4SS.dll and its proxy are held open by the game, and
    /// an update that fails halfway through a copy leaves a mix of two builds.
    private static bool RefuseIfRunning(GameInstallation game, string what)
    {
        var locked = new[]
        {
            Path.Combine(game.UE4SSRootPath, "UE4SS.dll"),
            Path.Combine(game.Win64Path, "UE4SS.dll"),
            Path.Combine(game.Win64Path, "dwmapi.dll")
        }.FirstOrDefault(UnrealModLoaderService.IsLocked);

        if (locked == null) return false;

        LoggingService.Instance.Error(
            $"Refused to {what} UE4SS: {Path.GetFileName(locked)} is in use, which means the game is running. Close it first.");
        return true;
    }

    public async Task<bool> InstallOrUpdateAsync(GameInstallation game, GitHubReleaseInfo release, GitHubAsset asset,
        IProgress<double>? progress = null, UE4SSChannel channel = UE4SSChannel.Experimental)
    {
        if (RefuseIfNotPermitted(game, "install") || RefuseIfRunning(game, "install")) return false;

        var log = LoggingService.Instance;

        // Known before the download, so it's refused before one: ApplyExtracted refuses this too,
        // but only after fetching a build it was never going to install.
        if (File.Exists(Path.Combine(game.Win64Path, "UE4SS.dll")) && ReadLegacyManifest(game) == null)
        {
            log.Error("UE4SS is already installed here in its older layout, not by this manager, so it's left as it is. " +
                      "Remove it by hand first if you want this manager to install one.");
            return false;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "DDS2MM_UE4SS_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var zipPath = Path.Combine(tempDir, asset.Name);

        try
        {
            log.Info($"Downloading {asset.Name} ({asset.Size / 1024.0 / 1024.0:F1} MB)...");
            await _github.DownloadAssetAsync(asset.BrowserDownloadUrl, zipPath, progress);
            log.Success("Download complete. Extracting...");

            var extractDir = Path.Combine(tempDir, "extracted");
            ArchiveExtractionService.ExtractToDirectory(zipPath, extractDir);

            return ApplyExtracted(game, extractDir, release.TagName, asset.Name, channel);
        }
        catch (Exception ex)
        {
            log.Error($"UE4SS install failed: {ex.Message}");
            return false;
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    /// Files inside a UE4SS payload that belong to the USER, not to the release, relative to the
    /// folder that holds Mods\. Every other file in the archive is overwritten, which is what
    /// updating UE4SS means.
    ///
    /// load_order.txt is here because only a person ever writes it - BPModLoaderMod's own Lua just
    /// reads it - and losing it silently drops that loader back to loading Blueprint mods in an
    /// arbitrary order, which presents as a mod that "sometimes" works.
    private static readonly string[] UserOwnedFiles =
    [
        Path.Combine("Mods", "mods.txt"),
        Path.Combine("Mods", "mods.json"),
        Path.Combine("Mods", "BPModLoaderMod", "load_order.txt")
    ];

    /// Puts an extracted UE4SS release onto the game. Split from the download so it can be tested
    /// against a payload built on disk, and so both layouts go through one set of rules.
    ///
    /// Two archive shapes exist. The modern one (experimental) is dwmapi.dll beside a ue4ss\ folder.
    /// The older one (stable v3.0.1) is dwmapi.dll, UE4SS.dll, its settings and Mods\ all at the root,
    /// installed loose into Binaries\Win64. Which one arrived decides everything below.
    public static bool ApplyExtracted(GameInstallation game, string extractDir, string tag, string assetName,
        UE4SSChannel channel)
    {
        var log = LoggingService.Instance;

        var dwmapiSrc = Path.Combine(extractDir, "dwmapi.dll");
        var modernSrc = Path.Combine(extractDir, "ue4ss");
        var incomingModern = File.Exists(dwmapiSrc) && Directory.Exists(modernSrc);
        var incomingLegacy = File.Exists(dwmapiSrc) && !incomingModern && File.Exists(Path.Combine(extractDir, "UE4SS.dll"));

        if (!incomingModern && !incomingLegacy)
        {
            log.Error("Downloaded archive layout didn't match either UE4SS layout (dwmapi.dll beside a ue4ss\\ folder, or " +
                      "beside UE4SS.dll). UE4SS-RE may have changed the release layout - install manually and report this.");
            return false;
        }

        var legacyDll = Path.Combine(game.Win64Path, "UE4SS.dll");
        var legacyManifest = ReadLegacyManifest(game);

        // An older-layout UE4SS somebody installed by hand is what DDS1-era guides produce, and its
        // file list is unknown - so nothing is ever put on top of it or moved out from under it.
        if (File.Exists(legacyDll) && legacyManifest == null)
        {
            log.Error("UE4SS is already installed here in its older layout (UE4SS.dll directly in Binaries\\Win64), and not by " +
                      "this manager. Installing another copy beside it would leave two, so it's left as it is. Remove it by " +
                      "hand first if you want this manager to install one.");
            return false;
        }

        Directory.CreateDirectory(game.Win64Path);

        if (incomingLegacy)
            return ApplyLegacy(game, extractDir, tag, assetName, channel, legacyManifest);

        // Moving from stable to experimental: carry the player's mods and settings into ue4ss\ first,
        // so the ordinary update below preserves and merges them exactly as it would its own.
        var migrated = false;
        if (legacyManifest != null)
        {
            if (!MigrateLegacyToModern(game, legacyManifest)) return false;
            migrated = true;
        }

        var incomingIni = TopLevelIniNames(modernSrc);

        // Settings files are not preserved wholesale - they are merged below. Read them first,
        // because the copy is about to overwrite them with the incoming defaults.
        var settingsBefore = ReadExistingSettings(game.UE4SSRootPath, incomingIni);

        // Name both ends of the change BEFORE making it. The build string does not identify a
        // UE4SS build - a stock release and an experimental nightly both report "v3.0.1 Beta" -
        // so the asset name is the only thing in the log that says what someone was actually
        // running, and it is the first thing wanted when a mod stops working after an update.
        var outgoing = migrated ? legacyManifest!.InstalledAssetName : ReadManifest(GetManifestPath(game))?.InstalledAssetName;
        log.Info(outgoing == null ? $"Installing {assetName}." : $"Replacing {outgoing} with {assetName}.");

        // The copy is taken BEFORE anything of the incoming build touches the game - the proxy DLL
        // included. Copying the new dwmapi.dll first meant the copy recorded the new proxy beside
        // the old ue4ss\ folder, so "Undo update" put back a pairing that had never existed.
        if (migrated)
        {
            // What sits in ue4ss\ now is only the player's mods and settings, not a build - setting
            // that aside as "the previous UE4SS" would make Undo restore half of one.
            DiscardPreviousBuild(game);
            log.Info("Moving from stable to experimental can't be undone with Undo update - Choose build installs any " +
                     "experimental build, and stable can be installed again after removing UE4SS.");
        }
        else
        {
            ArchivePreviousBuild(game, outgoing);
        }

        File.Copy(dwmapiSrc, Path.Combine(game.Win64Path, "dwmapi.dll"), true);
        CopyDirectoryPreserving(modernSrc, game.UE4SSRootPath, UserOwnedFiles);
        MergeSettingsFiles(game.UE4SSRootPath, settingsBefore, incomingIni);

        var manifest = new UE4SSManifest
        {
            InstalledTag = tag,
            InstalledAssetName = assetName,
            InstalledAt = DateTime.Now,
            Channel = channel
        };
        File.WriteAllText(GetManifestPath(game), JsonSerializer.Serialize(manifest));

        log.Success($"UE4SS ({assetName}) installed/updated successfully.");
        return true;
    }

    /// Installs the older layout (stable v3.0.1) loose into Binaries\Win64, recording every file.
    private static bool ApplyLegacy(GameInstallation game, string extractDir, string tag, string assetName,
        UE4SSChannel channel, UE4SSManifest? previous)
    {
        var log = LoggingService.Instance;

        // Experimental lives in ue4ss\, and the older layout only works while that folder doesn't
        // exist - its proxy would load one build while the manager managed the other.
        if (Directory.Exists(game.UE4SSRootPath))
        {
            if (File.Exists(Path.Combine(game.UE4SSRootPath, "UE4SS.dll")))
            {
                log.Error($"UE4SS experimental is installed here. Stable {LoaderCompatibility.StableVersion} uses the older " +
                          "layout, and going back to it isn't automatic - your mods live in ue4ss\\Mods and would have to " +
                          "move. Choose build installs any experimental build instead, including older ones.");
                return false;
            }

            // A ue4ss\ with no UE4SS.dll is one a lua mod created before UE4SS was installed. Its mods
            // move to where the older layout reads them; anything else in there means it isn't that.
            if (!AdoptModsFromEmptyModernFolder(game)) return false;
        }

        var incomingIni = TopLevelIniNames(extractDir);
        var settingsBefore = ReadExistingSettings(game.Win64Path, incomingIni);

        log.Info(previous == null
            ? $"Installing {assetName} (stable, older layout: its files go directly in Binaries\\Win64)."
            : $"Replacing {previous.InstalledAssetName} with {assetName}.");

        var written = new List<string>();
        foreach (var file in Directory.GetFiles(extractDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(extractDir, file);

            // An archive has no business putting an executable beside the game's own.
            if (relative.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;

            var target = Path.Combine(game.Win64Path, relative);
            var userOwned = UserOwnedFiles.Contains(relative, StringComparer.OrdinalIgnoreCase);
            if (userOwned && File.Exists(target)) continue;

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);

            // The player's files are never on record, even when this install created them, so a
            // removal can't take their mod list with it.
            if (!userOwned) written.Add(relative);
        }

        MergeSettingsFiles(game.Win64Path, settingsBefore, incomingIni);

        // The merge's baseline snapshots sit beside the settings in Win64 too, so they go on record
        // and leave with the rest.
        written.AddRange(incomingIni.Select(ini => ini + DefaultSnapshotSuffix)
            .Where(s => File.Exists(Path.Combine(game.Win64Path, s))));

        var manifest = new UE4SSManifest
        {
            InstalledTag = tag,
            InstalledAssetName = assetName,
            InstalledAt = DateTime.Now,
            Channel = channel,
            Files = written
        };
        File.WriteAllText(LegacyManifestPath(game), JsonSerializer.Serialize(manifest));

        log.Success($"UE4SS ({assetName}) installed. Stable keeps no undo copy - it hasn't changed since " +
                    $"{LoaderCompatibility.StableVersion}, and experimental is one Choose build away.");
        return true;
    }

    /// Stable -> experimental: Mods\ and the settings move into ue4ss\, then the rest of what stable
    /// put loose in Win64 is removed by its manifest. Moves rather than copies, on one volume, so a
    /// failure leaves every mod in exactly one place.
    private static bool MigrateLegacyToModern(GameInstallation game, UE4SSManifest legacy)
    {
        var log = LoggingService.Instance;
        var createdRoot = !Directory.Exists(game.UE4SSRootPath);
        var moved = new List<(string From, string To)>();

        try
        {
            Directory.CreateDirectory(game.UE4SSRootPath);

            var legacyMods = Path.Combine(game.Win64Path, "Mods");
            var modernMods = Path.Combine(game.UE4SSRootPath, "Mods");
            if (Directory.Exists(legacyMods)) MoveContents(legacyMods, modernMods, moved);

            // With its snapshot and backup, so the merge still knows which values were the player's.
            foreach (var file in Directory.GetFiles(game.Win64Path, "UE4SS-settings.ini*"))
            {
                var to = Path.Combine(game.UE4SSRootPath, Path.GetFileName(file));
                File.Move(file, to, true);
                moved.Add((file, to));
            }
        }
        catch (Exception ex)
        {
            // Stable is still the build that loads, and it reads Binaries\Win64\Mods - so everything
            // that moved goes back, and a ue4ss\ this created goes too, or every path would flip to a
            // folder nothing reads.
            var putBack = RollBack(moved);
            if (createdRoot) RemoveIfEmpty(game.UE4SSRootPath);

            log.Error($"Couldn't move your UE4SS mods into ue4ss\\: {ex.Message}. " + (putBack
                ? "Everything that had moved was put back, and stable UE4SS is as it was."
                : $"Not everything could be put back - check Binaries\\Win64\\Mods and {game.UE4SSRootPath}\\Mods."));
            return false;
        }

        // Every move succeeded, so only now does what stable put loose in Win64 go.
        var notRemoved = new List<string>();
        foreach (var path in LegacyFilesOnRecord(game, legacy))
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { notRemoved.Add(Path.GetRelativePath(game.Win64Path, path)); }
        }

        log.Info("Moved your UE4SS mods and settings from Binaries\\Win64 into ue4ss\\, where experimental reads them.");
        if (notRemoved.Count > 0)
            log.Warn($"Couldn't remove {string.Join(", ", notRemoved.Take(6))} left by stable UE4SS in Binaries\\Win64. " +
                     "Experimental doesn't use them - remove them by hand.");
        return true;
    }

    /// Reverses recorded moves, newest first. False when any of them couldn't be undone.
    private static bool RollBack(List<(string From, string To)> moved)
    {
        var ok = true;
        foreach (var (from, to) in Enumerable.Reverse(moved))
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(from)!);
                if (Directory.Exists(to)) Directory.Move(to, from);
                else if (File.Exists(to)) File.Move(to, from);
            }
            catch (Exception ex)
            {
                ok = false;
                LoggingService.Instance.Warn($"Couldn't put {to} back at {from}: {ex.Message}");
            }
        }
        return ok;
    }

    /// Deletes a folder tree that holds no files at all - never one with anything in it.
    private static void RemoveIfEmpty(string folder)
    {
        try
        {
            if (Directory.Exists(folder) && !Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Any())
                Directory.Delete(folder, recursive: true);
        }
        catch { /* best effort: an empty folder left behind is harmless */ }
    }

    /// For stable onto a game where a lua mod created ue4ss\Mods before any UE4SS existed.
    private static bool AdoptModsFromEmptyModernFolder(GameInstallation game)
    {
        var log = LoggingService.Instance;
        var root = game.UE4SSRootPath;

        var others = Directory.EnumerateFileSystemEntries(root)
            .Where(e => !string.Equals(Path.GetFileName(e), "Mods", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (others.Count > 0)
        {
            log.Error($"{root} holds files that aren't mods ({Path.GetFileName(others[0])}), so it isn't safe to treat as " +
                      "empty. Stable uses the older layout, which only works without that folder - remove it first.");
            return false;
        }

        try
        {
            var mods = Path.Combine(root, "Mods");
            if (Directory.Exists(mods)) MoveContents(mods, Path.Combine(game.Win64Path, "Mods"));
            if (!Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root);
            log.Info("Moved the mods waiting in ue4ss\\Mods to Binaries\\Win64\\Mods, where stable UE4SS reads them.");
            return !Directory.Exists(root);
        }
        catch (Exception ex)
        {
            log.Error($"Couldn't move the mods out of {root}: {ex.Message}");
            return false;
        }
    }

    /// Moves every entry of one folder into another, never overwriting: an entry already present at
    /// the destination stays put at the source and is named, so nothing is lost to a collision.
    private static void MoveContents(string source, string dest, List<(string From, string To)>? moved = null)
    {
        Directory.CreateDirectory(dest);

        foreach (var entry in Directory.EnumerateFileSystemEntries(source).ToList())
        {
            var target = Path.Combine(dest, Path.GetFileName(entry));
            if (File.Exists(target) || Directory.Exists(target))
            {
                LoggingService.Instance.Warn($"Left {entry} where it was - {target} already exists.");
                continue;
            }

            if (Directory.Exists(entry)) Directory.Move(entry, target);
            else File.Move(entry, target);
            moved?.Add((entry, target));
        }

        if (!Directory.EnumerateFileSystemEntries(source).Any()) Directory.Delete(source);
    }

    /// The .ini files a payload ships at its top level - the only ones a merge may touch. Under the
    /// older layout the folder being merged into is Binaries\Win64, which also holds other tools'
    /// configs (ReShade.ini, say), and those are nobody's business here.
    private static HashSet<string> TopLevelIniNames(string payloadRoot) =>
        new(Directory.GetFiles(payloadRoot, "*.ini", SearchOption.TopDirectoryOnly).Select(f => Path.GetFileName(f)),
            StringComparer.OrdinalIgnoreCase);

    /// Drops the copy kept by the last update, when it no longer describes anything restorable.
    private static void DiscardPreviousBuild(GameInstallation game)
    {
        try
        {
            var dest = AppPaths.PreviousUE4SSFor(game.RootPath);
            if (Directory.Exists(dest)) Directory.Delete(dest, true);
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Warn($"Couldn't clear the old undo copy: {ex.Message}");
        }
    }

    /// Sets the installed UE4SS aside before the incoming one lands on top of it.
    ///
    /// The reason this has to exist: the release tag is "experimental-latest", one rolling release
    /// whose assets are REPLACED IN PLACE. So the build a user was on cannot be re-downloaded once
    /// it is superseded - recovery is only ever possible from a copy taken before the overwrite, and
    /// until now the only copy was overwritten and the downloaded zip was deleted in a finally.
    /// A user broken by an update had no way back at all.
    ///
    /// Copied, never moved. A move that fails halfway leaves no working UE4SS at all, which is a
    /// worse position than the one this is insuring against. Costs about 40 MB, and only one is
    /// kept - this is an undo for the update that just happened, not a build library.
    ///
    /// Failure is logged and swallowed: not being able to take a safety copy is not a reason to
    /// refuse an update the user asked for.
    private static void ArchivePreviousBuild(GameInstallation game, string? outgoingAssetName)
    {
        var log = LoggingService.Instance;

        if (!Directory.Exists(game.UE4SSRootPath)) return;

        try
        {
            var dest = AppPaths.PreviousUE4SSFor(game.RootPath);
            if (Directory.Exists(dest)) Directory.Delete(dest, true);
            Directory.CreateDirectory(dest);

            CopyDirectoryPlain(game.UE4SSRootPath, Path.Combine(dest, "ue4ss"));

            // The proxy DLL is half the install and lives outside the ue4ss folder, so a restore
            // without it would put back the loader's files and none of what loads them.
            var proxy = Path.Combine(game.Win64Path, "dwmapi.dll");
            if (File.Exists(proxy)) File.Copy(proxy, Path.Combine(dest, "dwmapi.dll"), true);

            File.WriteAllText(Path.Combine(dest, "asset.txt"), outgoingAssetName ?? "unknown");

            log.Info($"Kept a copy of the UE4SS you had ({outgoingAssetName ?? "unknown build"}), so this update "
                     + "can be undone from Saves & Config if it breaks a mod.");
        }
        catch (Exception ex)
        {
            log.Warn($"Couldn't keep a copy of your current UE4SS first: {ex.Message}. The update will still "
                     + "proceed, but it won't be undoable.");
        }
    }

    /// The UE4SS build set aside by the last update, or null when there is none to go back to.
    public static PreviousUE4SSBuild? FindPreviousBuild(GameInstallation game)
    {
        var dest = AppPaths.PreviousUE4SSFor(game.RootPath);
        var payload = Path.Combine(dest, "ue4ss");
        if (!Directory.Exists(payload)) return null;

        var assetFile = Path.Combine(dest, "asset.txt");

        return new PreviousUE4SSBuild
        {
            RootPath = dest,
            AssetName = File.Exists(assetFile) ? File.ReadAllText(assetFile).Trim() : "unknown",
            TakenAt = Directory.GetCreationTime(dest)
        };
    }

    /// Puts the previous UE4SS back.
    ///
    /// Deliberately NOT a merge. This is "undo the update", so the whole payload goes back exactly
    /// as it was, settings included - the point is to return to a state the user knows worked, and
    /// carrying anything forward from the build being removed would make that state something they
    /// have never actually run.
    ///
    /// The copy is left in place afterwards rather than consumed, so a restore that turns out not to
    /// have been the problem can be repeated.
    public static bool RestorePreviousBuild(GameInstallation game)
    {
        // Restoring IS installing: it writes a UE4SS build and its proxy DLL into the game. The kept
        // copy is per install folder, so a folder once managed as DDS2 still has one after it is
        // recognised as some other game - which is exactly when this must refuse.
        if (RefuseIfNotPermitted(game, "restore") || RefuseIfRunning(game, "restore")) return false;

        var log = LoggingService.Instance;
        var previous = FindPreviousBuild(game);

        // The copy is always a ue4ss\ folder. Restoring it beside an older-layout UE4SS would leave
        // two builds installed, one loaded and one managed.
        if (File.Exists(Path.Combine(game.Win64Path, "UE4SS.dll")))
        {
            log.Error("UE4SS is installed in its older layout here, so the kept ue4ss\\ copy can't be put back beside it.");
            return false;
        }

        if (previous == null)
        {
            log.Error("There's no previous UE4SS to go back to - one is only kept from the next update onwards.");
            return false;
        }

        try
        {
            if (Directory.Exists(game.UE4SSRootPath)) Directory.Delete(game.UE4SSRootPath, true);
            CopyDirectoryPlain(Path.Combine(previous.RootPath, "ue4ss"), game.UE4SSRootPath);

            var proxy = Path.Combine(previous.RootPath, "dwmapi.dll");
            if (File.Exists(proxy)) File.Copy(proxy, Path.Combine(game.Win64Path, "dwmapi.dll"), true);

            log.Success($"Put {previous.AssetName} back. Your mods.txt and mod folders were not touched - "
                        + "only UE4SS itself.");
            return true;
        }
        catch (Exception ex)
        {
            log.Error($"Couldn't restore the previous UE4SS: {ex.Message}. The copy is still at {previous.RootPath}.");
            return false;
        }
    }

    /// A straight recursive copy. Separate from CopyDirectoryPreserving because that one exists to
    /// skip the user's files, and this one must not skip anything.
    private static void CopyDirectoryPlain(string source, string dest)
    {
        Directory.CreateDirectory(dest);

        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(dest, Path.GetRelativePath(source, dir)));

        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(dest, Path.GetRelativePath(source, file)), true);
    }

    /// The suffix on the snapshot of a settings file as UE4SS shipped it.
    ///
    /// This is the baseline the merge needs: without a record of the defaults the user started
    /// from, a value that differs from the new default could equally be their choice or a default
    /// UE4SS changed, and there is no way to tell which. Written on every install and update, so
    /// it always describes the version currently on disk.
    public const string DefaultSnapshotSuffix = ".dds2mm.default";

    /// The settings files as they are right now, before the incoming version overwrites them. Only
    /// the ones the incoming payload ships - see TopLevelIniNames.
    private static Dictionary<string, string> ReadExistingSettings(string root, IReadOnlySet<string> incoming)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in SafeEnumerateIni(root))
        {
            if (IsGeneratedState(path) || !incoming.Contains(Path.GetFileName(path))) continue;
            try { result[Path.GetFileName(path)] = File.ReadAllText(path); }
            catch (Exception ex)
            {
                LoggingService.Instance.Warn($"Couldn't read {Path.GetFileName(path)} before updating: {ex.Message}");
            }
        }

        return result;
    }

    /// Puts the user's own settings back on top of the version that just landed.
    ///
    /// Not a straight restore of their old file. Doing that would keep their values but also keep
    /// the whole old file, so options a newer UE4SS added would never appear and the comments
    /// documenting them would never arrive - the setting would exist with nothing on disk to say
    /// so. Instead the new file is kept as-is and only the values they changed are written back
    /// into it, which is why this needs a baseline to know which values those were.
    ///
    /// Failure here is not fatal: the mod loader is already installed and working with default
    /// settings at this point, so a merge that goes wrong is worth reporting, not rolling back.
    private static void MergeSettingsFiles(string root, Dictionary<string, string> before, IReadOnlySet<string> incoming)
    {
        var log = LoggingService.Instance;

        foreach (var path in SafeEnumerateIni(root))
        {
            var name = Path.GetFileName(path);
            if (IsGeneratedState(path) || !incoming.Contains(name)) continue;

            try
            {
                var newDefault = File.ReadAllText(path);
                var snapshotPath = path + DefaultSnapshotSuffix;

                // A first install has nothing to merge - record the baseline and stop.
                if (!before.TryGetValue(name, out var current))
                {
                    File.WriteAllText(snapshotPath, newDefault);
                    continue;
                }

                var baseline = File.Exists(snapshotPath) ? File.ReadAllText(snapshotPath) : null;
                var basis = "the snapshot taken when UE4SS was last installed";

                // Older builds of this manager kept no snapshot, but did back a file up the first
                // time it was edited here. That backup is the file as it was before their edits,
                // which is exactly the baseline wanted.
                if (baseline == null && File.Exists(path + GameConfigService.BackupSuffix))
                {
                    baseline = File.ReadAllText(path + GameConfigService.BackupSuffix);
                    basis = "the backup taken the first time you edited it here";
                }

                // No snapshot and no backup means this manager has no record of the file ever being
                // edited through it, so the most likely truth is that it is untouched. Treating it
                // as the baseline carries nothing and takes the new version wholesale.
                //
                // The alternative - assume every value differing from the new defaults was chosen
                // deliberately - is what used to happen here, and it is how a user's mods all broke:
                // UE4SS raised SecondsToScanBeforeGivingUp from 30 to 120 between two builds because
                // 30 was timing out, the old 30 was read as their preference and pinned, and UE4SS
                // then gave up scanning and loaded nothing. An old DEFAULT is not a choice.
                //
                // The two ways to be wrong are not equal. Assume-untouched can drop an edit made
                // outside this manager, which is visible, reported below, and one line to put back.
                // Assume-deliberate silently pins a value upstream changed on purpose, and presents
                // as every mod breaking at once with nothing on screen to connect it.
                var assumedUntouched = baseline == null;
                if (assumedUntouched)
                {
                    baseline = current;
                    basis = "the assumption that you had not edited it";
                }

                var merged = IniSettingsMerger.Merge(newDefault, current, baseline);

                // Nothing is dropped silently. When the file was assumed untouched but was not, this
                // is the only record of what was in it - so it names the lines rather than counting
                // them, and points at the copy that still has them.
                if (assumedUntouched)
                {
                    var changedByHand = IniSettingsMerger.DifferingLines(current, newDefault);
                    if (changedByHand.Count > 0)
                    {
                        log.Warn($"{name} differed from what UE4SS shipped in {changedByHand.Count} place(s), and this "
                                 + "manager has no record of you editing it - so the new version's values were used. "
                                 + "If any of these were yours, set them again:");
                        foreach (var line in changedByHand.Take(12)) log.Info($"    was: {line}");
                        if (changedByHand.Count > 12) log.Info($"    ...and {changedByHand.Count - 12} more");
                    }
                }

                // The snapshot always records what UE4SS shipped, never the merged result - it is
                // the reference for the NEXT update, so it has to stay free of the user's values.
                File.WriteAllText(snapshotPath, newDefault);

                if (!merged.ChangedAnything && merged.Dropped.Count == 0)
                {
                    log.Info($"{name} was left at its defaults, so the new version is used as-is.");
                    continue;
                }

                File.WriteAllText(path, merged.Text);

                if (merged.Carried.Count > 0)
                {
                    log.Success($"Kept your {merged.Carried.Count} change(s) to {name}, on top of the new version's "
                                + "defaults - so anything this UE4SS release added is present too.");
                    foreach (var line in merged.Carried.Take(12)) log.Info($"    {line}");
                    if (merged.Carried.Count > 12) log.Info($"    ...and {merged.Carried.Count - 12} more");
                }

                // Worth naming rather than dropping quietly: the user chose these, and the reason
                // they are gone is that the new UE4SS no longer has the setting at all.
                foreach (var line in merged.Dropped)
                    log.Warn($"{name}: '{line}' no longer exists in this version of UE4SS, so it wasn't carried over.");

                log.Info($"{name} was merged against {basis}.");
            }
            catch (Exception ex)
            {
                log.Error($"Couldn't merge your settings into the new {name}: {ex.Message}. "
                          + "The new version's file has been left in place.");
            }
        }
    }

    /// Regenerated state that happens to end in .ini. imgui.ini is the debug UI's remembered
    /// window positions, rewritten every run, so merging it would be busywork over noise.
    private static bool IsGeneratedState(string path) =>
        Path.GetFileName(path).Equals("imgui.ini", StringComparison.OrdinalIgnoreCase);

    /// Top-level .ini files in an existing UE4SS folder. Returns nothing rather than throwing when
    /// the folder is missing or unreadable - this only decides what to preserve, and a first-time
    /// install has nothing to preserve anyway.
    private static IEnumerable<string> SafeEnumerateIni(string ue4ssRoot)
    {
        try
        {
            return Directory.Exists(ue4ssRoot)
                ? Directory.GetFiles(ue4ssRoot, "*.ini", SearchOption.TopDirectoryOnly)
                : Enumerable.Empty<string>();
        }
        catch
        {
            return Enumerable.Empty<string>();
        }
    }

    private static void CopyDirectoryPreserving(string source, string dest, string[] preserveRelativePaths)
    {
        Directory.CreateDirectory(dest);
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(dest, Path.GetRelativePath(source, dir)));

        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(source, file);
            var target = Path.Combine(dest, rel);

            if (preserveRelativePaths.Contains(rel, StringComparer.OrdinalIgnoreCase) && File.Exists(target))
                continue; // keep the user's existing file

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }
    }

}
