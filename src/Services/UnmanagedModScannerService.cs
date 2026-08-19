using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Versions;

namespace DDS2ModManager.Services;

/// Finds mods already sitting in the game folders that the registry doesn't know about - the
/// normal case for anyone who modded by hand before installing this manager.
///
/// The point is not just to list them: an unmanaged mod can't be enabled/disabled/uninstalled and
/// is invisible to conflict checking, and it may be in the wrong folder entirely (a LogicMod in
/// Content\Paks silently never loads). So this reads each one properly with CUE4Parse to work out
/// what it actually is, compares that against where it currently sits, and hands back enough info
/// for the caller to adopt it into the registry and optionally move it somewhere correct.
public class UnmanagedModScannerService
{
    /// Files Unreal/the game itself ships in Content\Paks. Anything matching these is the base
    /// game, not a mod. Matched on the name without extension, so it covers a whole
    /// .pak/.ucas/.utoc set at once.
    ///
    /// The project-name clause is load-bearing and was missing. UE5 cooks to "pakchunkN-Windows"
    /// and "global", which is all DDS2 ever produces - but UE4 names its single pak after the
    /// project: DDS1 ships "DrugDealerSimulator-WindowsNoEditor.pak", matching NEITHER of the other
    /// two rules. Without this the scanner would offer the user's entire 11.3 GB base game as an
    /// importable "mod", and the reset path feeds that same list to an unconditional File.Delete.
    public static bool IsBaseGameArchive(string baseName, GameInstallation game) =>
        baseName.StartsWith("pakchunk", StringComparison.OrdinalIgnoreCase) ||
        baseName.Equals("global", StringComparison.OrdinalIgnoreCase) ||
        baseName.StartsWith(game.ProjectName + "-", StringComparison.OrdinalIgnoreCase);

    /// Mod folders UE4SS itself ships inside ue4ss\Mods. These are part of UE4SS, not user mods,
    /// and adopting them would let the user "uninstall" pieces of UE4SS from the mod list.
    /// "shared" is a support folder rather than a mod, and mods.txt/mods.json are config files.
    private static readonly HashSet<string> UE4SSBuiltInMods = new(StringComparer.OrdinalIgnoreCase)
    {
        "ActorDumperMod", "BPML_GenericFunctions", "BPModLoaderMod", "CheatManagerEnablerMod",
        "ConsoleCommandsMod", "ConsoleEnablerMod", "EventViewerMod", "Keybinds", "KismetDebuggerMod",
        "LineTraceMod", "SplitScreenMod", "jsbLuaProfilerMod", "shared"
    };

    private readonly LuaModConfigService _lua = new();
    private readonly ModUpdateSourceResolver _updateSources = new();

    /// A manifest sitting in a SHARED folder can only be claimed by the mod it is named after.
    /// Every pak mod lives in Content\Paks\LogicMods together, so a bare .dds2mod.json there is
    /// ambiguous - and attributing it to the wrong mod would point that mod's updates at a
    /// repository belonging to someone else entirely.
    private ModUpdateSource? ManifestNamedAfter(UnmanagedMod mod)
    {
        foreach (var ending in ModManifest.FileNames)
        {
            var named = Path.Combine(mod.CurrentFolder, mod.Name + ending);
            if (File.Exists(named)) return _updateSources.FromManifestFile(named, mod.Name);
        }

        return null;
    }

    public List<UnmanagedMod> Scan(
        GameInstallation game, IEnumerable<ModInfo> knownMods, string mappingsPath, EGame egame, string? aesKeyHex)
    {
        var log = LoggingService.Instance;
        var results = new List<UnmanagedMod>();

        // Match on absolute file/folder paths rather than mod names: a hand-installed mod won't
        // necessarily be named the same as anything in the registry, but its files are unambiguous.
        var knownPaths = knownMods
            .SelectMany(m => m.InstallFiles)
            .Select(p => p.TrimEnd(Path.DirectorySeparatorChar))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        results.AddRange(ScanLuaMods(game, knownPaths));

        // Only where the engine can actually load them. On an IoStore game there is no loose-file
        // path for it to prefer, so anything under Content there is not a mod.
        if (game.Profile.SupportsLooseAssets)
            results.AddRange(ScanLooseAssets(game, knownPaths));

        var pakGroups = FindUnmanagedPakGroups(game, knownPaths);
        if (pakGroups.Count > 0)
        {
            DefaultFileProvider? provider = null;
            try
            {
                provider = GameMountService.Mount(game.PaksPath, mappingsPath, egame, aesKeyHex);
                DataTableAppendScanner.EnableScriptReading(provider);
                var appendScanner = new DataTableAppendScanner();

                foreach (var group in pakGroups)
                {
                    var mod = BuildPakMod(game, group, provider);
                    if (mod.HasModActor)
                    {
                        mod.DataTableAppends = appendScanner.Scan(provider, mod.Name, mod.ContainedAssetPaths);
                        mod.UpdateSource = _updateSources.FromModActor(
                            provider, mod.ContainedAssetPaths, mod.Name);
                    }

                    // Patch mods have no ModActor, and a LogicMod author may have used the
                    // manifest instead, so fall back to it either way.
                    //
                    // Matched by NAME, not by searching the folder: CurrentFolder for a pak mod is
                    // the shared LogicMods root, so a recursive search there would find a
                    // neighbour's manifest and offer updates from someone else's repository.
                    mod.UpdateSource ??= ManifestNamedAfter(mod);

                    results.Add(mod);
                }
            }
            catch (Exception ex)
            {
                // Without a mount we can't type any pak mod, so rather than reporting them all as
                // unverifiable we report nothing and say why - a scan that silently found "0 mods"
                // when the real answer is "couldn't look" would be actively misleading.
                log.Error($"Couldn't read the game's pak files to identify existing mods: {ex.Message}");
                return results;
            }
            finally
            {
                (provider as IDisposable)?.Dispose();
            }
        }

        return results;
    }

    /// Cooked asset extensions that travel as a set. A .uasset is the header, .uexp the exports,
    /// .ubulk the bulk data, and .umap is a level rather than an asset.
    private static readonly string[] CookedExtensions = [".uasset", ".uexp", ".ubulk", ".umap"];

    /// Loose cooked assets a person copied into the game's Content folder.
    ///
    /// The rule that makes this scannable: **a vanilla install has exactly one thing under Content,
    /// and that is the Paks folder.** Everything else is something someone put there. That matters
    /// because ownership of an individual file genuinely cannot be recovered - an overriding
    /// .uasset at a vanilla path is byte-identical in kind to the file it shadows, and nothing on
    /// disk records which mod supplied it - but "did the game ship this folder?" has a clean answer.
    ///
    /// Reported per top-level folder rather than per file. A mod shipping forty assets across three
    /// categories is not forty mods, and with no manifest there is nothing to tell where one mod
    /// ends and the next begins - so the row says it is a folder and says ownership is unknown,
    /// instead of inventing a mod name and a boundary.
    private List<UnmanagedMod> ScanLooseAssets(GameInstallation game, HashSet<string> knownPaths)
    {
        var results = new List<UnmanagedMod>();
        if (!Directory.Exists(game.ContentPath)) return results;

        var paks = Path.TrimEndingDirectorySeparator(Path.GetFullPath(game.PaksPath));

        foreach (var folder in Directory.GetDirectories(game.ContentPath))
        {
            // Paks is the game's own, and its contents are covered by the pak scan above.
            if (Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder))
                .Equals(paks, StringComparison.OrdinalIgnoreCase)) continue;

            List<string> files;
            try
            {
                files = Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
                    .Where(f => CookedExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                    .Where(f => !knownPaths.Contains(f.TrimEnd(Path.DirectorySeparatorChar)))
                    .ToList();
            }
            catch (Exception ex)
            {
                LoggingService.Instance.Warn($"Couldn't read '{Path.GetFileName(folder)}': {ex.Message}");
                continue;
            }

            if (files.Count == 0) continue;

            var name = Path.GetFileName(folder);

            results.Add(new UnmanagedMod
            {
                Name = name,
                DetectedType = ModType.LooseAsset,
                TypeAssumedFromLocation = true,
                IsLooseAssetGroup = true,
                CurrentFolder = folder,
                CorrectFolder = folder,
                Files = files,

                // Relative to Content, which is the namespace loose mods are compared in.
                ContainedAssetPaths = files
                    .Select(f => Path.GetRelativePath(game.ContentPath, f).Replace('\\', '/'))
                    .ToList(),

                Issues =
                {
                    $"{files.Count} loose asset file(s) in Content\\\\{name}. These override whatever the game packs " +
                    "at the same path. Which mod they came from can't be recovered - nothing on disk records it - " +
                    "so they're listed as one folder. Importing tracks them together; it moves nothing."
                }
            });
        }

        return results;
    }

    private record PakGroup(string BaseName, string Folder, List<string> Files);

    private List<PakGroup> FindUnmanagedPakGroups(GameInstallation game, HashSet<string> knownPaths)
    {
        var groups = new List<PakGroup>();

        // Where pak mods actually live:
        //
        //   Content\Paks                 patch mods, loose
        //   Content\Paks\Mods            flat override paks
        //   Content\Paks\LogicMods       logic mods - almost always in a SUBFOLDER PER MOD
        //
        // Each folder is still scanned top-level-only, because LogicMods and Mods sit inside
        // Paks and a recursive scan of Paks would find every mod twice and report it in the
        // wrong folder. The subfolders are enumerated explicitly instead.
        //
        // That per-mod subfolder is the whole point of this change. UE4SS's BPModLoaderMod
        // loads LogicMods\<Name>\<Name>.pak, which is what every deploy script and this
        // project's own README produce - and scanning only the top level of LogicMods found
        // exactly none of them. On a real install with three logic mods present, the scan
        // reported "no untracked mods found", which reads as "you're all set" rather than
        // "I didn't look in the right place".
        // Content\Paks\DisabledMods is included too. It is not a folder this manager creates -
        // disabling through the UI parks files in %AppData%, outside the game entirely - but people
        // do create it by hand believing it switches a mod off.
        //
        // IT DOES NOT. Unreal enumerates Content\Paks recursively, so those paks mount and load
        // normally. Scanning the folder is still right: those mods are installed, are the user's,
        // and were invisible to conflict checking. What changed is that we no longer repeat the
        // claim that they are disabled. The only in-place hand-disable that actually works is
        // renaming the file so it no longer ends in .pak.
        var disabledRoot = Path.Combine(game.PaksPath, "DisabledMods");
        var modsRoot = Path.Combine(game.PaksPath, "Mods");

        var roots = new List<string> { game.PaksPath, game.LogicModsPath, modsRoot, disabledRoot };

        foreach (var parent in new[] { game.LogicModsPath, modsRoot, disabledRoot })
        {
            if (!Directory.Exists(parent)) continue;
            roots.AddRange(Directory.GetDirectories(parent));
        }

        foreach (var folder in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(folder)) continue;

            var byBaseName = Directory.GetFiles(folder, "*", SearchOption.TopDirectoryOnly)
                .Where(f => game.Profile.ContainerExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .GroupBy(f => Path.GetFileNameWithoutExtension(f), StringComparer.OrdinalIgnoreCase);

            foreach (var g in byBaseName)
            {
                if (IsBaseGameArchive(g.Key, game)) continue;
                if (g.Any(f => knownPaths.Contains(f.TrimEnd(Path.DirectorySeparatorChar)))) continue;
                groups.Add(new PakGroup(g.Key, folder, g.ToList()));
            }
        }

        return groups;
    }

    private UnmanagedMod BuildPakMod(GameInstallation game, PakGroup group, DefaultFileProvider provider)
    {
        var mod = new UnmanagedMod
        {
            Name = group.BaseName,
            CurrentFolder = group.Folder,
            Files = group.Files
        };

        // Both the .pak and the .utoc register as their own archive reader; a mod's content may
        // come from either, so read both and union.
        var archiveNames = group.Files
            .Where(f => Path.GetExtension(f).Equals(".pak", StringComparison.OrdinalIgnoreCase) ||
                        Path.GetExtension(f).Equals(".utoc", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileName)
            .Where(n => n != null)
            .Select(n => n!);

        var paths = GameMountService.ReadArchivePaths(provider, archiveNames);

        if (paths.Count > 0)
        {
            mod.ContainedAssetPaths = paths.ToList();
            mod.HasModActor = paths.Any(p =>
                Path.GetFileNameWithoutExtension(p).Equals("ModActor", StringComparison.OrdinalIgnoreCase));
            mod.DetectedType = mod.HasModActor ? ModType.LogicMod : ModType.PatchMod;
        }
        else
        {
            // Couldn't read it. Fall back to "it is whatever its current folder implies" - see
            // UnmanagedMod.TypeAssumedFromLocation for why that's an acceptable guess here
            // specifically (unlike at install time, where a wrong guess picks the wrong folder).
            mod.TypeAssumedFromLocation = true;
            mod.DetectedType = IsUnderLogicMods(game, group.Folder) ? ModType.LogicMod : ModType.PatchMod;
            mod.Issues.Add("Couldn't read this mod's pak, so its type was assumed from the folder it's in.");
        }

        // A logic mod in its OWN SUBFOLDER of LogicMods is correctly placed - that is the layout
        // UE4SS's BPModLoaderMod expects and what every deploy script produces. Comparing against
        // LogicMods itself would mark every one of them "misplaced" and offer to move it up a
        // level, i.e. offer to break a working install. So a mod already somewhere under
        // LogicMods is considered to be where it belongs, and only one that is genuinely outside
        // gets moved.
        var underLogicMods = IsUnderLogicMods(game, group.Folder);

        // A mod under Content\Paks\DisabledMods is NOT switched off, however much the folder name
        // suggests it. Unreal discovers pak files under Content\Paks RECURSIVELY
        // (FPakPlatformFile::FindPakFilesInDirectory -> IterateDirectoryRecursively), so a pak in a
        // subfolder there mounts exactly like one at the top level. A _P mod parked there is still
        // overriding base-game assets at +100 priority while the folder claims it is disabled.
        //
        // The scan still adopts them - they ARE the user's mods and tracking them is right - but the
        // wording now says what is actually happening, and importing genuinely disables them by
        // moving the files out of the game folder entirely (see ImportUnmanaged).
        var underDisabled = IsUnder(Path.Combine(game.PaksPath, "DisabledMods"), group.Folder);
        if (underDisabled)
        {
            mod.IsEnabled = false;
            mod.ParkedInGameFolder = true;
            mod.CorrectFolder = group.Folder;
            mod.Issues.Add(
                "This is in a DisabledMods folder inside the game, but the game still loads it - Unreal scans "
                + "Content\\Paks and everything under it. Importing will move the files out of the game folder, "
                + "which actually switches it off.");
        }
        else
        {
            mod.CorrectFolder = mod.DetectedType == ModType.LogicMod
                ? (underLogicMods ? group.Folder : game.LogicModsPath)
                : (underLogicMods ? game.PaksPath : group.Folder);
        }

        if (mod.IsMisplaced)
        {
            // Loader-neutral wording: DDS2 loads these through UE4SS's BPModLoaderMod, DDS1's scene
            // through UnrealModLoader. Both read the same LogicMods folder, and naming the wrong one
            // sends a DDS1 user looking for a tool they do not have.
            mod.Issues.Add(mod.DetectedType == ModType.LogicMod
                ? "This is a LogicMod but it's in Content\\Paks - logic mods are only loaded from Content\\Paks\\LogicMods, so it almost certainly isn't working."
                : "This is a regular mod but it's in Content\\Paks\\LogicMods - it should be in Content\\Paks.");
        }

        AddFileCompletenessIssues(mod, group);
        return mod;
    }

    /// A .utoc and .ucas are two halves of one IoStore container - one without the other can't
    /// load. A lone .pak, by contrast, is a perfectly valid legacy-format mod, so that's not flagged.
    private static void AddFileCompletenessIssues(UnmanagedMod mod, PakGroup group)
    {
        bool Has(string ext) => group.Files.Any(f => Path.GetExtension(f).Equals(ext, StringComparison.OrdinalIgnoreCase));

        if (Has(".utoc") && !Has(".ucas"))
            mod.Issues.Add("Missing its .ucas file - the mod's data is incomplete and it won't load.");
        else if (Has(".ucas") && !Has(".utoc"))
            mod.Issues.Add("Missing its .utoc file - the mod's data is incomplete and it won't load.");
    }

    /// True for Content\Paks\LogicMods itself AND anything beneath it.
    ///
    /// Mods normally sit in their own subfolder there (LogicMods\MyMod\MyMod.pak), so an exact
    /// equality test answers "no" for the overwhelmingly common case.
    private static bool IsUnderLogicMods(GameInstallation game, string folder) =>
        IsUnder(game.LogicModsPath, folder);

    /// True when candidate is root itself or anything beneath it.
    private static bool IsUnder(string root, string candidate)
    {
        var r = root.TrimEnd(Path.DirectorySeparatorChar);
        var c = candidate.TrimEnd(Path.DirectorySeparatorChar);

        if (c.Equals(r, StringComparison.OrdinalIgnoreCase)) return true;

        // Compare with the separator appended so a sibling like "LogicModsOld" cannot match.
        return c.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private List<UnmanagedMod> ScanLuaMods(GameInstallation game, HashSet<string> knownPaths)
    {
        var results = new List<UnmanagedMod>();
        if (!Directory.Exists(game.UE4SSModsPath)) return results;

        var enabledEntries = _lua.ReadEntries(game);

        foreach (var dir in Directory.GetDirectories(game.UE4SSModsPath))
        {
            var name = Path.GetFileName(dir);
            if (UE4SSBuiltInMods.Contains(name)) continue;
            if (knownPaths.Contains(dir.TrimEnd(Path.DirectorySeparatorChar))) continue;

            // UE4SS loads Lua mods from Scripts\main.lua and C++ mods from dlls\main.dll. A folder
            // with neither isn't a mod UE4SS would load, so don't offer to manage it as one.
            var hasLua = File.Exists(Path.Combine(dir, "Scripts", "main.lua"));
            var hasDll = Directory.Exists(Path.Combine(dir, "dlls")) &&
                         Directory.GetFiles(Path.Combine(dir, "dlls"), "*.dll").Length > 0;
            if (!hasLua && !hasDll) continue;

            var mod = new UnmanagedMod
            {
                Name = name,
                DetectedType = ModType.LuaMod,
                CurrentFolder = game.UE4SSModsPath,
                CorrectFolder = game.UE4SSModsPath,
                Files = new List<string> { dir },
                IsEnabled = enabledEntries.TryGetValue(name, out var on) && on,
                ContainedAssetPaths = Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                    .Select(f => Path.GetRelativePath(dir, f).Replace('\\', '/'))
                    .ToList(),

                // dir, not CurrentFolder: CurrentFolder is the shared UE4SS Mods root, and
                // scanning that would pick up a neighbouring mod's manifest and attribute it
                // to this one. A lua mod owns `dir` outright, so searching it is safe.
                UpdateSource = _updateSources.FromManifestFolder(dir, name)
            };

            if (!enabledEntries.ContainsKey(name))
                mod.Issues.Add("Not listed in mods.txt - UE4SS won't load it until it is. Importing will add it.");
            else if (!mod.IsEnabled)
                mod.Issues.Add("Currently disabled in mods.txt.");

            results.Add(mod);
        }

        return results;
    }
}
