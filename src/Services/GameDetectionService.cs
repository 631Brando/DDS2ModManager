using Microsoft.Win32;

namespace DDS2ModManager.Services;

/// Finds supported game installs by scanning every Steam library folder (default + any added
/// via Steam's "Storage Manager"). Falls back to letting the user browse manually.
public class GameDetectionService
{
    /// Every supported game that is actually installed, in GameProfiles order.
    ///
    /// The profile is assigned rather than inferred: detection knows which game it went looking
    /// for, and that is more trustworthy than guessing from a folder name afterwards.
    public IReadOnlyList<GameInstallation> DetectAll()
    {
        var log = LoggingService.Instance;
        var found = new List<GameInstallation>();

        // Materialised once: the library list involves a registry read and a file parse, and
        // repeating that per game would triple the work to reach the same answer.
        var libraries = GetSteamLibraryFolders().ToList();
        if (libraries.Count == 0) return found;

        foreach (var profile in GameProfiles.All)
        {
            foreach (var lib in libraries)
            {
                var candidate = Path.Combine(lib, "steamapps", "common", profile.SteamFolderName);
                var install = new GameInstallation { RootPath = candidate, Profile = profile };
                if (!install.IsValid) continue;

                log.Success($"Found {profile.DisplayName} at: {candidate}");
                found.Add(install);
                break; // first library holding this game wins; the same game can't be installed twice
            }
        }

        return found;
    }

    /// The install to open on startup when nothing else is remembered. DDS2 comes first in
    /// GameProfiles, so a machine with both keeps opening on DDS2 as it always has.
    public GameInstallation? TryAutoDetect()
    {
        var log = LoggingService.Instance;
        log.Info("Searching Steam libraries for supported games...");

        var all = DetectAll();
        if (all.Count > 0) return all[0];

        log.Warn("Could not auto-detect a supported game. Please browse for the install folder manually.");
        return null;
    }

    private IEnumerable<string> GetSteamLibraryFolders()
    {
        var results = new List<string>();
        var steamPath = GetSteamInstallPath();
        if (steamPath == null) yield break;

        results.Add(steamPath);

        // The extra libraries are a bonus on top of the default one, so a failure to read them must
        // not cost us the default. Steam rewrites this file while it runs and can hold it locked,
        // and an exception escaping here would abort auto-detection completely - reporting "could
        // not find the game" to someone whose game is sitting in the folder we already know about.
        var vdfPath = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdfPath))
        {
            try
            {
                var text = File.ReadAllText(vdfPath);
                foreach (Match m in Regex.Matches(text, "\"path\"\\s*\"([^\"]+)\""))
                {
                    var p = m.Groups[1].Value.Replace("\\\\", "\\");
                    if (!results.Contains(p, StringComparer.OrdinalIgnoreCase))
                        results.Add(p);
                }
            }
            catch (Exception ex)
            {
                LoggingService.Instance.Warn(
                    $"Couldn't read Steam's library list ({ex.Message}). Only the default Steam folder will be "
                    + "searched - if the game is on another drive, browse for it manually.");
            }
        }

        foreach (var r in results) yield return r;
    }

    private string? GetSteamInstallPath()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            var path = key?.GetValue("SteamPath") as string;
            if (!string.IsNullOrEmpty(path)) return path.Replace('/', '\\');
        }
        catch { }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam")
                          ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Valve\Steam");
            var path = key?.GetValue("InstallPath") as string;
            if (!string.IsNullOrEmpty(path)) return path;
        }
        catch { }

        return null;
    }
}
