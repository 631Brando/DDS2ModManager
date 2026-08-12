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

        if (Directory.Exists(sourcePath))
        {
            return new PreparedInstall
            {
                ExtractedRoot = sourcePath,
                IsTempExtraction = false,
                VariantCandidates = ModVariantDetectionService.DetectCandidates(sourcePath)
            };
        }

        if (File.Exists(sourcePath) && ArchiveExtractionService.IsSupportedArchive(sourcePath))
        {
            var tempExtract = Path.Combine(Path.GetTempPath(), "DDS2MM_Install_" + Guid.NewGuid().ToString("N"));
            log.Info($"Extracting '{Path.GetFileName(sourcePath)}'...");
            ArchiveExtractionService.ExtractToDirectory(sourcePath, tempExtract);

            return new PreparedInstall
            {
                ExtractedRoot = tempExtract,
                IsTempExtraction = true,
                VariantCandidates = ModVariantDetectionService.DetectCandidates(tempExtract)
            };
        }

        throw new InvalidOperationException(
            $"Unsupported mod source (expected a folder, or a {string.Join("/", ArchiveExtractionService.SupportedExtensions)} archive): {sourcePath}");
    }

    /// Step 2: analyze + copy files into the game. chosenRoot is either prepared.ExtractedRoot
    /// itself (no variants) or the single folder the user picked from prepared.VariantCandidates.
    public async Task<ModInfo?> InstallFromRootAsync(string originalSourcePath, PreparedInstall prepared, string chosenRoot)
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

            if (_registry.Mods.Any(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                log.Warn($"A mod named '{name}' is already installed. Uninstall it first if you mean to replace it.");
                return null;
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

                // Pinned here, at install time, from the copy the user actually downloaded -
                // which for a Nexus download is a copy Nexus scanned. ModUpdateService compares
                // against this later, so an update that starts pointing somewhere new is
                // visible rather than silently followed.
                ModUpdateUrl = analysis.UpdateDeclaration.UpdateUrl,
                UpdateSource = analysis.UpdateDeclaration.Source,
                InstalledVersion = analysis.UpdateDeclaration.Version ?? ""
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
            if (prepared.IsTempExtraction)
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
            if (_registry.Mods.Any(m => m.Name.Equals(found.Name, StringComparison.OrdinalIgnoreCase)))
            {
                log.Warn($"Skipped importing '{found.Name}' - a different mod with that name is already tracked.");
                return null;
            }

            var mod = new ModInfo
            {
                Name = found.Name,
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
                ModUpdateUrl = found.UpdateDeclaration.UpdateUrl,
                UpdateSource = found.UpdateDeclaration.Source,
                InstalledVersion = found.UpdateDeclaration.Version ?? ""
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

    private void InstallLuaMod(string workingDir, ModInfo mod)
    {
        Directory.CreateDirectory(_game.UE4SSModsPath);
        var scriptsDir = Directory.GetDirectories(workingDir, "Scripts", SearchOption.AllDirectories).FirstOrDefault();
        var modRoot = scriptsDir != null ? Path.GetDirectoryName(scriptsDir)! : workingDir;

        var destDir = Path.Combine(_game.UE4SSModsPath, mod.Name);
        CopyDirectoryRecursive(modRoot, destDir);

        mod.InstallPath = destDir;
        mod.InstallFiles = new List<string> { destDir };
        _lua.SetEnabled(_game, mod.Name, true);
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
                _lua.RemoveEntry(_game, mod.Name);
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
            _lua.SetEnabled(_game, mod.Name, false);
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
            _lua.SetEnabled(_game, mod.Name, true);
            mod.IsEnabled = true;
            _registry.Upsert(mod);
            log.Success($"Enabled '{mod.Name}'.");
            return;
        }

        try
        {
            var destFolder = mod.Type == ModType.LogicMod ? _game.LogicModsPath : _game.PaksPath;
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
