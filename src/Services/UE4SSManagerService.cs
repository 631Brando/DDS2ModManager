namespace DDS2ModManager.Services;

/// Handles detecting, installing and updating UE4SS from the experimental-latest
/// release tag. We deliberately never touch the stable release channel - the user's
/// game needs the experimental build specifically.
public class UE4SSManagerService
{
    private const string Owner = "UE4SS-RE";
    private const string Repo = "RE-UE4SS";
    private const string Tag = "experimental-latest";

    private readonly GitHubReleaseService _github = new();

    public Task<GitHubReleaseInfo?> GetLatestExperimentalReleaseAsync() =>
        _github.GetReleaseByTagAsync(Owner, Repo, Tag);

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

        info.CanInstall = game.Profile.InstallableLoaders.HasFlag(ModLoaders.UE4SS);
        if (!info.CanInstall)
            info.InstallBlockedReason =
                $"{game.Profile.DisplayName} needs a UE4SS build made for its engine version, and that build " +
                "isn't published as a download - the standard ones crash this game on startup. Install it " +
                "yourself if you need it; this manager works with whatever is already there.";

        var manifestPath = GetManifestPath(game);
        if (File.Exists(manifestPath))
        {
            try
            {
                var manifest = JsonSerializer.Deserialize<UE4SSManifest>(File.ReadAllText(manifestPath));
                if (manifest != null)
                {
                    info.IsManagedByUs = true;
                    info.IsConfirmedExperimental = true;
                    info.InstalledVersionTag = manifest.InstalledTag;
                    info.InstalledAssetName = manifest.InstalledAssetName;
                    info.InstalledAt = manifest.InstalledAt;
                }
            }
            catch { /* corrupt manifest - treat as unmanaged */ }
        }

        return info;
    }

    private string GetManifestPath(GameInstallation game) =>
        Path.Combine(game.UE4SSRootPath, ".dds2modmanager_manifest.json");

    public async Task<bool> InstallOrUpdateAsync(GameInstallation game, GitHubReleaseInfo release, GitHubAsset asset,
        IProgress<double>? progress = null)
    {
        var log = LoggingService.Instance;
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

            var dwmapiSrc = Path.Combine(extractDir, "dwmapi.dll");
            var ue4ssSrc = Path.Combine(extractDir, "ue4ss");

            if (!File.Exists(dwmapiSrc) || !Directory.Exists(ue4ssSrc))
            {
                log.Error("Downloaded archive layout didn't match the expected dwmapi.dll + ue4ss\\ structure. " +
                           "UE4SS-RE may have changed the release layout - install manually and report this.");
                return false;
            }

            Directory.CreateDirectory(game.Win64Path);
            File.Copy(dwmapiSrc, Path.Combine(game.Win64Path, "dwmapi.dll"), true);

            // Files inside the UE4SS payload that belong to the USER, not to the release. Every
            // other file in the archive is overwritten, which is what updating UE4SS means.
            //
            // load_order.txt is here because only a person ever writes it - BPModLoaderMod's own Lua
            // just reads it - and losing it silently drops that loader back to loading Blueprint mods
            // in an arbitrary order, which presents as a mod that "sometimes" works.
            var preserve = new List<string>
            {
                Path.Combine("Mods", "mods.txt"),
                Path.Combine("Mods", "mods.json"),
                Path.Combine("Mods", "BPModLoaderMod", "load_order.txt")
            };

            // Settings files are not preserved wholesale - they are merged below. Read them first,
            // because the copy is about to overwrite them with the incoming defaults.
            var settingsBefore = ReadExistingSettings(game);

            // Name both ends of the change BEFORE making it. The build string does not identify a
            // UE4SS build - a stock release and an experimental nightly both report "v3.0.1 Beta" -
            // so the asset name is the only thing in the log that says what someone was actually
            // running, and it is the first thing wanted when a mod stops working after an update.
            var outgoing = GetCurrentStatus(game).InstalledAssetName;
            log.Info(outgoing == null
                ? $"Installing {asset.Name}."
                : $"Replacing {outgoing} with {asset.Name}.");

            ArchivePreviousBuild(game, outgoing);

            CopyDirectoryPreserving(ue4ssSrc, game.UE4SSRootPath, preserve.ToArray());

            MergeSettingsFiles(game, settingsBefore);

            var manifest = new UE4SSManifest
            {
                InstalledTag = release.TagName,
                InstalledAssetName = asset.Name,
                InstalledAt = DateTime.Now
            };
            File.WriteAllText(GetManifestPath(game), JsonSerializer.Serialize(manifest));

            log.Success($"UE4SS ({asset.Name}) installed/updated successfully.");
            return true;
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
        var log = LoggingService.Instance;
        var previous = FindPreviousBuild(game);

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

    /// The settings files as they are right now, before the incoming version overwrites them.
    private static Dictionary<string, string> ReadExistingSettings(GameInstallation game)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in SafeEnumerateIni(game.UE4SSRootPath))
        {
            if (IsGeneratedState(path)) continue;
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
    private static void MergeSettingsFiles(GameInstallation game, Dictionary<string, string> before)
    {
        var log = LoggingService.Instance;

        foreach (var path in SafeEnumerateIni(game.UE4SSRootPath))
        {
            var name = Path.GetFileName(path);
            if (IsGeneratedState(path)) continue;

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

                // Older builds of this manager kept no snapshot, but did back a file up the first
                // time it was edited here. That backup is the file as it was before their edits,
                // which is exactly the baseline wanted.
                if (baseline == null && File.Exists(path + GameConfigService.BackupSuffix))
                    baseline = File.ReadAllText(path + GameConfigService.BackupSuffix);

                var merged = IniSettingsMerger.Merge(newDefault, current, baseline);

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

                if (baseline == null)
                    log.Info($"There was no record of {name}'s original defaults, so anything differing from the new "
                             + "version's was treated as yours. Future updates will be exact.");
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

    private void CopyDirectoryPreserving(string source, string dest, string[] preserveRelativePaths)
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
