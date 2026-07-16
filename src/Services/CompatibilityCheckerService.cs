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
    public List<ModConflictGroup> CheckConflicts(IEnumerable<ModInfo> mods)
    {
        var enabled = mods.Where(m => m.IsEnabled && m.IsInstalled &&
            m.Type is ModType.LogicMod or ModType.PatchMod).ToList();

        var pathToMods = BuildPathMap(enabled, m => m.ContainedAssetPaths);
        var conflicts = BuildConflicts(pathToMods);

        LogResult(conflicts, "Compatibility check");
        return conflicts;
    }

    /// Re-reads installed paks in place. mappingsPath/egame come from Settings via the caller.
    public List<ModConflictGroup> DeepScan(GameInstallation game, IEnumerable<ModInfo> mods, string mappingsPath, EGame egame, string? aesKeyHex)
    {
        var log = LoggingService.Instance;
        var enabled = mods.Where(m => m.IsEnabled && m.IsInstalled &&
            m.Type is ModType.LogicMod or ModType.PatchMod).ToList();

        log.Info("Deep scan: re-reading installed pak files in place...");

        var perMod = new Dictionary<ModInfo, List<string>>();
        DefaultFileProvider? provider = null;
        try
        {
            // Mount the game exactly as currently installed, once, for every mod - no moving
            // files aside. Each mod's own paths come straight from its own archive reader(s)
            // (see ReadModArchivePaths), so nothing needs to be temporarily removed to isolate
            // "before" vs "after": that approach also couldn't tell two mods conflicting on the
            // same path from one mod being absent, since both looked identical in the diff.
            provider = MountGame(game, mappingsPath, egame, aesKeyHex);

            foreach (var mod in enabled)
            {
                var paths = ReadModArchivePaths(provider, mod);
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
                    log.Warn($"Deep scan couldn't find '{mod.Name}''s own archive(s) in the mounted game - using its stored file list instead.");
                }
            }
        }
        finally
        {
            (provider as IDisposable)?.Dispose();
        }

        var pathToMods = BuildPathMap(perMod.Keys, m => perMod[m]);
        var conflicts = BuildConflicts(pathToMods);

        LogResult(conflicts, "Deep scan");
        return conflicts;
    }

    /// Reads the asset paths contributed by exactly this mod's own installed .pak/.utoc
    /// archive(s), straight from each reader's own Files dictionary - not a diff against some
    /// other mount of the game. Correct even when another mod (or a stale leftover) touches the
    /// same path, which is precisely the case a compatibility checker needs to catch rather than
    /// accidentally hide.
    private List<string>? ReadModArchivePaths(DefaultFileProvider provider, ModInfo mod)
    {
        var archiveNames = mod.InstallFiles
            .Where(f => File.Exists(f) &&
                (f.EndsWith(".pak", StringComparison.OrdinalIgnoreCase) ||
                 f.EndsWith(".utoc", StringComparison.OrdinalIgnoreCase)))
            .Select(Path.GetFileName)
            .ToList();

        if (archiveNames.Count == 0) return null;

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var foundAny = false;
        foreach (var name in archiveNames)
        {
            if (name != null && provider.TryGetArchive(name, out var archive))
            {
                foundAny = true;
                foreach (var p in archive.Files.Keys) paths.Add(p);
            }
        }

        return foundAny ? paths.ToList() : null;
    }

    private DefaultFileProvider MountGame(GameInstallation game, string mappingsPath, EGame egame, string? aesKeyHex)
    {
#pragma warning disable CS0618
        var provider = new DefaultFileProvider(game.PaksPath, SearchOption.AllDirectories, true, new VersionContainer(egame));
#pragma warning restore CS0618
        try { provider.MappingsContainer = new FileUsmapTypeMappingsProvider(mappingsPath); } catch { /* best-effort */ }
        provider.Initialize();

        if (!string.IsNullOrWhiteSpace(aesKeyHex))
        {
            try { provider.SubmitKey(new CUE4Parse.UE4.Objects.Core.Misc.FGuid(), new CUE4Parse.Encryption.Aes.FAesKey(aesKeyHex)); }
            catch { /* logged elsewhere */ }
        }

        // See ModAnalyzerService.MountAndReadArchives - Initialize() only registers archives,
        // Mount() is the call that actually mounts them.
        provider.Mount();
        return provider;
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

    /// Groups per-path collisions by the exact set of mods involved, so two mods sharing several
    /// files produce a single card naming both mods once (with every shared path listed under it)
    /// instead of one repeated, near-identical card per file - which was the real reason "which
    /// two mods conflict" wasn't clear: the mod names were the least prominent part of each card,
    /// and got buried under one entry per colliding path.
    private List<ModConflictGroup> BuildConflicts(Dictionary<string, List<ModInfo>> pathToMods)
    {
        var groups = new Dictionary<string, ModConflictGroup>();
        foreach (var (path, involvedMods) in pathToMods.Where(kv => kv.Value.Count > 1))
        {
            var distinctMods = involvedMods.DistinctBy(m => m.Id)
                .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
            if (distinctMods.Count < 2) continue;

            var key = string.Join("|", distinctMods.Select(m => m.Id));
            if (!groups.TryGetValue(key, out var group))
            {
                groups[key] = group = new ModConflictGroup
                {
                    ModNames = distinctMods.Select(m => m.Name).ToList(),
                    LikelyWinningModName = distinctMods.Last().Name,
                    Kind = path.Contains("/MODS/", StringComparison.OrdinalIgnoreCase)
                        ? ConflictKind.ModFolderNameClash
                        : ConflictKind.BaseGameOverride
                };
            }
            group.AssetPaths.Add(path);
        }

        return groups.Values
            .OrderByDescending(g => g.AssetPaths.Count)
            .ThenBy(g => g.ModNamesDisplay, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void LogResult(List<ModConflictGroup> groups, string label)
    {
        var fileCount = groups.Sum(g => g.AssetPaths.Count);
        LoggingService.Instance.Log(
            groups.Count == 0
                ? $"{label} complete - no conflicts found."
                : $"{label} complete - {groups.Count} mod conflict(s) found ({fileCount} file(s)): " +
                  string.Join("; ", groups.Select(g => g.ModNamesDisplay)),
            groups.Count == 0 ? LogLevel.Success : LogLevel.Warning);
    }
}
