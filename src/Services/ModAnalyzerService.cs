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
/// in that same directory, then read back just the entries the mod contributes.
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
        DefaultFileProvider? baseProvider = null;
        DefaultFileProvider? modProvider = null;

        try
        {
            if (!Directory.Exists(_game.PaksPath))
                throw new DirectoryNotFoundException(
                    $"Game Paks folder not found at {_game.PaksPath}. Can't analyze IoStore mods without the game's global.utoc.");

            // Pass 1: mount the game as-is to get the baseline set of paths (before the mod).
            var baseline = MountAndListPaths(out baseProvider);

            // Stage the mod's files into the real Content\Paks so they mount alongside global.utoc.
            // Prefixed so they're unmistakable, and always removed in the finally block.
            foreach (var f in modFiles)
            {
                var staged = Path.Combine(_game.PaksPath, "__DDS2MM_analyze__" + Path.GetFileName(f));
                File.Copy(f, staged, true);
                stagedInGame.Add(staged);
            }

            // Pass 2: mount again with the mod present, and diff against the baseline. Whatever's
            // new is what the mod contributes - no dependency on VFS-reader internals.
            var withMod = MountAndListPaths(out modProvider);

            var modPaths = withMod.Except(baseline, StringComparer.OrdinalIgnoreCase).ToList();

            foreach (var path in modPaths)
            {
                if (Path.GetFileNameWithoutExtension(path).Equals("ModActor", StringComparison.OrdinalIgnoreCase))
                    result.HasModActor = true;
            }

            if (modPaths.Count == 0)
            {
                throw new InvalidOperationException(
                    "Mounted the mod against the game but it added no new files. This usually means the Unreal Engine " +
                    "version (Settings > EGame) doesn't match (DDS2 is GAME_UE5_3), Oodle isn't available (see startup " +
                    "log), or the container format isn't supported.");
            }

            result.AssetPaths = modPaths;
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
            (baseProvider as IDisposable)?.Dispose();
            (modProvider as IDisposable)?.Dispose();

            // Always remove the temporarily-staged mod files from the game folder.
            foreach (var staged in stagedInGame)
            {
                try { if (File.Exists(staged)) File.Delete(staged); }
                catch (Exception ex) { LoggingService.Instance.Warn($"Couldn't remove temporary analysis file '{staged}': {ex.Message}. You can delete it manually."); }
            }
        }

        return result;
    }

    /// Mounts the game Paks folder and returns the set of all asset paths it exposes.
    private HashSet<string> MountAndListPaths(out DefaultFileProvider provider)
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
            try { provider.SubmitKey(new CUE4Parse.UE4.Objects.Core.Misc.FGuid(), new FAesKey(_aesKeyHex)); }
            catch (Exception ex) { LoggingService.Instance.Warn($"Failed to submit AES key: {ex.Message}"); }
        }

        // Initialize() only registers archives into UnloadedVfs - it does not actually mount them
        // into Files. That final mount pass happens in PostMount(), which must run even when the
        // game has no AES encryption at all. Skipping it is why mounts always looked like they
        // produced zero files, independent of the Oodle/EGame/AES checks in the error message below.
        provider.PostMount();

        return provider.Files.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
