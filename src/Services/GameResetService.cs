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
        var settings = AppSettingsService.Instance.Current;
        var mappingsPath = !string.IsNullOrWhiteSpace(settings.MappingsOverridePath) && File.Exists(settings.MappingsOverridePath)
            ? settings.MappingsOverridePath!
            : MappingsProviderService.EnsureExtracted();
        var egame = Enum.TryParse<CUE4Parse.UE4.Versions.EGame>(settings.EGameVersion, out var parsed)
            ? parsed
            : CUE4Parse.UE4.Versions.EGame.GAME_UE5_3;

        List<UnmanagedMod> untracked;
        try
        {
            untracked = _scanner.Scan(_game, _registry.Mods, mappingsPath, egame, settings.AesKeyHex);
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

            foreach (var f in mod.Files.Where(File.Exists))
            {
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

    private bool RemoveUE4SS(VanillaResetResult result)
    {
        var log = LoggingService.Instance;
        var ok = true;

        try
        {
            var dwmapi = Path.Combine(_game.Win64Path, "dwmapi.dll");
            if (File.Exists(dwmapi)) File.Delete(dwmapi);
        }
        catch (Exception ex)
        {
            result.Failures.Add($"Couldn't delete dwmapi.dll: {ex.Message}");
            ok = false;
        }

        try
        {
            if (Directory.Exists(_game.UE4SSRootPath)) Directory.Delete(_game.UE4SSRootPath, true);
        }
        catch (Exception ex)
        {
            result.Failures.Add($"Couldn't delete the ue4ss folder: {ex.Message}");
            ok = false;
        }

        if (ok) log.Info("Removed UE4SS (dwmapi.dll and the ue4ss folder).");
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
