namespace DDS2ModManager.Services;

public class PreparedInstall
{
    /// The temp folder the archive was extracted to (or the original folder, if a folder was
    /// dropped in directly). Only ever deleted by the caller if IsTempExtraction is true.
    public string ExtractedRoot { get; set; } = "";
    public bool IsTempExtraction { get; set; }

    /// More than one entry means the archive contains several self-contained mod variants
    /// (see ModVariantDetectionService) and the caller must ask the user to pick one before
    /// calling InstallFromRootAsync.
    public List<string> VariantCandidates { get; set; } = new();

    /// Non-empty when the archive is laid out by DESTINATION rather than as one mod - a pak
    /// half and a lua half that belong in two different game folders (see
    /// ModArchiveLayoutService). Every entry is installed, in contrast to VariantCandidates
    /// where exactly one is. The two are mutually exclusive: a destination layout is not a
    /// choice the user should be asked to make.
    public List<string> DestinationParts { get; set; } = new();
}

/// Core install/uninstall/enable/disable logic. Disable is implemented as "move the
/// files out of the game folder into our own cache" for pak-based mods, because UE4
/// will load any pak/logicmod pak it finds regardless of any in-game toggle. Lua mods
/// are the one exception - UE4SS itself honors mods.txt, so disabling those is just a
/// config edit and the files never move.
public class ModInstallerService
{
    private readonly GameInstallation _game;
    private readonly ModAnalyzerService _analyzer;
    private readonly ModRegistryService _registry;
    private readonly LuaModConfigService _lua = new();
    private readonly ModUpdateSourceResolver _updateSources = new();
    private readonly string _disabledCacheDir;

    public ModInstallerService(GameInstallation game, ModAnalyzerService analyzer, ModRegistryService registry)
    {
        _game = game;
        _analyzer = analyzer;
        _registry = registry;
        // Scoped per install, like the mod registry and disabled saves. Flat, this was an
        // unattributed pool: mod ids are GUIDs so folders don't collide, but nothing recorded which
        // game owned each one, so "Open Disabled Mods" showed an undifferentiated pile and two
        // installs' disabled mods could never be told apart.
        _disabledCacheDir = AppPaths.DisabledModsFor(game.RootPath);
        Directory.CreateDirectory(_disabledCacheDir);
    }

    /// Step 1: extract the archive (if needed) and check whether it contains multiple mod
    /// variants. Does not install anything yet.
    public PreparedInstall PrepareInstall(string sourcePath)
    {
        var log = LoggingService.Instance;

        if (Directory.Exists(sourcePath)) return Describe(sourcePath, isTemp: false);

        if (File.Exists(sourcePath) && ArchiveExtractionService.IsSupportedArchive(sourcePath))
        {
            var tempExtract = Path.Combine(Path.GetTempPath(), "DDS2MM_Install_" + Guid.NewGuid().ToString("N"));
            log.Info($"Extracting '{Path.GetFileName(sourcePath)}'...");
            ArchiveExtractionService.ExtractToDirectory(sourcePath, tempExtract);

            return Describe(tempExtract, isTemp: true);
        }

        throw new InvalidOperationException(
            $"Unsupported mod source (expected a folder, or a {string.Join("/", ArchiveExtractionService.SupportedExtensions)} archive): {sourcePath}");
    }

    /// A destination layout is checked FIRST and suppresses variant detection entirely.
    /// Otherwise the two halves of one mod look like two variants of it, and the user is asked
    /// to choose between "UE4SSMods" and "LogicMods" as though they were x2/x5 multipliers -
    /// then gets half a mod whichever they pick.
    private static PreparedInstall Describe(string root, bool isTemp)
    {
        var parts = ModArchiveLayoutService.DetectParts(root);

        if (parts.Count > 0)
        {
            LoggingService.Instance.Info(
                $"This archive installs to {parts.Count} locations - all of them will be installed.");
            return new PreparedInstall
            {
                ExtractedRoot = root,
                IsTempExtraction = isTemp,
                DestinationParts = parts,
                VariantCandidates = new List<string> { root }
            };
        }

        return new PreparedInstall
        {
            ExtractedRoot = root,
            IsTempExtraction = isTemp,
            VariantCandidates = ModVariantDetectionService.DetectCandidates(root)
        };
    }

    /// Step 2: analyze + copy files into the game. chosenRoot is either prepared.ExtractedRoot
    /// itself (no variants) or the single folder the user picked from prepared.VariantCandidates.
    /// keepExtraction leaves the temp folder in place for the caller to reuse and delete.
    /// A destination-layout archive is installed from the SAME extraction more than once, and
    /// without this the first part's cleanup deletes the second part's source out from under
    /// it - "Could not find a part of the path ...\UE4SSMods\MyMod", after which only half the
    /// mod is installed and the run still reports success for the half that made it.
    public async Task<ModInfo?> InstallFromRootAsync(
        string originalSourcePath, PreparedInstall prepared, string chosenRoot, bool keepExtraction = false)
    {
        var log = LoggingService.Instance;

        try
        {
            log.Info("Analyzing mod contents with CUE4Parse...");
            var analysis = await Task.Run(() => _analyzer.Analyze(chosenRoot));
            foreach (var w in analysis.Warnings) log.Warn(w);

            if (analysis.Type == ModType.Unknown)
            {
                log.Error(analysis.ParseFailed
                    ? "Installation blocked: CUE4Parse could not verify this mod's contents (see warnings above)."
                    : "Installation blocked: couldn't determine a mod type for this archive.");
                return null;
            }

            var isTempRoot = prepared.IsTempExtraction && SamePath(chosenRoot, prepared.ExtractedRoot);
            var name = InferModName(chosenRoot, analysis.Type, isTempRoot);

            if (name == null)
            {
                log.Error(
                    $"Installation blocked: nothing in this archive names the mod. It has no .pak, no Scripts " +
                    "folder, and no single file this manager can name it after - so it would be listed under the " +
                    "temporary folder it was unpacked into. Extract the archive into a folder named after the mod " +
                    "and install that folder instead.");
                return null;
            }

            // Same reasoning as ImportUnmanaged: a name clash across DIFFERENT types is the two
            // halves of one mod, not a duplicate. Refusing on name alone made the second half of
            // every two-part archive un-installable.
            var clash = _registry.Mods.FirstOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (clash != null)
            {
                if (clash.Type == analysis.Type)
                {
                    log.Warn($"A mod named '{name}' is already installed. Uninstall it first if you mean to replace it.");
                    return null;
                }

                name = $"{name} ({analysis.Type})";
                log.Info($"'{clash.Name}' is already installed as {clash.Type}, so this half is listed as '{name}'.");
            }

            var mod = new ModInfo
            {
                Name = name,
                Type = analysis.Type,
                SourcePath = originalSourcePath,
                ContainedAssetPaths = analysis.AssetPaths,
                HasModActor = analysis.HasModActor,
                DataTableAppends = analysis.DataTableAppends,
                // The analyzer scans appends for anything with a ModActor, so a mod that has one
                // has definitively been checked - even if the result was "merges nothing".
                DataTableScanCompleted = analysis.HasModActor,

                UpdateSource = analysis.UpdateSource,

                // Pinned here, at install time, from the copy the user actually downloaded -
                // which for a Nexus download is a copy Nexus scanned. ModInfo.UpdateUrlChanged
                // compares against this later, so a mod that starts pointing somewhere new is
                // visible rather than silently followed.
                InstalledUpdateUrl = analysis.UpdateSource?.DeclaredUrl
            };

            switch (analysis.Type)
            {
                case ModType.LogicMod:
                    // InstallPakTriple creates the folder if it's missing. UE4SS also creates it
                    // itself the first time the game runs, so there's nothing special about it -
                    // blocking the install until the user had launched the game once was pure
                    // friction for a directory we can just make.
                    InstallPakTriple(chosenRoot, _game.LogicModsPath, mod);
                    break;

                case ModType.PatchMod:
                    InstallPakTriple(chosenRoot, _game.PaksPath, mod);
                    break;

                case ModType.LuaMod:
                    if (!InstallLuaMod(chosenRoot, mod, isTempRoot)) return null;
                    break;

                case ModType.LooseAsset:
                    InstallLooseAssets(chosenRoot, mod);
                    break;

                case ModType.DllPlugin:
                    if (!InstallDllPlugin(chosenRoot, mod)) return null;
                    break;
            }

            mod.IsInstalled = true;
            mod.IsEnabled = true;
            _registry.Upsert(mod);
            log.Success($"Installed '{mod.Name}' as {mod.Type}.");
            return mod;
        }
        catch (Exception ex)
        {
            log.Error($"Installation failed: {ex.Message}");
            return null;
        }
        finally
        {
            if (prepared.IsTempExtraction && !keepExtraction)
                try { Directory.Delete(prepared.ExtractedRoot, true); } catch { }
        }
    }

    /// Convenience wrapper for the common case (no variants to choose between).
    public async Task<ModInfo?> InstallAsync(string sourcePath)
    {
        var prepared = PrepareInstall(sourcePath);
        var chosenRoot = prepared.VariantCandidates.Count == 1 ? prepared.VariantCandidates[0] : prepared.ExtractedRoot;
        return await InstallFromRootAsync(sourcePath, prepared, chosenRoot);
    }

    /// Adopts a mod that was already sitting in the game folders (see UnmanagedModScannerService)
    /// into the registry, so it can be enabled/disabled/uninstalled and included in conflict
    /// checks like any normally-installed mod. Copies nothing - the files are already in place -
    /// unless fixMisplaced is set and the mod is in the wrong folder for its type.
    public ModInfo? ImportUnmanaged(UnmanagedMod found, bool fixMisplaced)
    {
        var log = LoggingService.Instance;
        try
        {
            // Match on FILES, not on the name.
            //
            // Plenty of mods ship in two halves that share a name: a lua script under
            // ue4ss\Mods\<Name> and a pak under Paks\LogicMods\<Name>. Refusing anything whose
            // name was already tracked meant adopting the lua half made the pak half
            // permanently un-adoptable - it was reported as "a different mod with that name is
            // already tracked" and silently skipped, so the pak stayed invisible to conflict
            // checking and to update checks forever.
            //
            // Files are unambiguous where names are not, and the scanner already uses exactly
            // this test to decide what counts as unmanaged in the first place.
            var alreadyTracked = _registry.Mods
                .SelectMany(m => m.InstallFiles)
                .Select(p => p.TrimEnd(Path.DirectorySeparatorChar))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (found.Files.Any(f => alreadyTracked.Contains(f.TrimEnd(Path.DirectorySeparatorChar))))
            {
                log.Warn($"Skipped importing '{found.Name}' - its files are already tracked by another mod.");
                return null;
            }

            // A same-name, different-type pair is legitimate, but two rows reading
            // "DriveableScooter" is confusing, so say which half this one is.
            var name = found.Name;
            if (_registry.Mods.Any(m => m.Name.Equals(found.Name, StringComparison.OrdinalIgnoreCase)))
            {
                name = $"{found.Name} ({found.DetectedType})";
                log.Info($"'{found.Name}' is already tracked as a different type, so this one is listed as '{name}'.");
            }

            var mod = new ModInfo
            {
                Name = name,
                Type = found.DetectedType,
                // No archive to point at: these were installed by hand, so the files in the game
                // folder are the only source that ever existed for them.
                SourcePath = found.CurrentFolder,
                ContainedAssetPaths = found.ContainedAssetPaths,
                HasModActor = found.HasModActor,
                DataTableAppends = found.DataTableAppends,
                DataTableScanCompleted = found.HasModActor,
                InstallPath = found.CurrentFolder,
                InstallFiles = found.Files,
                IsInstalled = true,
                IsEnabled = found.IsEnabled,
                UpdateSource = found.UpdateSource,

                // An adopted mod was installed by hand, so the address it declares now is the
                // earliest one this manager can honestly claim to have seen. Pinning it here is
                // what gives UpdateUrlChanged something to compare against from now on.
                InstalledUpdateUrl = found.UpdateSource?.DeclaredUrl
            };

            if (found.DetectedType == ModType.LuaMod)
            {
                mod.InstallPath = found.Files.FirstOrDefault() ?? found.CurrentFolder;
                // Writes the mods.txt entry if it was missing entirely (UE4SS wouldn't have been
                // loading it at all), and otherwise preserves whatever state it's already in.
                _lua.SetEnabled(_game, mod.Name, found.IsEnabled);
            }
            else if (fixMisplaced && found.IsMisplaced)
            {
                MoveModFiles(mod, found.CorrectFolder);
                log.Success($"Moved '{mod.Name}' to {found.CorrectFolder} so the game will actually load it.");
            }

            // Recorded FIRST, deliberately. If the move below throws, the registry still holds the
            // pre-move file list, so the mod is tracked and recoverable rather than half-adopted
            // with paths pointing nowhere.
            _registry.Upsert(mod);
            log.Success($"Imported existing mod '{mod.Name}' as {mod.Type}.");

            // The mod was sitting in a DisabledMods folder INSIDE the game, which disables nothing -
            // Unreal enumerates Content\Paks recursively and loads it anyway. Adopting it as
            // "disabled" while the game still loads it would carry the same lie forward, so put the
            // files where being disabled is actually true: out of the game folder entirely.
            if (found.ParkedInGameFolder && mod.Type is ModType.LogicMod or ModType.PatchMod)
            {
                log.Info(
                    $"'{mod.Name}' was in a DisabledMods folder inside the game, where it was still being " +
                    "loaded. Moving its files out so it really is switched off.");
                Disable(mod);
            }

            return mod;
        }
        catch (Exception ex)
        {
            log.Error($"Failed to import '{found.Name}': {ex.Message}");
            return null;
        }
    }

    /// Moves a pak mod's files into destFolder and updates the mod's recorded paths to match.
    private void MoveModFiles(ModInfo mod, string destFolder)
    {
        Directory.CreateDirectory(destFolder);
        var newFiles = new List<string>();
        foreach (var f in mod.InstallFiles.Where(File.Exists))
        {
            var dest = Path.Combine(destFolder, Path.GetFileName(f));
            File.Move(f, dest, true);
            newFiles.Add(dest);
        }
        mod.InstallFiles = newFiles;
        mod.InstallPath = destFolder;
    }

    /// The name a mod is listed under, or NULL when nothing in the archive names it.
    ///
    /// Null matters. The name is not a label: it is the duplicate-install key, the profile match
    /// key, the Nexus match key and the mod-list group key, and there is no rename anywhere in the
    /// UI. So a name that cannot be resolved has to stop the install, not be filled in.
    ///
    /// It used to fall back to the working directory's own name. For an ARCHIVE install that
    /// directory is the temp folder PrepareInstall mints - "DDS2MM_Install_<guid>" - so every mod
    /// shape with no .pak and no Scripts folder was listed, keyed and stored under a GUID.
    /// DllPlugin and LooseAsset hit that unconditionally: the analyzer only assigns those two types
    /// when there is no pak and no main.lua, which is exactly the negation of both earlier rules.
    ///
    /// workingDirIsTempRoot is passed rather than sniffed for. Deciding it here would mean matching
    /// on the "DDS2MM_Install_" prefix, which is a guess about our own temp names; the caller
    /// already knows the answer for certain.
    private string? InferModName(string workingDir, ModType type, bool workingDirIsTempRoot)
    {
        // What the author called it beats anything inferred from the packaging. It is the only
        // source here that is a STATEMENT rather than a deduction - everything below reads a
        // filename or a folder and reasons about what it probably means.
        var declared = _updateSources.NameFromManifestFolder(workingDir, Path.GetFileName(workingDir));
        if (declared != null) return declared;

        if (type == ModType.LuaMod)
        {
            var scriptsDir = Directory.GetDirectories(workingDir, "Scripts", SearchOption.AllDirectories).FirstOrDefault();
            if (scriptsDir != null)
            {
                var luaRoot = Path.GetDirectoryName(scriptsDir)!;
                if (!(workingDirIsTempRoot && SamePath(luaRoot, workingDir)))
                    return Path.GetFileName(luaRoot.TrimEnd(Path.DirectorySeparatorChar));
            }
        }

        var pak = Directory.GetFiles(workingDir, "*.pak", SearchOption.AllDirectories).FirstOrDefault();
        if (pak != null) return Path.GetFileNameWithoutExtension(pak);

        // A DLL plugin IS its DLL: InstallDllPlugin copies it by filename into a flat folder the
        // loader keys on filename, so the basename is already this mod's identity on disk - the
        // same reasoning that makes the pak rule right, and stable across versions in a way a
        // download filename is not.
        //
        // Exactly one, never the first of several. Two DLLs is a framework plus its dependency, or
        // two mods in one archive, and picking either one names the mod after a coin flip.
        if (type == ModType.DllPlugin)
        {
            var dlls = Directory.GetFiles(workingDir, "*.dll", SearchOption.AllDirectories);
            if (dlls.Length == 1) return Path.GetFileNameWithoutExtension(dlls[0]);
        }

        // Loose assets are named after the folder wrapping Content\, mirroring the lua rule.
        // Refused when that folder is the archive root (nothing named it) or the project folder -
        // authors routinely ship "DrugDealerSimulator\Content\...", and listing a mod under the
        // game's own project name is a wrong name, not a fallback.
        if (type == ModType.LooseAsset)
        {
            var contentDir = ModAnalyzerService.FindLooseAssetRoot(workingDir);
            var wrapper = contentDir != null && !SamePath(contentDir, workingDir)
                ? Path.GetDirectoryName(contentDir.TrimEnd(Path.DirectorySeparatorChar))
                : null;

            if (wrapper != null && !SamePath(wrapper, workingDir))
            {
                var candidate = Path.GetFileName(wrapper.TrimEnd(Path.DirectorySeparatorChar));
                if (!string.Equals(candidate, _game.ProjectName, StringComparison.OrdinalIgnoreCase))
                    return candidate;
            }
        }

        // Correct for a dropped folder and for a chosen sub-root - both are folders a person named.
        // Only the temp extraction root is nameless.
        if (workingDirIsTempRoot) return null;

        return Path.GetFileName(workingDir.TrimEnd(Path.DirectorySeparatorChar));
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            StringComparison.OrdinalIgnoreCase);

    private void InstallPakTriple(string workingDir, string destFolder, ModInfo mod)
    {
        // Logic mods go in their OWN subfolder, named after the pak. That is the layout UE4SS's
        // BPModLoaderMod expects, what every deploy script produces, and what Enable() restores
        // to - installing flat into LogicMods put a fresh install in a different shape from the
        // same mod after one disable/enable cycle.
        // Only where the game's loader actually reads subfolders. On DDS1 it does not: UnrealModLoader
        // scans LogicMods flat, so a nested pak mounts but its ModActor never spawns - the mod appears
        // to install fine and then does nothing, with no error anywhere to explain it.
        if (mod.Type == ModType.LogicMod && _game.Profile.LogicModsUseSubfolders)
        {
            var pakName = Directory.GetFiles(workingDir, "*.pak", SearchOption.AllDirectories)
                .Select(Path.GetFileNameWithoutExtension)
                .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));

            if (!string.IsNullOrWhiteSpace(pakName)) destFolder = Path.Combine(destFolder, pakName);
        }

        Directory.CreateDirectory(destFolder);
        var moved = new List<string>();

        // Driven by the game's layout. On an IoStore title the three files are one container and a
        // missing member is a real defect worth warning about; on UE4 a mod IS a single .pak, and
        // looking for the other two would warn about their absence on every single install.
        foreach (var ext in _game.Profile.ContainerExtensions)
        {
            var src = Directory.GetFiles(workingDir, "*" + ext, SearchOption.AllDirectories).FirstOrDefault();
            if (src == null)
            {
                LoggingService.Instance.Warn($"'{mod.Name}' is missing a {ext} file - it may not load correctly.");
                continue;
            }
            var dest = Path.Combine(destFolder, Path.GetFileName(src));
            File.Copy(src, dest, true);
            moved.Add(dest);
        }

        mod.InstallPath = destFolder;
        mod.InstallFiles = moved;
    }

    /// The name UE4SS knows a lua mod by: the folder on disk, which is what mods.txt keys on.
    ///
    /// NOT mod.Name - that is a display label and can carry a "(LuaMod)" suffix when both
    /// halves of a two-part mod are installed. Enabling with the display name writes an entry
    /// naming a folder that does not exist, so the mod silently stops loading.
    private static string LuaFolderName(ModInfo mod) =>
        !string.IsNullOrWhiteSpace(mod.InstallPath) && Directory.Exists(mod.InstallPath)
            ? Path.GetFileName(mod.InstallPath.TrimEnd(Path.DirectorySeparatorChar))
            : mod.Name;

    /// Copies a lua mod into UE4SS's Mods folder. False means the install was refused.
    private bool InstallLuaMod(string workingDir, ModInfo mod, bool workingDirIsTempRoot)
    {
        Directory.CreateDirectory(_game.UE4SSModsPath);
        var scriptsDir = Directory.GetDirectories(workingDir, "Scripts", SearchOption.AllDirectories).FirstOrDefault();
        var modRoot = scriptsDir != null ? Path.GetDirectoryName(scriptsDir)! : workingDir;

        // The folder on disk is named after the SOURCE folder, never after mod.Name.
        //
        // mod.Name is a display label and may carry a disambiguating suffix when both halves of
        // a two-part mod are installed - "SpecialClientMarker (LuaMod)". UE4SS loads the folder
        // named in mods.txt, so writing that suffix to disk produces a folder the loader has no
        // reason to recognise, and an entry in mods.txt that does not name the mod.
        //
        // Which is also why an archive shipping Scripts\ with no folder around it has to be refused
        // rather than named from anywhere else. modRoot is then the temp extraction root, and its
        // name is a GUID that would be created under ue4ss\Mods AND written into mods.txt - a
        // wrong name in the one place it is load-bearing rather than cosmetic.
        if (workingDirIsTempRoot && SamePath(modRoot, workingDir))
        {
            LoggingService.Instance.Error(
                "Installation blocked: this archive ships its Scripts folder with no mod folder around it, so " +
                "there is no name for the folder UE4SS keys on in mods.txt. Put Scripts\\ inside a folder named " +
                "after the mod and install that folder instead.");
            return false;
        }

        var folderName = Path.GetFileName(modRoot.TrimEnd(Path.DirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(folderName)) folderName = mod.Name;

        var destDir = Path.Combine(_game.UE4SSModsPath, folderName);
        CopyDirectoryRecursive(modRoot, destDir);

        mod.InstallPath = destDir;
        mod.InstallFiles = new List<string> { destDir };
        _lua.SetEnabled(_game, folderName, true);
        return true;
    }

    private void CopyDirectoryRecursive(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(dest, Path.GetRelativePath(source, dir)));
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(dest, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }
    }

    /// Installs a native DLL plugin into whichever loader on this install can load one.
    ///
    /// The destination is NOT fixed: UnrealModUnlocker reads Binaries\Win64\UnrealModPlugins,
    /// UnrealModLoader reads coremods, and there is no shared convention between them. So it is
    /// resolved from what is actually installed, and refused outright when nothing present can load
    /// a DLL - dropping a native DLL somewhere the game never reads is indistinguishable, from the
    /// user's side, from the mod being broken.
    ///
    /// Only the DLLs are placed. A framework's data folder has its own layout, described by its own
    /// documentation - guessing where "Custom Example Pack (CEP)/CustomDrugs" belongs would as
    /// likely put it one level off as get it right, so the remaining contents are reported instead.
    private bool InstallDllPlugin(string sourceRoot, ModInfo mod)
    {
        var log = LoggingService.Instance;

        var loader = new ModLoaderService().DetectAll(_game)
            .FirstOrDefault(l => l.IsInstalled && l.PluginFolder != null);

        if (loader?.PluginFolder == null)
        {
            log.Error(
                $"'{mod.Name}' is a DLL plugin, and nothing installed here can load one. Install a loader that " +
                "takes DLL plugins first (UnrealModUnlocker reads Binaries\\Win64\\UnrealModPlugins; " +
                "UnrealModLoader reads coremods), launch the game once so it creates that folder, then install " +
                "this again.");
            return false;
        }

        var dlls = Directory.GetFiles(sourceRoot, "*.dll", SearchOption.AllDirectories);

        // These folders are normally created BY the loader, on the first launch after it patches the
        // game. Its absence usually means that launch has not happened yet - so create it and say so,
        // rather than installing into a folder nothing is watching and calling it done.
        var loaderMadeIt = Directory.Exists(loader.PluginFolder);
        Directory.CreateDirectory(loader.PluginFolder);

        // A loader's plugin folder is flat and keyed by filename, so a second copy of the same
        // framework - or a hand-installed one - is overwritten with no trace. Note it before copying,
        // because afterwards there is nothing left to tell the user about.
        var overwritten = new List<string>();

        var placed = new List<string>();
        foreach (var src in dlls)
        {
            var dest = Path.Combine(loader.PluginFolder, Path.GetFileName(src));
            if (File.Exists(dest)) overwritten.Add(Path.GetFileName(src));
            File.Copy(src, dest, true);
            placed.Add(dest);
        }

        mod.InstallPath = loader.PluginFolder;
        mod.InstallFiles = placed;

        log.Success(
            $"Installed {placed.Count} DLL(s) into {loader.PluginFolder} for {loader.DisplayName}.");

        if (overwritten.Count > 0)
        {
            log.Warn(
                $"Replaced an existing {string.Join(", ", overwritten)} in {loader.PluginFolder}. If that was " +
                "another copy of this framework - installed by hand or by another mod - it is gone now, and " +
                "removing this mod will not bring it back.");
        }

        if (!loaderMadeIt)
        {
            log.Warn(
                $"{loader.DisplayName} had not created its plugin folder yet, so this manager made it. That folder " +
                "normally appears the first time the game runs after the loader patches it - if the mod does not " +
                "load, launch the game once and check the loader is actually installed.");
        }

        // Anything else in the archive is the framework's own content, whose layout only its docs
        // describe. Name it rather than place it.
        var extras = Directory.GetFiles(sourceRoot, "*", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Select(f => Path.GetRelativePath(sourceRoot, f).Split(Path.DirectorySeparatorChar)[0])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (extras.Count > 0)
        {
            log.Warn(
                $"The archive also contains {string.Join(", ", extras.Take(4))}" +
                (extras.Count > 4 ? ", ..." : "") +
                ". Those are the mod's own content and this manager hasn't placed them - most frameworks " +
                "read them from a folder they create next to their DLL on first launch. Check the mod's " +
                "instructions for where they go.");
        }

        return true;
    }

    /// Copies a loose-asset mod into the game's Content folder, preserving its directory tree.
    ///
    /// The tree IS the mod: a loose asset only overrides the packed original when it sits at the
    /// exact same relative path, so flattening or re-rooting the files produces an install where
    /// nothing loads and nothing explains why.
    private void InstallLooseAssets(string sourceRoot, ModInfo mod)
    {
        var looseRoot = ModAnalyzerService.FindLooseAssetRoot(sourceRoot) ?? sourceRoot;
        var copied = new List<string>();

        foreach (var src in Directory.GetFiles(looseRoot, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(_game.ContentPath, Path.GetRelativePath(looseRoot, src));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(src, target, true);
            copied.Add(target);
        }

        mod.InstallPath = _game.ContentPath;
        mod.InstallFiles = copied;

        WarnAboutMissingCompanions(copied);
    }

    /// A cooked asset is more than one file: .uasset carries the header, .uexp the exports, .ubulk
    /// the bulk data. Shipping one without the others crashes the game or loses the asset silently,
    /// and it is a packaging mistake worth naming at install time rather than leaving the user to
    /// discover as a crash later.
    private static void WarnAboutMissingCompanions(IEnumerable<string> files)
    {
        var groups = files.GroupBy(
            f => Path.Combine(Path.GetDirectoryName(f) ?? "", Path.GetFileNameWithoutExtension(f)),
            StringComparer.OrdinalIgnoreCase);

        // Only when the mod ships a MIX of shapes.
        //
        // A .uasset with no .uexp is normal and correct for plenty of cooked assets - measured across
        // real DDS1 mods, the single-file ModActor.uasset shape is the most common logic mod there is.
        // Warning per file meant every one of those installs produced a wall of warnings about a
        // problem that did not exist, which is how a real warning stops being read.
        var lone = groups.Where(g =>
        {
            var ext = g.Select(f => Path.GetExtension(f).ToLowerInvariant()).ToHashSet();
            return ext.Contains(".uasset") && !ext.Contains(".uexp");
        }).ToList();

        var paired = groups.Any(g =>
            g.Any(f => f.EndsWith(".uexp", StringComparison.OrdinalIgnoreCase)));

        if (paired && lone.Count > 0)
        {
            LoggingService.Instance.Warn(
                $"{lone.Count} asset(s) in this mod have no .uexp beside them while others do " +
                $"({string.Join(", ", lone.Take(3).Select(g => Path.GetFileName(g.Key) + ".uasset"))}" +
                (lone.Count > 3 ? ", ..." : "") + "). Cooked assets usually ship as a set - worth checking " +
                "the archive is complete.");
        }
    }

    /// Removes folders left empty after a loose-asset uninstall, stopping at (and never deleting)
    /// the game's own Content folder.
    private static void PruneEmptyFolders(IEnumerable<string> removedFiles, string stopAt)
    {
        var stop = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stopAt));

        foreach (var dir in removedFiles.Select(Path.GetDirectoryName)
                     .Where(d => !string.IsNullOrEmpty(d))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderByDescending(d => d!.Length))
        {
            var current = dir!;
            while (true)
            {
                var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(current));

                // Never the Content folder itself, and never anything outside it.
                if (full.Equals(stop, StringComparison.OrdinalIgnoreCase)) break;
                if (!full.StartsWith(stop + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) break;
                if (!Directory.Exists(full)) break;
                if (Directory.EnumerateFileSystemEntries(full).Any()) break;

                try { Directory.Delete(full); } catch { break; }
                current = Path.GetDirectoryName(full) ?? "";
                if (string.IsNullOrEmpty(current)) break;
            }
        }
    }

    /// A file's path relative to wherever the mod's files currently live, so moving them preserves
    /// the tree instead of flattening it.
    ///
    /// For a pak mod every file sits directly in InstallPath, so this returns the bare filename and
    /// behaviour is byte-identical to what it always was. It matters for loose assets, whose files
    /// span Content\&lt;Category&gt;\ subfolders where the same filename appearing under two categories
    /// is completely normal - flattening those into one folder would have one silently overwrite the
    /// other, destroying a file with no way to get it back.
    private static string RelativeToInstallRoot(ModInfo mod, string file)
    {
        try
        {
            if (!string.IsNullOrEmpty(mod.InstallPath))
            {
                var rel = Path.GetRelativePath(mod.InstallPath, file);
                if (!rel.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(rel)) return rel;
            }
        }
        catch { /* unrelated roots, different drives - fall through */ }

        return Path.GetFileName(file);
    }

    public void Uninstall(ModInfo mod)
    {
        var log = LoggingService.Instance;
        try
        {
            // Files another row still claims are NOT ours to delete, whatever this row recorded.
            //
            // Two rows can genuinely list the same file: install a mod, then install it again from a
            // differently-named archive, and the second row overwrites the first's file in place -
            // most visibly for a DLL plugin, whose destination folder is flat and keyed by filename.
            // Removing either row then deleted a file the other one is still relying on, leaving a
            // mod listed as installed with nothing behind it.
            //
            // Same test ImportUnmanaged already uses, for the same reason: files are unambiguous
            // where names are not.
            var claimedByOthers = _registry.Mods
                .Where(m => m.Id != mod.Id)
                .SelectMany(m => m.InstallFiles)
                .Select(p => p.TrimEnd(Path.DirectorySeparatorChar))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var shared = new List<string>();

            bool Ours(string f)
            {
                if (!claimedByOthers.Contains(f.TrimEnd(Path.DirectorySeparatorChar))) return true;
                shared.Add(Path.GetFileName(f));
                return false;
            }

            if (mod.Type is ModType.LogicMod or ModType.PatchMod or ModType.DllPlugin)
            {
                // Only the DLLs this manager placed. A framework's data folder holds the user's own
                // settings and content and was never installed from the archive, so it is left alone.
                foreach (var f in mod.InstallFiles.Where(File.Exists).Where(Ours))
                    File.Delete(f);
            }
            else if (mod.Type == ModType.LooseAsset)
            {
                // Only the files this mod recorded at install time. Ownership of a loose asset
                // cannot be inferred from where it sits - an overriding .uasset at a vanilla path is
                // indistinguishable from a vanilla one - so the install manifest is the only thing
                // that makes removing it safe, and anything not on that list is left alone.
                foreach (var f in mod.InstallFiles.Where(File.Exists).Where(Ours))
                    File.Delete(f);

                PruneEmptyFolders(mod.InstallFiles, stopAt: _game.ContentPath);
            }
            else if (mod.Type == ModType.LuaMod)
            {
                if (Directory.Exists(mod.InstallPath)) Directory.Delete(mod.InstallPath, true);
                _lua.RemoveEntry(_game, LuaFolderName(mod));
            }

            var cachePath = Path.Combine(_disabledCacheDir, mod.Id);
            if (Directory.Exists(cachePath)) Directory.Delete(cachePath, true);

            if (shared.Count > 0)
            {
                log.Warn(
                    $"Left {string.Join(", ", shared.Take(4))}" + (shared.Count > 4 ? ", ..." : "") +
                    " in place - another installed mod lists the same file(s). Uninstall that one too if you " +
                    "meant to remove them.");
            }

            _registry.Remove(mod.Id);
            log.Success($"Uninstalled '{mod.Name}'.");
        }
        catch (Exception ex)
        {
            log.Error($"Failed to uninstall '{mod.Name}': {ex.Message}");
        }
    }

    public void Disable(ModInfo mod)
    {
        var log = LoggingService.Instance;

        if (mod.Type == ModType.LuaMod)
        {
            _lua.SetEnabled(_game, LuaFolderName(mod), false);
            // Guarded exactly like the pak path below. mods.txt is an ordinary file that can be
            // read-only or held open, and without this an unwritable one throws straight out
            // through the command that called us and takes the app down. Worse for a two-part mod:
            // the caller toggles each half in turn, so a throw here would leave the pak half
            // disabled and the lua half enabled, with no undo recorded - the exact half-enabled
            // state the rest of this class exists to prevent.
            try
            {
                _lua.SetEnabled(_game, LuaFolderName(mod), false);
                mod.IsEnabled = false;
                _registry.Upsert(mod);
                log.Info($"Disabled '{mod.Name}' (mods.txt set to 0 - files left in place).");
            }
            catch (Exception ex)
            {
                log.Error($"Failed to disable '{mod.Name}': {ex.Message}");
            }
            return;
        }

        try
        {
            var cacheDir = Path.Combine(_disabledCacheDir, mod.Id);
            Directory.CreateDirectory(cacheDir);

            var newFiles = new List<string>();
            foreach (var f in mod.InstallFiles.Where(File.Exists))
            {
                var dest = Path.Combine(cacheDir, RelativeToInstallRoot(mod, f));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Move(f, dest, true);
                newFiles.Add(dest);
            }

            // Refuse to record an empty file list for a mod that had files. Nothing moved means the
            // recorded paths are all gone (deleted by hand, or a game verify), and writing the empty
            // result back would destroy the only record of what this mod owns - permanently, and
            // while reporting success. Leaving the stale list alone keeps the mod recoverable.
            if (newFiles.Count == 0)
            {
                log.Error(
                    $"Couldn't disable '{mod.Name}': none of its {mod.InstallFiles.Count} recorded file(s) are on " +
                    "disk. Leaving it tracked as-is rather than forgetting what it owns - use Re-scan Mod Files if " +
                    "the mod was changed outside the manager.");
                return;
            }

            mod.InstallFiles = newFiles;
            mod.InstallPath = cacheDir;
            mod.IsEnabled = false;
            _registry.Upsert(mod);
            log.Info($"Disabled '{mod.Name}' - files moved out of the game folder so UE4 can't load them.");
        }
        catch (Exception ex)
        {
            log.Error($"Failed to disable '{mod.Name}': {ex.Message}");
        }
    }

    public void Enable(ModInfo mod)
    {
        var log = LoggingService.Instance;

        if (mod.Type == ModType.LuaMod)
        {
            // Guarded for the same reason as Disable: an unwritable mods.txt must be reported, not
            // thrown out through the caller mid-way through a two-part toggle.
            try
            {
                _lua.SetEnabled(_game, LuaFolderName(mod), true);
                mod.IsEnabled = true;
                _registry.Upsert(mod);
                log.Success($"Enabled '{mod.Name}'.");
            }
            catch (Exception ex)
            {
                log.Error($"Failed to enable '{mod.Name}': {ex.Message}");
            }
            return;
        }

        try
        {
            // Logic mods go back into their OWN subfolder of LogicMods, which is the layout
            // UE4SS's BPModLoaderMod expects and what every deploy script produces. Restoring
            // them flat into LogicMods worked by accident at best, and made a tidy install
            // messier every time somebody toggled a mod off and on.
            // Named after the PAK, not after mod.Name - the display name can carry a
            // disambiguating suffix like "DriveableScooter (LogicMod)", and the folder should
            // match what the container is actually called.
            var pakBaseName = mod.InstallFiles
                .Select(Path.GetFileNameWithoutExtension)
                .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? mod.Name;

            var destFolder = mod.Type switch
            {
                // Must match the install rule exactly. This reconstructs the destination
                // independently, so gating only the install would leave a working flat DDS1 mod
                // re-nested - and re-broken - by the next disable/enable cycle.
                ModType.LogicMod => _game.Profile.LogicModsUseSubfolders
                    ? Path.Combine(_game.LogicModsPath, pakBaseName)
                    : _game.LogicModsPath,
                // Loose assets go back where they override from, at their original relative paths.
                ModType.LooseAsset => _game.ContentPath,
                _ => _game.PaksPath
            };
            Directory.CreateDirectory(destFolder);

            var newFiles = new List<string>();
            foreach (var f in mod.InstallFiles.Where(File.Exists))
            {
                var dest = Path.Combine(destFolder, RelativeToInstallRoot(mod, f));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Move(f, dest, true);
                newFiles.Add(dest);
            }

            // Same guard as Disable, and the more dangerous direction: the files being restored live
            // in this app's own DisabledMods cache, so an empty result means the cache was cleared
            // or moved. Writing it back would mark the mod enabled with no files at all - it would
            // vanish from the game and from its own record, while the log said "Enabled".
            if (newFiles.Count == 0)
            {
                log.Error(
                    $"Couldn't enable '{mod.Name}': none of its {mod.InstallFiles.Count} disabled file(s) are where " +
                    "they were parked. Leaving it tracked as disabled rather than forgetting what it owns - the " +
                    "files may still be recoverable from the DisabledMods folder (Settings > Open Disabled Mods).");
                return;
            }

            mod.InstallFiles = newFiles;
            mod.InstallPath = destFolder;
            mod.IsEnabled = true;
            _registry.Upsert(mod);
            log.Success($"Enabled '{mod.Name}'.");
        }
        catch (Exception ex)
        {
            log.Error($"Failed to enable '{mod.Name}': {ex.Message}");
        }
    }
}
