using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Versions;

namespace DDS2ModManager.Services;

public class ModAnalysisResult
{
    public ModType Type { get; set; }
    public bool HasModActor { get; set; }
    public List<string> AssetPaths { get; set; } = new();
    public List<string> Warnings { get; set; } = new();

    /// For LogicMods: the base-game DataTables this mod merges into at runtime, captured during
    /// the same mount that reads its asset paths. Populated here rather than only during Deep Scan
    /// so row-level conflict checking works from the moment a mod is installed.
    public List<DataTableAppend> DataTableAppends { get; set; } = new();

    /// True when CUE4Parse genuinely failed to read this mod's pak - installation should be
    /// blocked rather than guessing a type, since a wrong guess (PatchMod instead of LogicMod)
    /// means the mod gets copied to the wrong folder and silently won't load in-game.
    public bool ParseFailed { get; set; }

    /// Where this mod says its updates come from, read during the same mount that produced
    /// AssetPaths. Null when it declares nothing, which is the normal case for mods published
    /// before the ModUpdateUrl convention existed.
    public ModUpdateSource? UpdateSource { get; set; }
}

/// Uses CUE4Parse to read a mod's .pak/.ucas/.utoc and enumerate the asset paths inside it,
/// which tells us whether it has a ModActor.uasset (-> LogicMod) and which game files it
/// touches (-> conflict detection).
///
/// CRITICAL: modern UE5 mods are IoStore containers (.utoc/.ucas). An IoStore patch container
/// does NOT carry its own global name/ID table - it references the game's global.utoc that
/// lives in Content\Paks. If you mount a mod's .utoc in an isolated folder with no global.utoc
/// present, CUE4Parse mounts it but resolves ZERO files (the "found 0 files" failure). So we
/// must analyze the mod IN THE CONTEXT OF THE REAL GAME: we mount the game's Content\Paks
/// directory (which loads global.utoc + all base containers), with the mod's own files present
/// in that same directory, then read directly from the mod's own archive reader(s) (not a diff
/// against the rest of the mount - a path the mod legitimately overrides, or that a previous
/// install/another mod also touches, would otherwise vanish from the "new" set even though the
/// mod mounted and read just fine).
public class ModAnalyzerService
{
    private readonly GameInstallation _game;
    private readonly string _mappingsPath;
    private readonly EGame _egame;
    private readonly string? _aesKeyHex;
    private readonly ModUpdateSourceResolver _updateSources = new();

    public ModAnalyzerService(GameInstallation game, string mappingsPath, EGame egame = EGame.GAME_UE5_3, string? aesKeyHex = null)
    {
        _game = game;
        _mappingsPath = mappingsPath;
        _egame = egame;
        _aesKeyHex = aesKeyHex;
    }

    public ModAnalysisResult Analyze(string modFolderPath)
    {
        var result = new ModAnalysisResult();
        var log = LoggingService.Instance;

        // Lua mods have no pak - detect and list from disk, no CUE4Parse needed.
        var mainLua = Directory.GetFiles(modFolderPath, "main.lua", SearchOption.AllDirectories)
            .FirstOrDefault(f => string.Equals(
                Path.GetFileName(Path.GetDirectoryName(f) ?? ""), "Scripts", StringComparison.OrdinalIgnoreCase));

        var modPaks = Directory.GetFiles(modFolderPath, "*.pak", SearchOption.AllDirectories).ToList();

        if (mainLua != null && modPaks.Count == 0)
        {
            result.Type = ModType.LuaMod;
            var modRoot = Path.GetDirectoryName(Path.GetDirectoryName(mainLua)!)!;
            foreach (var f in Directory.GetFiles(modRoot, "*", SearchOption.AllDirectories))
                result.AssetPaths.Add(Path.GetRelativePath(modRoot, f).Replace('\\', '/'));

            // Lua mods have no ModActor, so a manifest is the only place they can declare
            // an update source. They ship folders anyway, so the extra file costs nothing.
            result.UpdateSource = _updateSources.FromManifestFolder(modFolderPath, Path.GetFileName(modFolderPath));
            return result;
        }

        if (modPaks.Count == 0)
        {
            result.Type = ModType.Unknown;
            result.Warnings.Add("No .pak and no Scripts\\main.lua found - couldn't determine mod type.");
            return result;
        }

        // Gather the mod's full container set (.pak + .ucas + .utoc).
        var modFiles = new[] { ".pak", ".ucas", ".utoc" }
            .SelectMany(ext => Directory.GetFiles(modFolderPath, "*" + ext, SearchOption.AllDirectories))
            .ToList();

        var stagedInGame = new List<string>();
        var stagedArchiveNames = new List<string>();
        DefaultFileProvider? provider = null;

        try
        {
            if (!Directory.Exists(_game.PaksPath))
                throw new DirectoryNotFoundException(
                    $"Game Paks folder not found at {_game.PaksPath}. Can't analyze IoStore mods without the game's global.utoc.");

            // Stage the mod's files into the real Content\Paks so its IoStore container(s) mount
            // alongside global.utoc. Prefixed so they're unmistakable, and always removed below.
            foreach (var f in modFiles)
            {
                var staged = Path.Combine(_game.PaksPath, "__DDS2MM_analyze__" + Path.GetFileName(f));
                File.Copy(f, staged, true);
                stagedInGame.Add(staged);

                // Only .pak/.utoc get registered as their own archive reader - .ucas is just the
                // companion data stream the .utoc reader opens, never a reader on its own.
                var ext = Path.GetExtension(staged);
                if (ext.Equals(".pak", StringComparison.OrdinalIgnoreCase) || ext.Equals(".utoc", StringComparison.OrdinalIgnoreCase))
                    stagedArchiveNames.Add(Path.GetFileName(staged));
            }

            var modPaths = MountAndReadArchives(stagedArchiveNames, out provider);

            foreach (var path in modPaths)
            {
                if (Path.GetFileNameWithoutExtension(path).Equals("ModActor", StringComparison.OrdinalIgnoreCase))
                    result.HasModActor = true;
            }

            if (modPaths.Count == 0)
            {
                throw new InvalidOperationException(
                    "Mounted the mod's container(s) against the game but couldn't read any files from them. This " +
                    "usually means the Unreal Engine version (Settings > EGame) doesn't match (DDS2 is GAME_UE5_3), " +
                    "Oodle isn't available (see startup log), or the container format isn't supported.");
            }

            result.AssetPaths = modPaths.ToList();
            result.Type = result.HasModActor ? ModType.LogicMod : ModType.PatchMod;
            log.Info($"CUE4Parse read {modPaths.Count} asset path(s) from '{Path.GetFileName(modFolderPath)}'" +
                     (result.HasModActor ? " (ModActor.uasset found -> LogicMod)." : " (no ModActor -> PatchMod)."));

            // Read the mod's DataTable merges while the game is still mounted. Doing this here
            // rather than only in Deep Scan is what makes row-level conflict checking work
            // immediately after install - otherwise a newly added LogicMod has no row data and the
            // compatibility panel reports "no conflicts" because it has nothing to compare, which
            // is indistinguishable from genuinely not conflicting.
            if (result.HasModActor)
            {
                result.DataTableAppends = new DataTableAppendScanner()
                    .Scan(provider, Path.GetFileName(modFolderPath), result.AssetPaths);
                if (result.DataTableAppends.Count > 0)
                    log.Info($"Reads/merges {result.DataTableAppends.Count} game DataTable(s) at runtime.");

                // Same mount, same loaded package - reading the update URL here costs nothing
                // beyond the property lookup.
                result.UpdateSource = _updateSources.FromModActor(
                    provider, result.AssetPaths, Path.GetFileName(modFolderPath));
            }

            // Patch mods have no ModActor by definition, and a LogicMod author may prefer the
            // manifest, so fall back to it whenever the ModActor did not supply one.
            result.UpdateSource ??= _updateSources.FromManifestFolder(modFolderPath, Path.GetFileName(modFolderPath));

            if (result.UpdateSource is { IsUsable: true } declared)
                log.Info($"Declares updates at {declared.DeclaredUrl} (from {declared.Declaration}).");
        }
        catch (Exception ex)
        {
            // Do NOT guess a type. A mod's ModActor.uasset lives compressed inside the pak, never as
            // a loose file on disk, so there's no reliable filesystem fallback once CUE4Parse fails
            // to read the container - guessing PatchMod is exactly how a LogicMod ends up in the wrong
            // folder and silently never loads.
            log.Error($"CUE4Parse failed to read the mod against the game: {ex.Message}");
            result.ParseFailed = true;
            result.Type = ModType.Unknown;
            result.Warnings.Add(
                "Couldn't verify this mod's contents, so installation was blocked rather than guessing its type. " +
                "Check that Oodle is available (see startup log), the Unreal Engine version is correct " +
                "(Settings > EGame, DDS2 = GAME_UE5_3), and - if the mod is encrypted - an AES key is set.");
        }
        finally
        {
            (provider as IDisposable)?.Dispose();

            // Always remove the temporarily-staged mod files from the game folder.
            foreach (var staged in stagedInGame)
            {
                try { if (File.Exists(staged)) File.Delete(staged); }
                catch (Exception ex) { LoggingService.Instance.Warn($"Couldn't remove temporary analysis file '{staged}': {ex.Message}. You can delete it manually."); }
            }
        }

        return result;
    }

    /// Mounts the game Paks folder and returns the union of asset paths contributed by exactly
    /// the named archives. See GameMountService for why this reads each archive directly rather
    /// than diffing against the rest of the mount.
    private HashSet<string> MountAndReadArchives(IReadOnlyCollection<string> archiveNames, out DefaultFileProvider provider)
    {
        provider = GameMountService.Mount(_game.PaksPath, _mappingsPath, _egame, _aesKeyHex, warnOnMappingsFailure: true);
        // Needed for the ModActor DataTable-append scan below; without it Blueprint bytecode is
        // skipped during deserialization and no appends are ever found.
        DataTableAppendScanner.EnableScriptReading(provider);
        return GameMountService.ReadArchivePaths(provider, archiveNames);
    }
}
