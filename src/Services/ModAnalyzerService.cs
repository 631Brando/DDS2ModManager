using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.MappingsProvider;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.Versions;

namespace DDS2ModManager.Services;

public class ModAnalysisResult
{
    public ModType Type { get; set; }
    public bool HasModActor { get; set; }
    public List<string> AssetPaths { get; set; } = new();
    public List<string> Warnings { get; set; } = new();

    /// True when CUE4Parse genuinely failed to read this mod's pak - installation should be
    /// blocked rather than guessing a type, since a wrong guess (PatchMod instead of LogicMod)
    /// means the mod gets copied to the wrong folder and silently won't load in-game.
    public bool ParseFailed { get; set; }
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
    /// the named archives (by file name) - NOT a diff against some other mount. A diff-based
    /// approach breaks the moment a path is contributed by more than one source, which is exactly
    /// what happens for a legitimate override mod, a mod being re-analyzed while its own previous
    /// copy is still installed, or two mods that genuinely conflict: the "new" set comes back
    /// empty even though everything mounted and read correctly. Each archive reader keeps its own
    /// Files dictionary independent of anything else mounted, so reading directly from the
    /// specific reader(s) we staged is both simpler and correct in all of those cases.
    private HashSet<string> MountAndReadArchives(IReadOnlyCollection<string> archiveNames, out DefaultFileProvider provider)
    {
        // NOTE: CUE4Parse marked this 4-arg constructor obsolete in favor of one taking an explicit
        // StringComparer, but the replacement's exact parameter order varies between library versions.
        // This overload still works correctly, so we suppress the deprecation warning rather than risk
        // a signature mismatch. If you upgrade CUE4Parse and want to silence it "properly", switch to
        // the StringComparer overload your version exposes.
#pragma warning disable CS0618
        provider = new DefaultFileProvider(_game.PaksPath, SearchOption.AllDirectories, true, new VersionContainer(_egame));
#pragma warning restore CS0618
        try { provider.MappingsContainer = new FileUsmapTypeMappingsProvider(_mappingsPath); }
        catch (Exception mex)
        {
            LoggingService.Instance.Warn($"Mappings file couldn't be loaded ({mex.Message}) - continuing without it. " +
                "This only affects deep property parsing, not mod type detection or conflict checking.");
        }

        provider.Initialize();

        if (!string.IsNullOrWhiteSpace(_aesKeyHex))
        {
            // Mounts only the archives whose EncryptionKeyGuid matches this guid - irrelevant to
            // DDS2, which has no AES encryption at all, but harmless to keep for games that do.
            try { provider.SubmitKey(new CUE4Parse.UE4.Objects.Core.Misc.FGuid(), new FAesKey(_aesKeyHex)); }
            catch (Exception ex) { LoggingService.Instance.Warn($"Failed to submit AES key: {ex.Message}"); }
        }

        // Initialize() only scans the directory and registers each .pak/.utoc into UnloadedVfs - it
        // never mounts anything into Files, and neither does PostMount() (that one only reconciles a
        // DefaultGame.EncryptionKeyGuid ini edge case, unrelated to normal mounting). The call that
        // actually mounts unencrypted archives into Files is Mount()/MountAsync() - SubmitKey above
        // only covers archives that need a specific AES key, which DDS2 has none of, so without this
        // call every mount produced zero files regardless of Oodle/EGame/AES being correct.
        provider.Mount();

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in archiveNames)
        {
            if (provider.TryGetArchive(name, out var archive))
                foreach (var p in archive.Files.Keys) paths.Add(p);
        }
        return paths;
    }
}
