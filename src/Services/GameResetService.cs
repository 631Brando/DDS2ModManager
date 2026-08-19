namespace DDS2ModManager.Services;

/// What a "reset to vanilla" run should remove. Every part is opt-in because "vanilla" means
/// different things to different people - some want the mods gone but UE4SS kept for next time,
/// others want the game exactly as Steam installed it.
public class VanillaResetOptions
{
    /// Mods this manager installed and is tracking. Uses the normal uninstall path, so lua mods
    /// also get their mods.txt entries cleaned up.
    public bool RemoveTrackedMods { get; set; } = true;

    /// Mod paks sitting in Content\Paks / LogicMods that the manager never installed (hand-placed
    /// files, or leftovers from a previous tool). Base-game paks are never touched.
    public bool RemoveUntrackedMods { get; set; } = true;

    /// dwmapi.dll + the whole ue4ss folder. Without this the game still loads UE4SS, it just has
    /// no mods to run.
    public bool RemoveUE4SS { get; set; }

    /// Deletes the per-user .ini files so the game regenerates them at defaults on next launch.
    /// Wipes graphics/keybind settings too, which is why it's off by default.
    public bool ResetConfigs { get; set; }
}

public class VanillaResetResult
{
    public int TrackedModsRemoved { get; set; }
    public int UntrackedFilesRemoved { get; set; }
    public bool UE4SSRemoved { get; set; }
    public bool ConfigsReset { get; set; }
    public List<string> Failures { get; set; } = new();
}

/// Returns a modded game to an unmodded state.
///
/// Distinct from AppSettingsService.ResetAllAppData, which only clears the *manager's* own state
/// (settings, mod tracking, cached mappings) and deliberately leaves every mod file installed.
/// This one is the opposite: it touches the game, not the manager.
///
/// Never touches saves. Wiping a playthrough is not something anyone means by "remove my mods",
/// and the save manager already exists for when they do.
public class GameResetService
{
    private readonly GameInstallation _game;
    private readonly ModInstallerService _installer;
    private readonly ModRegistryService _registry;
    private readonly UnmanagedModScannerService _scanner = new();

    public GameResetService(GameInstallation game, ModInstallerService installer, ModRegistryService registry)
    {
        _game = game;
        _installer = installer;
        _registry = registry;
    }

    public VanillaResetResult Reset(VanillaResetOptions options)
    {
        var log = LoggingService.Instance;
        var result = new VanillaResetResult();

        if (options.RemoveTrackedMods)
        {
            // ToList() first: Uninstall mutates the registry collection we'd otherwise be iterating.
            foreach (var mod in _registry.Mods.ToList())
            {
                var before = _registry.Mods.Count;
                _installer.Uninstall(mod);
                if (_registry.Mods.Count < before) result.TrackedModsRemoved++;
                else result.Failures.Add($"Couldn't fully remove '{mod.Name}'.");
            }
        }

        if (options.RemoveUntrackedMods)
            result.UntrackedFilesRemoved = RemoveUntrackedModFiles(result);

        if (options.RemoveUE4SS)
            result.UE4SSRemoved = RemoveUE4SS(result);

        if (options.ResetConfigs)
            result.ConfigsReset = ResetConfigs(result);

        log.Success(
            $"Reset complete - {result.TrackedModsRemoved} tracked mod(s) removed, " +
            $"{result.UntrackedFilesRemoved} untracked mod file(s) removed" +
            (result.UE4SSRemoved ? ", UE4SS removed" : "") +
            (result.ConfigsReset ? ", configs reset" : "") + ".");

        if (result.Failures.Count > 0)
            log.Warn($"{result.Failures.Count} item(s) couldn't be removed - see the errors above.");

        return result;
    }

    /// Deletes mod pak files the manager doesn't track. Reuses the unmanaged-mod scanner so the
    /// definition of "a mod file rather than a base-game file" is identical to the one the import
    /// feature uses - this must never delete pakchunk*/global*.
    private int RemoveUntrackedModFiles(VanillaResetResult result)
    {
        var log = LoggingService.Instance;
        var removed = 0;

        // Mappings/EGame only matter for identifying mod *types*, which a delete doesn't need, so
        // a failed CUE4Parse read here is harmless: the scanner still reports the files.
        var opts = GameMountService.OptionsFor(_game);

        List<UnmanagedMod> untracked;
        try
        {
            untracked = _scanner.Scan(_game, _registry.Mods, opts.MappingsPath, opts.EGame, opts.AesKeyHex);
        }
        catch (Exception ex)
        {
            result.Failures.Add($"Couldn't scan for untracked mods: {ex.Message}");
            return 0;
        }

        foreach (var mod in untracked)
        {
            // Lua mods live in ue4ss\Mods; those come out with UE4SS (or via the mod list), not here.
            if (mod.DetectedType == ModType.LuaMod) continue;

            // A loose-asset FOLDER is not something this can safely empty. Ownership of an
            // individual .uasset cannot be recovered from disk, so "delete every mod file here"
            // cannot be distinguished from "delete some of the game". Reported, never deleted.
            if (mod.IsLooseAssetGroup)
            {
                var why = $"Left '{mod.Name}' alone - it's a folder of loose assets whose ownership can't be "
                          + "determined, so removing it automatically could delete more than mod files. "
                          + "Remove it by hand if you mean to.";
                log.Warn(why);
                result.Failures.Add(why);
                continue;
            }

            foreach (var f in mod.Files.Where(File.Exists))
            {
                // Second, independent check before an irreversible delete.
                //
                // The scanner is already supposed to have excluded the base game, but that filter and
                // this File.Delete were a single point of failure for losing an 11.3 GB install that
                // no undo can bring back. One regression in one predicate is not an acceptable
                // distance from "reset my mods" to "re-download the entire game", so the deleter
                // refuses on its own account rather than trusting what it was handed.
                if (IsProtectedGameFile(f))
                {
                    var why = $"Refused to delete '{Path.GetFileName(f)}' - it looks like part of the base game, "
                              + "not a mod. Nothing was removed for this entry.";
                    log.Warn(why);
                    result.Failures.Add(why);
                    continue;
                }

                try
                {
                    File.Delete(f);
                    removed++;
                }
                catch (Exception ex)
                {
                    result.Failures.Add($"Couldn't delete {Path.GetFileName(f)}: {ex.Message}");
                }
            }
            log.Info($"Removed untracked mod '{mod.Name}'.");
        }

        return removed;
    }

    /// Files this service will never delete, however it was asked.
    ///
    /// Two rules, either of which is enough to refuse:
    ///  - it sits DIRECTLY in Content\Paks and the scanner's own base-game test claims it. Mods live
    ///    in Mods\ or LogicMods\ subfolders; a loose archive at the top of Paks is the shipped game.
    ///  - it is enormous. No mod is gigabytes; the base paks are. A size rule catches a base pak
    ///    whose name nobody anticipated, which is exactly the case a name-based rule cannot.
    private bool IsProtectedGameFile(string path)
    {
        try
        {
            var directlyInPaks = string.Equals(
                Path.GetDirectoryName(path)?.TrimEnd(Path.DirectorySeparatorChar),
                _game.PaksPath.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);

            if (directlyInPaks &&
                UnmanagedModScannerService.IsBaseGameArchive(Path.GetFileNameWithoutExtension(path), _game))
                return true;

            return new FileInfo(path).Length >= BasePakSizeFloorBytes;
        }
        catch
        {
            // Can't tell what it is, so don't delete it.
            return true;
        }
    }

    /// A mod this big does not exist; a cooked base pak does. Deliberately far above any plausible
    /// content pack so it never blocks a legitimate removal.
    private const long BasePakSizeFloorBytes = 2L * 1024 * 1024 * 1024;

    /// Removes UE4SS, but only when there is something that can be removed without collateral.
    ///
    /// Driven by what detection found rather than by fixed paths. Under UE4SS's older layout its
    /// files sit directly in Binaries\Win64 - the folder holding the game's executable, with the
    /// user's own mods in its Mods\ subfolder - so there is no directory that can be deleted
    /// wholesale. That case is reported and skipped, never approximated.
    private bool RemoveUE4SS(VanillaResetResult result)
    {
        var log = LoggingService.Instance;
        var loader = new ModLoaderService().Detect(_game, ModLoaders.UE4SS);

        if (loader is not { IsInstalled: true })
        {
            log.Info("UE4SS isn't installed - nothing to remove.");
            return true;
        }

        if (!loader.CanRemoveAutomatically)
        {
            var why = loader.RemovalBlockedReason ?? "UE4SS can't be removed automatically here.";
            log.Warn(why);
            result.Failures.Add(why);
            return false;
        }

        var ok = true;

        foreach (var file in loader.RemovableFiles)
        {
            try { if (File.Exists(file)) File.Delete(file); }
            catch (Exception ex)
            {
                result.Failures.Add($"Couldn't delete {Path.GetFileName(file)}: {ex.Message}");
                ok = false;
            }
        }

        var root = loader.RemovableRoot;
        if (root != null)
        {
            // Belt and braces on an irreversible recursive delete: refuse anything that is not a
            // strict subfolder of Binaries\Win64. If RemovableRoot were ever set to Win64 itself,
            // "remove UE4SS" would delete the game.
            var win64 = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_game.Win64Path));
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

            if (!full.StartsWith(win64 + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                var why = $"Refused to delete '{root}' - it isn't inside the game's Binaries\\Win64 folder.";
                log.Error(why);
                result.Failures.Add(why);
                return false;
            }

            try { if (Directory.Exists(full)) Directory.Delete(full, true); }
            catch (Exception ex)
            {
                result.Failures.Add($"Couldn't delete the ue4ss folder: {ex.Message}");
                ok = false;
            }
        }

        if (ok) log.Info("Removed UE4SS.");
        return ok;
    }

    private bool ResetConfigs(VanillaResetResult result)
    {
        try
        {
            if (Directory.Exists(_game.ConfigPath))
                Directory.Delete(_game.ConfigPath, true);

            LoggingService.Instance.Info("Deleted the game's config files - it will regenerate them at defaults on next launch.");
            return true;
        }
        catch (Exception ex)
        {
            result.Failures.Add($"Couldn't reset configs: {ex.Message}");
            return false;
        }
    }
}
