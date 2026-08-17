using Microsoft.Win32;

namespace DDS2ModManager.Services;

/// Finds the DDS2 install by scanning every Steam library folder (default + any added
/// via Steam's "Storage Manager"). Falls back to letting the user browse manually.
public class GameDetectionService
{
    private const string GameFolderName = "Drug Dealer Simulator 2";

    public GameInstallation? TryAutoDetect()
    {
        var log = LoggingService.Instance;
        log.Info("Searching Steam libraries for Drug Dealer Simulator 2...");

        foreach (var lib in GetSteamLibraryFolders())
        {
            var candidate = Path.Combine(lib, "steamapps", "common", GameFolderName);
            var install = new GameInstallation { RootPath = candidate };
            if (install.IsValid)
            {
                log.Success($"Found game at: {candidate}");
                return install;
            }
        }

        log.Warn("Could not auto-detect the game. Please browse for the install folder manually.");
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
