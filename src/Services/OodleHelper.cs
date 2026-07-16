namespace DDS2ModManager.Services;

/// UE5 .ucas/.pak content is Oodle-compressed. CUE4Parse decompresses it via a native Oodle
/// library it does NOT ship, but CUE4Parse.Compression.OodleHelper.Initialize() already knows how
/// to fetch the right one (from WorkingRobot's OodleUE builds) when it's missing - that's the same
/// mechanism FModel and every other CUE4Parse consumer rely on, so end users never have to supply a
/// DLL themselves. We only wrap it to (a) point the download at a stable path next to our exe so it
/// survives between runs and (b) prefer copying a loose DLL straight out of the game folder when one
/// is available, avoiding a network call entirely.
public static class OodleHelper
{
    private static bool _initialized;
    private static readonly object _lock = new();

    /// Ensures Oodle decompression is ready before any pak/utoc content is read. Safe to call
    /// repeatedly. Returns false only if we truly couldn't obtain Oodle (offline and no cached or
    /// game-local copy to fall back on).
    public static bool EnsureOodleAvailable(GameInstallation game)
    {
        lock (_lock)
        {
            if (_initialized) return true;

            var log = LoggingService.Instance;
            var exeDir = AppContext.BaseDirectory;

            // CUE4Parse itself prefers a legacy oo2core_9_win64.dll if present, otherwise the
            // current oodle-data-shared.dll/.so. Honor whichever one's already sitting next to us
            // (e.g. dropped in manually) before deciding where a fresh download should land.
            var legacyPath = Path.Combine(exeDir, global::CUE4Parse.Compression.OodleHelper.OODLE_NAME_OLD);
            var currentPath = Path.Combine(exeDir, global::CUE4Parse.Compression.OodleHelper.OodleFileName);
            var targetPath = File.Exists(legacyPath) ? legacyPath : currentPath;

            // If neither is already present, try to copy one straight out of the game install -
            // some UE5 games ship a loose copy, which saves a network round-trip. DDS2 links Oodle
            // statically into its exe, so this normally won't find anything and we fall through to
            // CUE4Parse's own downloader below.
            if (!File.Exists(targetPath) && Directory.Exists(game.RootPath))
            {
                try
                {
                    var found = Directory.GetFiles(game.RootPath, "oo2core_*_win64.dll", SearchOption.AllDirectories).FirstOrDefault()
                                ?? Directory.GetFiles(game.RootPath, global::CUE4Parse.Compression.OodleHelper.OodleFileName, SearchOption.AllDirectories).FirstOrDefault();
                    if (found != null)
                    {
                        targetPath = Path.Combine(exeDir, Path.GetFileName(found));
                        File.Copy(found, targetPath, true);
                        log.Info($"Copied {Path.GetFileName(found)} from the game folder for Oodle decompression.");
                    }
                }
                catch (Exception ex)
                {
                    log.Warn($"Couldn't copy an Oodle DLL from the game folder: {ex.Message}");
                }
            }

            try
            {
                // Downloads straight to targetPath if it isn't there yet, then loads and
                // initializes the native decompressor against it. Failures inside CUE4Parse's own
                // downloader are logged there and swallowed (Instance stays null) rather than
                // thrown, so we check Instance ourselves to detect that case.
                global::CUE4Parse.Compression.OodleHelper.Initialize(targetPath);
                if (global::CUE4Parse.Compression.OodleHelper.Instance == null)
                    throw new InvalidOperationException("CUE4Parse could not obtain an Oodle library (offline, and no cached or game-local copy was found).");

                _initialized = true;
                log.Success("Oodle decompression ready.");
                return true;
            }
            catch (Exception ex)
            {
                log.Error($"Oodle initialization failed: {ex.Message} " +
                          "Compressed mods can't be read until Oodle is available - check your internet connection, " +
                          $"or copy an Oodle DLL from any UE5 game to \"{targetPath}\".");
                return false;
            }
        }
    }
}
