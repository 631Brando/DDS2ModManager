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
    private readonly string _disabledCacheDir;

    public ModInstallerService(GameInstallation game, ModAnalyzerService analyzer, ModRegistryService registry)
    {
        _game = game;
        _analyzer = analyzer;
        _registry = registry;
        _disabledCacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DDS2ModManager", "DisabledMods");
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

            var name = InferModName(chosenRoot, analysis.Type);

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
                    InstallLuaMod(chosenRoot, mod);
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

            _registry.Upsert(mod);
            log.Success($"Imported existing mod '{mod.Name}' as {mod.Type}.");
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

    private string InferModName(string workingDir, ModType type)
    {
        if (type == ModType.LuaMod)
        {
            var scriptsDir = Directory.GetDirectories(workingDir, "Scripts", SearchOption.AllDirectories).FirstOrDefault();
            if (scriptsDir != null) return Path.GetFileName(Path.GetDirectoryName(scriptsDir)!);
        }

        var pak = Directory.GetFiles(workingDir, "*.pak", SearchOption.AllDirectories).FirstOrDefault();
        if (pak != null) return Path.GetFileNameWithoutExtension(pak);

        return Path.GetFileName(workingDir.TrimEnd(Path.DirectorySeparatorChar));
    }

    private void InstallPakTriple(string workingDir, string destFolder, ModInfo mod)
    {
        // Logic mods go in their OWN subfolder, named after the pak. That is the layout UE4SS's
        // BPModLoaderMod expects, what every deploy script produces, and what Enable() restores
        // to - installing flat into LogicMods put a fresh install in a different shape from the
        // same mod after one disable/enable cycle.
        if (mod.Type == ModType.LogicMod)
        {
            var pakName = Directory.GetFiles(workingDir, "*.pak", SearchOption.AllDirectories)
                .Select(Path.GetFileNameWithoutExtension)
                .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));

            if (!string.IsNullOrWhiteSpace(pakName)) destFolder = Path.Combine(destFolder, pakName);
        }

        Directory.CreateDirectory(destFolder);
        var moved = new List<string>();

        foreach (var ext in new[] { ".pak", ".ucas", ".utoc" })
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

    private void InstallLuaMod(string workingDir, ModInfo mod)
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
        var folderName = Path.GetFileName(modRoot.TrimEnd(Path.DirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(folderName)) folderName = mod.Name;

        var destDir = Path.Combine(_game.UE4SSModsPath, folderName);
        CopyDirectoryRecursive(modRoot, destDir);

        mod.InstallPath = destDir;
        mod.InstallFiles = new List<string> { destDir };
        _lua.SetEnabled(_game, folderName, true);
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

    public void Uninstall(ModInfo mod)
    {
        var log = LoggingService.Instance;
        try
        {
            if (mod.Type is ModType.LogicMod or ModType.PatchMod)
            {
                foreach (var f in mod.InstallFiles.Where(File.Exists))
                    File.Delete(f);
            }
            else if (mod.Type == ModType.LuaMod)
            {
                if (Directory.Exists(mod.InstallPath)) Directory.Delete(mod.InstallPath, true);
                _lua.RemoveEntry(_game, LuaFolderName(mod));
            }

            var cachePath = Path.Combine(_disabledCacheDir, mod.Id);
            if (Directory.Exists(cachePath)) Directory.Delete(cachePath, true);

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
            mod.IsEnabled = false;
            _registry.Upsert(mod);
            log.Info($"Disabled '{mod.Name}' (mods.txt set to 0 - files left in place).");
            return;
        }

        try
        {
            var cacheDir = Path.Combine(_disabledCacheDir, mod.Id);
            Directory.CreateDirectory(cacheDir);

            var newFiles = new List<string>();
            foreach (var f in mod.InstallFiles.Where(File.Exists))
            {
                var dest = Path.Combine(cacheDir, Path.GetFileName(f));
                File.Move(f, dest, true);
                newFiles.Add(dest);
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
            _lua.SetEnabled(_game, LuaFolderName(mod), true);
            mod.IsEnabled = true;
            _registry.Upsert(mod);
            log.Success($"Enabled '{mod.Name}'.");
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

            var destFolder = mod.Type == ModType.LogicMod
                ? Path.Combine(_game.LogicModsPath, pakBaseName)
                : _game.PaksPath;
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
