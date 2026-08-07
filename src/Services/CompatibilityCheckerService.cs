using CUE4Parse.FileProvider;
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

        var conflicts = BuildAllConflicts(enabled, m => m.ContainedAssetPaths);

        LogResult(conflicts, "Compatibility check");

        return conflicts;
    }

    /// Mods added before row-level checking existed have no DataTable info stored, so the fast
    /// check silently can't compare their rows. The caller uses this to refresh them automatically
    /// rather than leaving the user to figure out that Deep Scan is what fixes it.
    public static bool NeedsDataTableRefresh(IEnumerable<ModInfo> mods) =>
        mods.Any(m => m.IsInstalled && m.Type == ModType.LogicMod && m.HasModActor && !m.DataTableScanCompleted);

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
            // LogicMods do their real work in Blueprint bytecode, which the provider skips by
            // default - without this every DataTable append is invisible and two LogicMods
            // rewriting the same balance values look completely unrelated.
            DataTableAppendScanner.EnableScriptReading(provider);
            var appendScanner = new DataTableAppendScanner();

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

                // Only LogicMods carry a ModActor; this is a no-op for patch mods.
                if (mod.Type == ModType.LogicMod)
                {
                    mod.DataTableAppends = appendScanner.Scan(provider, mod.Name, perMod[mod]);
                    mod.DataTableScanCompleted = true;
                    if (mod.DataTableAppends.Count > 0)
                    {
                        var overridden = mod.DataTableAppends.Sum(a => a.OverriddenBaseRows.Count);
                        log.Info($"'{mod.Name}' merges into {mod.DataTableAppends.Count} game table(s)" +
                                 (overridden > 0 ? $", replacing {overridden} existing row(s)." : ", adding new rows only."));
                    }
                }
            }
        }
        finally
        {
            (provider as IDisposable)?.Dispose();
        }

        var conflicts = BuildAllConflicts(perMod.Keys, m => perMod[m]);

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

    private DefaultFileProvider MountGame(GameInstallation game, string mappingsPath, EGame egame, string? aesKeyHex) =>
        GameMountService.Mount(game.PaksPath, mappingsPath, egame, aesKeyHex);

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

    private List<ModConflictGroup> BuildAllConflicts(IEnumerable<ModInfo> mods, Func<ModInfo, List<string>> pathSelector)
    {
        var modList = mods.ToList();

        var conflicts = BuildFileConflicts(modList, pathSelector);
        conflicts.AddRange(BuildDataTableConflicts(modList));
        conflicts.AddRange(BuildPatchReplacesTableConflicts(modList, pathSelector));

        // A pair could in principle turn up from both passes (e.g. shipping the same file *and*
        // merging into the same table). Fold those together so the panel never shows the same two
        // mod names twice.
        var merged = conflicts
            .GroupBy(c => string.Join("|", c.ModNames), StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var first = g.First();
                if (g.Count() > 1)
                {
                    first.AssetPaths = g.SelectMany(c => c.AssetPaths).Distinct().ToList();
                    first.TableInteractions = g.SelectMany(c => c.TableInteractions).ToList();
                    first.Severity = g.Max(c => c.Severity);
                    first.Kind = g.OrderByDescending(c => c.Severity).First().Kind;
                }
                return first;
            });

        return merged
            .OrderByDescending(c => c.Severity)
            .ThenByDescending(c => c.TotalSharedRows)
            .ThenBy(c => c.ModNamesDisplay, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// Two mods shipping the same asset path. Grouped by the exact set of mods involved, so two
    /// mods sharing several files produce a single card naming both mods once rather than dozens
    /// of near-identical cards.
    private List<ModConflictGroup> BuildFileConflicts(List<ModInfo> mods, Func<ModInfo, List<string>> pathSelector)
    {
        var pathToMods = BuildPathMap(mods, pathSelector);
        var groups = new Dictionary<string, ModConflictGroup>();

        foreach (var (path, involvedMods) in pathToMods.Where(kv => kv.Value.Count > 1))
        {
            var distinctMods = involvedMods.DistinctBy(m => m.Id)
                .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
            if (distinctMods.Count < 2) continue;

            var isFolderClash = path.Contains("/MODS/", StringComparison.OrdinalIgnoreCase);
            var key = string.Join("|", distinctMods.Select(m => m.Id)) + (isFolderClash ? ":folder" : ":file");

            if (!groups.TryGetValue(key, out var group))
            {
                groups[key] = group = new ModConflictGroup
                {
                    ModNames = distinctMods.Select(m => m.Name).ToList(),
                    LikelyWinningModName = distinctMods.Last().Name,
                    Kind = isFolderClash ? ConflictKind.ModFolderNameClash : ConflictKind.FullFileReplacement,
                    // Whole-file replacement always loses one mod's version outright - there's no
                    // partial merge the way there is for DataTable rows.
                    Severity = ConflictSeverity.Critical
                };
            }
            group.AssetPaths.Add(path);
        }

        return groups.Values.ToList();
    }

    /// Row-level analysis for LogicMods.
    ///
    /// LogicMods ship their own tables and merge them into base-game tables at runtime, so two of
    /// them never share an asset path and are invisible to BuildFileConflicts above. What actually
    /// matters is whether they contribute the same *row keys*: same table + different rows is
    /// perfectly fine, same table + same rows means one silently overwrites the other.
    ///
    /// Results are aggregated per pair of mods rather than per table - two mods that both extend
    /// seven of the same tables are one relationship the user needs to understand, not seven.
    private List<ModConflictGroup> BuildDataTableConflicts(List<ModInfo> mods)
    {
        // rows contributed by each mod, per table
        var byTable = mods
            .SelectMany(m => m.DataTableAppends.Select(a => (Mod: m, Append: a)))
            .GroupBy(x => x.Append.TargetName, StringComparer.OrdinalIgnoreCase);

        var pairs = new Dictionary<string, ModConflictGroup>();

        foreach (var table in byTable)
        {
            var contributors = table
                .GroupBy(x => x.Mod.Id)
                .Select(g => (Mod: g.First().Mod,
                              Rows: g.SelectMany(x => x.Append.SourceRows).ToHashSet(StringComparer.OrdinalIgnoreCase)))
                .OrderBy(x => x.Mod.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (contributors.Count < 2) continue;

            // Pairwise: a three-way overlap is clearer as the specific pairs that actually collide
            // than as one card implicating all three.
            for (var i = 0; i < contributors.Count; i++)
            for (var j = i + 1; j < contributors.Count; j++)
            {
                var a = contributors[i];
                var b = contributors[j];

                var key = $"{a.Mod.Name}|{b.Mod.Name}";
                if (!pairs.TryGetValue(key, out var group))
                {
                    pairs[key] = group = new ModConflictGroup
                    {
                        ModNames = new List<string> { a.Mod.Name, b.Mod.Name },
                        LikelyWinningModName = b.Mod.Name
                    };
                }

                group.TableInteractions.Add(new TableInteraction
                {
                    TableName = table.Key,
                    SharedRows = a.Rows.Intersect(b.Rows, StringComparer.OrdinalIgnoreCase)
                        .OrderBy(r => r, StringComparer.OrdinalIgnoreCase).ToList()
                });
            }
        }

        // Severity for the pair as a whole is driven by its worst table: one contested table makes
        // the relationship a conflict even if the other six are harmless.
        foreach (var group in pairs.Values)
        {
            group.TableInteractions = group.TableInteractions
                .OrderByDescending(t => t.SharedRows.Count)
                .ThenBy(t => t.TableName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var anyOverlap = group.TableInteractions.Any(t => t.HasOverlap);
            group.Kind = anyOverlap ? ConflictKind.DataTableRowOverlap : ConflictKind.DataTableSharedNoOverlap;
            group.Severity = anyOverlap ? ConflictSeverity.Critical : ConflictSeverity.Info;
        }

        return pairs.Values.ToList();
    }

    /// A patch mod that ships a whole replacement for a table some LogicMod also merges into.
    /// Both still "work", but the appended rows land on top of the patch's version rather than the
    /// original, so the result depends on ordering - worth a heads-up rather than silence.
    ///
    /// Matched on table name because the two sides express paths differently: mounted archives give
    /// "DrugDealerSimulator2/Content/DataTables/X.uasset", while Blueprint bytecode gives
    /// "/Game/DataTables/X.X". Reconciling mount-point conventions would be more fragile than
    /// comparing the table's own (already unique) name.
    private List<ModConflictGroup> BuildPatchReplacesTableConflicts(
        List<ModInfo> mods, Func<ModInfo, List<string>> pathSelector)
    {
        var appenders = mods
            .SelectMany(m => m.DataTableAppends.Select(a => (Mod: m, a.TargetName)))
            .ToList();
        if (appenders.Count == 0) return new List<ModConflictGroup>();

        var pairs = new Dictionary<string, ModConflictGroup>();

        foreach (var patch in mods.Where(m => m.Type == ModType.PatchMod))
        {
            var replacedTables = pathSelector(patch)
                .Select(Path.GetFileNameWithoutExtension)
                .Where(n => !string.IsNullOrEmpty(n))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var (mod, tableName) in appenders)
            {
                if (!replacedTables.Contains(tableName)) continue;

                var key = $"{patch.Name}|{mod.Name}";
                if (!pairs.TryGetValue(key, out var group))
                {
                    pairs[key] = group = new ModConflictGroup
                    {
                        ModNames = new List<string> { patch.Name, mod.Name },
                        Kind = ConflictKind.PatchReplacesAppendedTable,
                        Severity = ConflictSeverity.Warning,
                        LikelyWinningModName = patch.Name
                    };
                }

                if (group.TableInteractions.All(t => !t.TableName.Equals(tableName, StringComparison.OrdinalIgnoreCase)))
                    group.TableInteractions.Add(new TableInteraction { TableName = tableName });
            }
        }

        return pairs.Values.ToList();
    }

    private void LogResult(List<ModConflictGroup> groups, string label)
    {
        var real = groups.Where(g => g.Severity != ConflictSeverity.Info).ToList();

        if (real.Count == 0)
        {
            var suffix = groups.Count > 0 ? $" ({groups.Count} compatible overlap(s) noted)" : "";
            LoggingService.Instance.Success($"{label} complete - no conflicts found.{suffix}");
            return;
        }

        LoggingService.Instance.Warn(
            $"{label} complete - {real.Count} conflict(s) found: " +
            string.Join("; ", real.Select(g => $"{g.ModNamesDisplay} ({g.Summary})")));
    }
}
