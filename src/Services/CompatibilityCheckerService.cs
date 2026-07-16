using CUE4Parse.FileProvider;
using CUE4Parse.MappingsProvider;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.Versions;

namespace DDS2ModManager.Services;

/// Finds mods that write to the same virtual asset path. When two paks both contain
/// e.g. Content/DataTables/Prices.uasset, only one copy actually mounts in-game and
/// silently overrides the other - that's the conflict we surface.
///
/// Two modes:
///   - CheckConflicts(mods): fast, uses each mod's ContainedAssetPaths captured at install
///     time. Runs automatically after every install/enable/disable for instant feedback.
///   - DeepScan(game, mods): authoritative. Re-reads the .pak/.ucas/.utoc files exactly as
///     they currently sit installed in Content\Paks and Content\Paks\LogicMods, mounting
///     each in place so the paths reflect what the game will actually load. This is what the
///     user should rely on before launching, since it accounts for any path differences
///     between an isolated pre-install read and the real installed layout.
///
/// NOTE on "who wins": UE4/UE5 pak mount priority depends on chunk priority and internal
/// mount rules, not strictly alphabetical order. The "likely winner" is a clearly-labeled
/// best-effort guess (last alphabetically), not a guarantee - if it matters, test in-game.
public class CompatibilityCheckerService
{
    public List<ModConflict> CheckConflicts(IEnumerable<ModInfo> mods)
    {
        var enabled = mods.Where(m => m.IsEnabled && m.IsInstalled &&
            m.Type is ModType.LogicMod or ModType.PatchMod).ToList();

        var pathToMods = BuildPathMap(enabled, m => m.ContainedAssetPaths);
        var conflicts = BuildConflicts(pathToMods);

        LogResult(conflicts.Count, "Compatibility check");
        return conflicts;
    }

    /// Re-reads installed paks in place. mappingsPath/egame come from Settings via the caller.
    public List<ModConflict> DeepScan(GameInstallation game, IEnumerable<ModInfo> mods, string mappingsPath, EGame egame, string? aesKeyHex)
    {
        var log = LoggingService.Instance;
        var enabled = mods.Where(m => m.IsEnabled && m.IsInstalled &&
            m.Type is ModType.LogicMod or ModType.PatchMod).ToList();

        log.Info("Deep scan: re-reading installed pak files in place...");

        var perMod = new Dictionary<ModInfo, List<string>>();
        foreach (var mod in enabled)
        {
            var paths = ReadInstalledPaths(game, mod, mappingsPath, egame, aesKeyHex);
            if (paths != null)
            {
                perMod[mod] = paths;
                // Refresh the stored list too, so the fast check and the Files tree agree with reality.
                mod.ContainedAssetPaths = paths;
            }
            else
            {
                // Fall back to whatever we captured at install time rather than dropping the mod.
                perMod[mod] = mod.ContainedAssetPaths;
                log.Warn($"Deep scan couldn't re-read '{mod.Name}' in place - using its stored file list instead.");
            }
        }

        var pathToMods = BuildPathMap(perMod.Keys, m => perMod[m]);
        var conflicts = BuildConflicts(pathToMods);

        LogResult(conflicts.Count, "Deep scan");
        return conflicts;
    }

    private List<string>? ReadInstalledPaths(GameInstallation game, ModInfo mod, string mappingsPath, EGame egame, string? aesKeyHex)
    {
        // Same IoStore rule as install-time analysis: a mod's .utoc references the game's
        // global.utoc, so it must be read in the context of the whole game, not in isolation.
        // The mod is already installed (its files sit in the game's Paks/LogicMods folder), so:
        //   1. mount the game as-is (mod present)              -> "withMod" set
        //   2. move the mod's files aside, re-mount            -> "withoutMod" set
        //   3. restore the mod's files
        //   4. the mod's paths = withMod - withoutMod
        var installedFiles = mod.InstallFiles
            .Where(f => File.Exists(f) &&
                (f.EndsWith(".pak", StringComparison.OrdinalIgnoreCase) ||
                 f.EndsWith(".ucas", StringComparison.OrdinalIgnoreCase) ||
                 f.EndsWith(".utoc", StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (installedFiles.Count == 0 || !Directory.Exists(game.PaksPath)) return null;

        var movedAside = new List<(string original, string temp)>();
        var asideDir = Path.Combine(Path.GetTempPath(), "DDS2MM_Aside_" + Guid.NewGuid().ToString("N"));

        try
        {
            var withMod = MountGameAndList(game, mappingsPath, egame, aesKeyHex);

            Directory.CreateDirectory(asideDir);
            foreach (var f in installedFiles)
            {
                var temp = Path.Combine(asideDir, Path.GetFileName(f));
                File.Move(f, temp, true);
                movedAside.Add((f, temp));
            }

            var withoutMod = MountGameAndList(game, mappingsPath, egame, aesKeyHex);

            return withMod.Except(withoutMod, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Warn($"Deep scan read failed for '{mod.Name}': {ex.Message}");
            return null;
        }
        finally
        {
            // Always restore the mod's files, even if anything above threw.
            foreach (var (original, temp) in movedAside)
            {
                try { if (File.Exists(temp)) File.Move(temp, original, true); }
                catch (Exception ex) { LoggingService.Instance.Error($"CRITICAL: couldn't restore '{original}' after deep scan ({ex.Message}). It's at '{temp}' - move it back manually."); }
            }
            try { if (Directory.Exists(asideDir)) Directory.Delete(asideDir, true); } catch { }
        }
    }

    private HashSet<string> MountGameAndList(GameInstallation game, string mappingsPath, EGame egame, string? aesKeyHex)
    {
        DefaultFileProvider? provider = null;
        try
        {
#pragma warning disable CS0618
            provider = new DefaultFileProvider(game.PaksPath, SearchOption.AllDirectories, true, new VersionContainer(egame));
#pragma warning restore CS0618
            try { provider.MappingsContainer = new FileUsmapTypeMappingsProvider(mappingsPath); } catch { /* best-effort */ }
            provider.Initialize();

            if (!string.IsNullOrWhiteSpace(aesKeyHex))
            {
                try { provider.SubmitKey(new CUE4Parse.UE4.Objects.Core.Misc.FGuid(), new CUE4Parse.Encryption.Aes.FAesKey(aesKeyHex)); }
                catch { /* logged elsewhere */ }
            }

            return provider.Files.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            (provider as IDisposable)?.Dispose();
        }
    }

    private Dictionary<string, List<ModInfo>> BuildPathMap(IEnumerable<ModInfo> mods, Func<ModInfo, List<string>> pathSelector)
    {
        var map = new Dictionary<string, List<ModInfo>>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in mods)
        {
            foreach (var path in pathSelector(mod))
            {
                if (!map.TryGetValue(path, out var list))
                    map[path] = list = new List<ModInfo>();
                if (!list.Contains(mod)) list.Add(mod);
            }
        }
        return map;
    }

    private List<ModConflict> BuildConflicts(Dictionary<string, List<ModInfo>> pathToMods)
    {
        var conflicts = new List<ModConflict>();
        foreach (var (path, involvedMods) in pathToMods.Where(kv => kv.Value.Count > 1))
        {
            var distinctMods = involvedMods.DistinctBy(m => m.Id)
                .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
            if (distinctMods.Count < 2) continue;

            conflicts.Add(new ModConflict
            {
                AssetPath = path,
                Kind = path.Contains("/MODS/", StringComparison.OrdinalIgnoreCase)
                    ? ConflictKind.ModFolderNameClash
                    : ConflictKind.BaseGameOverride,
                ConflictingModNames = distinctMods.Select(m => m.Name).ToList(),
                LikelyWinningModName = distinctMods.Last().Name
            });
        }
        return conflicts;
    }

    private void LogResult(int count, string label) =>
        LoggingService.Instance.Log(
            count == 0
                ? $"{label} complete - no conflicts found."
                : $"{label} complete - {count} conflicting file path(s) found.",
            count == 0 ? LogLevel.Success : LogLevel.Warning);
}
