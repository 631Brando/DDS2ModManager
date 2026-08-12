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
///
/// Lua mods are checked too, but on completely different evidence - see BuildLuaConflicts.
public class CompatibilityCheckerService
{
    public List<ModConflictGroup> CheckConflicts(IEnumerable<ModInfo> mods)
    {
        var enabled = mods.Where(m => m.IsEnabled && m.IsInstalled &&
            m.Type is ModType.LogicMod or ModType.PatchMod or ModType.LuaMod).ToList();

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

        // Lua mods are deliberately kept out of the mount below: they ship no container, so
        // ReadModArchivePaths would find nothing for every one of them and warn about it. Their
        // check reads the .lua text straight off disk at InstallPath, which is the same thing the
        // fast check does - a deep scan genuinely buys a lua mod nothing extra.
        var luaMods = mods.Where(m => m.IsEnabled && m.IsInstalled && m.Type == ModType.LuaMod).ToList();

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

        var conflicts = BuildAllConflicts(perMod.Keys.Concat(luaMods),
            m => perMod.TryGetValue(m, out var paths) ? paths : m.ContainedAssetPaths);

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

        // Pak mods and lua mods must never be compared against each other by path, and lua mods
        // must never go through BuildFileConflicts at all.
        //
        // A pak mod's ContainedAssetPaths are virtual mount paths inside its container; a lua
        // mod's are the real relative files in its own private folder - and EVERY lua mod on earth
        // contains "Scripts/main.lua". Feeding them to the same path map turns every pair of lua
        // mods into a Critical "both replace the same file" card, which is exactly backwards:
        // those two files live in two different folders and never see each other.
        var pakMods = modList.Where(m => m.Type is ModType.LogicMod or ModType.PatchMod).ToList();
        var luaMods = modList.Where(m => m.Type == ModType.LuaMod).ToList();

        var conflicts = BuildFileConflicts(pakMods, pathSelector);
        conflicts.AddRange(BuildDataTableConflicts(pakMods));
        conflicts.AddRange(BuildPatchReplacesTableConflicts(pakMods, pathSelector));
        conflicts.AddRange(BuildLuaConflicts(luaMods));

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

    // =============================================================================================
    // Lua mods
    // =============================================================================================
    //
    // Lua mods share no files and no DataTables, so everything above is blind to them. What they do
    // share is UE4SS's process-wide registries, and only two of those can be read out of the script
    // text with enough confidence to put a card on screen:
    //
    //   * console command names  (RegisterConsoleCommandHandler)
    //   * key bindings           (RegisterKeyBind / RegisterKeyBindAsync)
    //
    // Plus the one filesystem case: two lua mods installed into the same ue4ss\Mods\<X> folder.
    //
    // DELIBERATELY NOT CHECKED: two mods hooking the same UFunction via RegisterHook. It reads like
    // the obvious signal and it is the worst one. Measured against the 17 lua mods installed in the
    // game's ue4ss\Mods folder it produces nine pairs of pure noise and not one real conflict: four
    // separate mods hook /Script/Engine.PlayerController:ServerAcknowledgePossession purely to notice
    // that the player spawned, two hook PlayerLocalPopupsWidget:DisplayShortNotify to read banners,
    // the two built-in UE4SS mods both hook PlayerController:ClientRestart by design, and
    // BotanistExpansion + EthanolExtraction both PRE-hook BlueprintHelpersLib:GetCraftingStationTypeMeta
    // to append their own recipe rows to a widget - which their authors documented as safe precisely
    // because neither touches the return value. UE4SS runs every registered callback, so a shared hook
    // target is normal, not contested. The refinement that would make it a real signal - "and both
    // mods write to the return value or a parameter" - has exactly two `:set(` calls across all 17
    // mods, and neither is on a function another mod hooks, so there is nothing to build on.
    //
    // ALSO NOT CHECKED: hook targets and command names assembled at runtime. Most RegisterHook calls
    // here build their path from a local constant plus a loop variable, and the same is true of some
    // keybinds (see the blind spots listed above ExtractConsoleCommands). Resolving those means
    // interpreting lua, and a half-interpreted string is exactly how a checker starts inventing
    // conflicts - so an unresolvable form is dropped rather than approximated.

    /// What one lua mod claims in the registries above, as parsed out of its own scripts.
    private sealed class LuaRegistrations
    {
        public HashSet<string> ConsoleCommands { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Keybinds { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private List<ModConflictGroup> BuildLuaConflicts(List<ModInfo> luaMods)
    {
        var groups = new List<ModConflictGroup>();
        if (luaMods.Count < 2) return groups;

        var folderClashes = BuildLuaFolderConflicts(luaMods);
        groups.AddRange(folderClashes);

        // Two rows pointing at one folder read the same scripts, so they "clash" on every command
        // and every key in them - the same single finding counted a hundred times, and it would then
        // merge into the folder card and relabel its list as files. The folder card says it once.
        var sharedFolderPairs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var clash in folderClashes)
            for (var i = 0; i < clash.ModNames.Count; i++)
            for (var j = i + 1; j < clash.ModNames.Count; j++)
                sharedFolderPairs.Add($"{clash.ModNames[i]}|{clash.ModNames[j]}");

        var perMod = new Dictionary<ModInfo, LuaRegistrations>();
        var unreadable = new List<string>();
        foreach (var mod in luaMods)
        {
            var regs = ReadLuaRegistrations(mod);
            if (regs != null) perMod[mod] = regs;
            else unreadable.Add(mod.Name);
        }

        // Same argument as NeedsDataTableRefresh: a mod we could not read looks identical to a mod
        // that conflicts with nothing, so say which ones we were blind to rather than let the panel
        // imply it checked them.
        if (unreadable.Count > 0)
            LoggingService.Instance.Warn(
                "Couldn't read the scripts of " + string.Join(", ", unreadable) +
                " - their console commands and keybinds were not compared. Re-install them if the folder has moved.");

        groups.AddRange(BuildLuaRegistrationConflicts(perMod, sharedFolderPairs, r => r.ConsoleCommands,
            ConflictKind.LuaConsoleCommandClash, ModConflictGroup.LuaCommandLabel));
        groups.AddRange(BuildLuaRegistrationConflicts(perMod, sharedFolderPairs, r => r.Keybinds,
            ConflictKind.LuaKeybindClash, ModConflictGroup.LuaKeybindLabel));

        return groups;
    }

    /// Two lua mods installed into the same ue4ss\Mods\&lt;X&gt; folder.
    ///
    /// InstallLuaMod names the folder after the archive's own root folder, never after mod.Name, so
    /// two unrelated downloads that both ship a folder called "Mods" (or two copies of one mod) land
    /// on top of each other: the second copy overwrites the first file by file, and mods.txt keys on
    /// the folder so both rows share a single enable/disable switch. Compared on the real folder on
    /// disk rather than on file lists - see the "Scripts/main.lua" trap in BuildAllConflicts.
    private List<ModConflictGroup> BuildLuaFolderConflicts(List<ModInfo> luaMods)
    {
        return luaMods
            .Where(m => !string.IsNullOrWhiteSpace(m.InstallPath))
            .GroupBy(m => Path.GetFileName(m.InstallPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                     StringComparer.OrdinalIgnoreCase)
            .Where(g => g.DistinctBy(m => m.Id).Count() > 1)
            .Select(g =>
            {
                var involved = g.DistinctBy(m => m.Id).ToList();
                return new ModConflictGroup
                {
                    ModNames = involved.Select(m => m.Name)
                        .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(),
                    Kind = ConflictKind.ModFolderNameClash,
                    Severity = ConflictSeverity.Critical,
                    // The one place the winner is a fact rather than a guess: nothing is being
                    // mounted or load-ordered here, the second install simply copied its files over
                    // the first one's, so whoever was installed last is what is on disk now.
                    LikelyWinningModName = involved.OrderBy(m => m.InstalledAt).Last().Name
                };
            })
            .ToList();
    }

    /// Pairs up the mods that claim the same registration name. Aggregated per pair, like the
    /// DataTable check: two mods that collide on six commands are one relationship, not six cards.
    private List<ModConflictGroup> BuildLuaRegistrationConflicts(
        Dictionary<ModInfo, LuaRegistrations> perMod,
        HashSet<string> sharedFolderPairs,
        Func<LuaRegistrations, HashSet<string>> selector,
        ConflictKind kind,
        string entryLabel)
    {
        var byName = new Dictionary<string, List<ModInfo>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (mod, regs) in perMod)
        foreach (var name in selector(regs))
        {
            if (!byName.TryGetValue(name, out var list))
                byName[name] = list = new List<ModInfo>();
            if (!list.Contains(mod)) list.Add(mod);
        }

        var pairs = new Dictionary<string, ModConflictGroup>();

        foreach (var (name, involved) in byName.Where(kv => kv.Value.Count > 1))
        {
            var contenders = involved.DistinctBy(m => m.Id)
                .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
            if (contenders.Count < 2) continue;

            // Pairwise for the same reason the DataTable check is: three mods on one command name
            // is clearer as the pairs that actually collide.
            for (var i = 0; i < contenders.Count; i++)
            for (var j = i + 1; j < contenders.Count; j++)
            {
                var key = $"{contenders[i].Name}|{contenders[j].Name}";
                if (sharedFolderPairs.Contains(key)) continue;

                if (!pairs.TryGetValue(key, out var group))
                {
                    pairs[key] = group = new ModConflictGroup
                    {
                        ModNames = new List<string> { contenders[i].Name, contenders[j].Name },
                        Kind = kind,
                        // Not Critical: neither mod is broken and no content is lost - one name is
                        // contested and everything else about both mods keeps working. The panel is
                        // only useful while red still means "you will lose something".
                        Severity = ConflictSeverity.Warning
                    };
                }

                var entry = entryLabel + name;
                if (!group.AssetPaths.Contains(entry)) group.AssetPaths.Add(entry);
            }
        }

        foreach (var group in pairs.Values)
            group.AssetPaths.Sort(StringComparer.OrdinalIgnoreCase);

        return pairs.Values.ToList();
    }

    /// Reads a lua mod's scripts off disk. Null when the folder isn't there to read.
    ///
    /// Disabling a lua mod only flips its mods.txt entry to 0 and leaves the files alone, so unlike
    /// a disabled pak mod the scripts are always where InstallPath says they are.
    private LuaRegistrations? ReadLuaRegistrations(ModInfo mod)
    {
        var root = mod.InstallPath;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return null;

        var regs = new LuaRegistrations();

        foreach (var file in EnumerateLuaFiles(mod, root))
        {
            string source;
            try
            {
                source = File.ReadAllText(file);
            }
            catch (Exception ex)
            {
                LoggingService.Instance.Warn($"Couldn't read '{file}' while checking '{mod.Name}': {ex.Message}");
                continue;
            }

            var code = StripLuaComments(source);
            foreach (var name in ExtractConsoleCommands(code)) regs.ConsoleCommands.Add(name);
            foreach (var bind in ExtractKeybinds(code)) regs.Keybinds.Add(bind);
        }

        return regs;
    }

    /// Every script the mod actually ships, and ONLY files whose extension is exactly .lua.
    ///
    /// A lua mod folder that has been worked on fills up with main.lua.bak, main.lua.prewidget and
    /// mif_cloth_probe.lua.disabled next to the live script. UE4SS loads none of those, and counting
    /// them would credit a mod with commands it removed three revisions ago - a conflict the user
    /// cannot find or fix because it does not exist. Windows wildcard matching is the reason for the
    /// explicit extension check on the fallback: "*.lua" also matches "main.luac".
    private static IEnumerable<string> EnumerateLuaFiles(ModInfo mod, string root)
    {
        var rootFull = Path.GetFullPath(root);
        // Compared with the separator attached, so "...\Foo" does not accept "...\FooBar".
        var rootPrefix = rootFull.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        var fromRegistry = mod.ContainedAssetPaths
            .Where(p => p.EndsWith(".lua", StringComparison.OrdinalIgnoreCase))
            // Screened BEFORE GetFullPath, not after it.
            //
            // The containment check below treats a stored path as external input, which is
            // right - but GetFullPath is reached first, and it THROWS on a NUL or other
            // invalid character rather than returning something a later Where could reject.
            // RunCompatibilityCheck is synchronous on the UI thread, so that throw leaves as
            // an unhandled-exception dialog with the panel never updating. The registry is
            // JSON on disk and JSON can encode  , which is the same reason the check
            // below exists: distrust a stored path for its whole journey, not just the last
            // step of it.
            .Where(p => p.IndexOfAny(Path.GetInvalidPathChars()) < 0)
            .Select(p => Path.GetFullPath(Path.Combine(rootFull, p.Replace('/', Path.DirectorySeparatorChar))))
            // Refuse to follow one that "..." its way back out of the mod's own folder.
            .Where(p => p.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) && File.Exists(p))
            .ToList();

        if (fromRegistry.Count > 0) return fromRegistry;

        // Mods installed before lua checking existed have no .lua entries stored (and unmanaged
        // folders adopted later may have none either), so fall back to the folder itself rather
        // than reporting them as conflict-free without having looked.
        return Directory.EnumerateFiles(rootFull, "*.lua", SearchOption.AllDirectories)
            .Where(f => Path.GetExtension(f).Equals(".lua", StringComparison.OrdinalIgnoreCase));
    }

    // --- lua text parsing ------------------------------------------------------------------------
    //
    // Only literal, directly readable registrations are collected. Everything below is built to fail
    // towards silence: a form we cannot resolve is dropped, never guessed at, because a guessed
    // command name that happens to match another mod's real one is a conflict card about nothing.
    //
    // Known and accepted blind spots, all seen in real mods on this machine:
    //   * RegisterKeyBind(Key[n], ...) driven by a table of candidate key names (MifTools binds
    //     Ctrl+Shift+F5/F6/F4 this way) - the key is only decided at runtime.
    //   * RegisterKeyBindAsync(Keybinds[name].Key, ...) - the built-in Keybinds mod, which reads its
    //     keys out of a config table. It is also the one mod that already guards itself with
    //     IsKeyBindRegistered, so missing it costs nothing.
    //   * Registrations behind a wrapper more indirect than the one-parameter form below.

    /// Lua block comments, including the long form (--[==[ ... ]==]). Stripped before the line form
    /// because "--[[" starts with "--" too, and stripped at all because these mods carry long header
    /// comments quoting the very function paths and commands they are documenting.
    private static readonly Regex LuaBlockComment =
        new(@"--\[(?<eq>=*)\[.*?\]\k<eq>\]", RegexOptions.Singleline | RegexOptions.Compiled);

    /// Line comments. A "--" inside a string literal takes the rest of that line with it, which can
    /// only ever lose a registration, never invent one.
    private static readonly Regex LuaLineComment = new(@"--[^\r\n]*", RegexOptions.Compiled);

    private static readonly Regex LuaConsoleCommand =
        new(@"RegisterConsoleCommandHandler\s*\(\s*(?:""(?<name>[^""\r\n]+)""|'(?<name>[^'\r\n]+)')",
            RegexOptions.Compiled);

    /// The wrapper every hand-written mod here settles on:
    ///
    ///     local function cmd(name, fn)
    ///         local ok = pcall(function()
    ///             RegisterConsoleCommandHandler(name, function(...) ... end)
    ///
    /// Three of the 17 lua mods installed here (MifTools, MifQuestKit, MifEconLogger) register every
    /// one of their commands through a function of exactly this shape and nothing else, so without
    /// resolving it their 123 command names are invisible and the check quietly covers a third fewer
    /// mods than it claims to. The backreference is what keeps it from matching any old two-argument
    /// function: the wrapper's own first parameter has to be what gets handed to the registrar.
    private static readonly Regex LuaConsoleCommandWrapper =
        new(@"function\s+(?<fn>[A-Za-z_]\w*)\s*\(\s*(?<param>[A-Za-z_]\w*)\s*[,)][^\n]*\n(?:[^\n]*\n){0,6}?" +
            @"[^\n]*RegisterConsoleCommandHandler\s*\(\s*\k<param>\b",
            RegexOptions.Compiled);

    private static readonly Regex LuaKeybind =
        new(@"RegisterKeyBind(?:Async)?\s*\(\s*Key\.(?<key>[A-Za-z_0-9]+)\s*(?:,\s*\{(?<mods>[^}]*)\})?",
            RegexOptions.Compiled);

    private static readonly Regex LuaModifierKey = new(@"ModifierKey\.(?<mod>[A-Za-z_]+)", RegexOptions.Compiled);

    private static string StripLuaComments(string source) =>
        LuaLineComment.Replace(LuaBlockComment.Replace(source, " "), " ");

    private static IEnumerable<string> ExtractConsoleCommands(string code)
    {
        var names = new List<string>();

        foreach (Match m in LuaConsoleCommand.Matches(code))
            names.Add(m.Groups["name"].Value.Trim());

        foreach (Match wrapper in LuaConsoleCommandWrapper.Matches(code))
        {
            var call = new Regex(
                @"\b" + Regex.Escape(wrapper.Groups["fn"].Value) +
                @"\s*\(\s*(?:""(?<name>[^""\r\n]+)""|'(?<name>[^'\r\n]+)')");
            foreach (Match m in call.Matches(code))
                names.Add(m.Groups["name"].Value.Trim());
        }

        return names.Where(n => n.Length > 0);
    }

    /// "Ctrl+Shift+F5", "F3", "LEFT_MOUSE_BUTTON". Modifiers are sorted before they are joined so
    /// that {CONTROL, SHIFT} and {SHIFT, CONTROL} - the same binding to UE4SS - compare equal here.
    private static IEnumerable<string> ExtractKeybinds(string code)
    {
        foreach (Match m in LuaKeybind.Matches(code))
        {
            var modifiers = LuaModifierKey.Matches(m.Groups["mods"].Value)
                .Select(x => x.Groups["mod"].Value.ToUpperInvariant())
                .Distinct()
                .OrderBy(x => x, StringComparer.Ordinal)
                .Select(PrettyModifier)
                .ToList();

            var key = m.Groups["key"].Value;
            yield return modifiers.Count == 0 ? key : string.Join("+", modifiers) + "+" + key;
        }
    }

    private static string PrettyModifier(string modifier) => modifier switch
    {
        "CONTROL" => "Ctrl",
        "SHIFT" => "Shift",
        "ALT" => "Alt",
        _ => modifier
    };

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
