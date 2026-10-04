using System.Text.Json;
using Microsoft.Win32;

namespace DDS2ModManager.Services;

public enum GameStore
{
    None,
    Steam,
    Epic,
    Gog
}

/// Who installed a game, and what that store calls it.
public sealed record StoreIdentity(GameStore Store, string? StoreId, string? DisplayName, uint SteamAppId)
{
    public static readonly StoreIdentity Unknown = new(GameStore.None, null, null, 0);

    public string StoreLabel => Store switch
    {
        GameStore.Steam => "Steam",
        GameStore.Epic => "Epic Games",
        GameStore.Gog => "GOG",
        _ => "Added folder"
    };
}

/// A snapshot of every game install the launchers on this PC know about, keyed by folder.
///
/// This is what makes a game's identity independent of how it was found. Looking a folder up here
/// gives the same answer whether the scanner walked to it or the user browsed to it - so a game
/// opened both ways gets one settings section, not two. Without it, identity would depend on the
/// discovery route, and a Steam game added by hand would quietly start over with no settings.
///
/// Read-only throughout: nothing here writes to a launcher's files or the registry.
public sealed class GameStoreIndex
{
    private static readonly object SharedLock = new();
    private static GameStoreIndex? _shared;

    /// One index for the process, rebuilt on demand (a rescan) rather than on every lookup - the
    /// launchers' manifests don't change while someone is looking at a list of games.
    public static GameStoreIndex Shared
    {
        get
        {
            lock (SharedLock) return _shared ??= Build();
        }
    }

    public static GameStoreIndex Refresh()
    {
        var fresh = Build();
        lock (SharedLock) _shared = fresh;
        return fresh;
    }

    private readonly Dictionary<string, StoreIdentity> _byFolder;

    /// The Steam client's own folder (where appcache lives), or null if Steam isn't installed.
    public string? SteamRoot { get; }

    /// Every Steam library folder: the default one plus any added in Storage Manager.
    public IReadOnlyList<string> SteamLibraries { get; }

    /// Every install a launcher lists, with the folder it lives in. Includes folders that no longer
    /// exist - callers inspect the folder before trusting it.
    public IReadOnlyCollection<KeyValuePair<string, StoreIdentity>> KnownInstalls => _byFolder;

    private GameStoreIndex(string? steamRoot, IReadOnlyList<string> libraries, Dictionary<string, StoreIdentity> byFolder)
    {
        SteamRoot = steamRoot;
        SteamLibraries = libraries;
        _byFolder = byFolder;
    }

    /// Case- and separator-insensitive, because the same folder reaches us spelled several ways:
    /// a manifest's installdir, a registry value with forward slashes, a folder picker's output.
    public static string NormalizeFolder(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Replace('/', '\\')));
        }
        catch
        {
            return path.TrimEnd('\\', '/');
        }
    }

    public StoreIdentity? Identify(string folder) =>
        _byFolder.TryGetValue(NormalizeFolder(folder), out var id) ? id : null;

    public static GameStoreIndex Build()
    {
        var byFolder = new Dictionary<string, StoreIdentity>(StringComparer.OrdinalIgnoreCase);

        var steamRoot = FindSteamRoot();
        var libraries = steamRoot == null ? new List<string>() : FindSteamLibraries(steamRoot);

        // Each source is independent and failure-isolated: an unreadable Epic manifest must not
        // cost us the Steam games, and a missing launcher is simply absent.
        foreach (var (folder, id) in ReadSteamManifests(libraries)) byFolder.TryAdd(folder, id);
        foreach (var (folder, id) in ReadEpicManifests()) byFolder.TryAdd(folder, id);
        foreach (var (folder, id) in ReadGogRegistry()) byFolder.TryAdd(folder, id);

        return new GameStoreIndex(steamRoot, libraries, byFolder);
    }

    // ---- Steam ----------------------------------------------------------------------------------

    private static string? FindSteamRoot()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (key?.GetValue("SteamPath") is string path && !string.IsNullOrWhiteSpace(path))
                return NormalizeFolder(path);
        }
        catch { /* fall through */ }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam")
                          ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Valve\Steam");
            if (key?.GetValue("InstallPath") is string path && !string.IsNullOrWhiteSpace(path))
                return NormalizeFolder(path);
        }
        catch { /* no Steam */ }

        return null;
    }

    /// The default library plus every one listed in libraryfolders.vdf.
    ///
    /// The extra libraries are a bonus on top of the default, so failing to read them must not cost
    /// the default: Steam rewrites that file while it runs and can hold it locked.
    private static List<string> FindSteamLibraries(string steamRoot)
    {
        var results = new List<string> { steamRoot };

        var vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        try
        {
            if (File.Exists(vdf))
            {
                foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s*\"([^\"]+)\""))
                {
                    var p = NormalizeFolder(m.Groups[1].Value.Replace("\\\\", "\\"));
                    if (!results.Contains(p, StringComparer.OrdinalIgnoreCase)) results.Add(p);
                }
            }
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Warn(
                $"Couldn't read Steam's library list ({ex.Message}). Only the default Steam folder will be searched.");
        }

        return results;
    }

    private static IEnumerable<(string Folder, StoreIdentity Id)> ReadSteamManifests(IEnumerable<string> libraries)
    {
        var found = new List<(string, StoreIdentity)>();

        foreach (var lib in libraries)
        {
            var steamapps = Path.Combine(lib, "steamapps");
            string[] manifests;
            try
            {
                if (!Directory.Exists(steamapps)) continue;
                manifests = Directory.GetFiles(steamapps, "appmanifest_*.acf");
            }
            catch
            {
                continue;
            }

            foreach (var file in manifests)
            {
                var parsed = ParseSteamManifest(SafeRead(file));
                if (parsed == null) continue;

                var folder = NormalizeFolder(Path.Combine(steamapps, "common", parsed.Value.InstallDir));
                found.Add((folder, new StoreIdentity(GameStore.Steam, parsed.Value.AppId.ToString(),
                    parsed.Value.Name, parsed.Value.AppId)));
            }
        }

        return found;
    }

    /// Reads the three fields that matter from a Steam appmanifest_*.acf. Public for tests.
    ///
    /// ACF is Valve's KeyValues text format. A full parser is not needed for three top-level string
    /// values, and a regex keeps this tolerant of the format's loose whitespace.
    public static (uint AppId, string Name, string InstallDir)? ParseSteamManifest(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        static string? Field(string text, string key)
        {
            var m = Regex.Match(text, $"\"{key}\"\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : null;
        }

        var appIdText = Field(text, "appid");
        var installDir = Field(text, "installdir");
        if (!uint.TryParse(appIdText, out var appId) || appId == 0 || string.IsNullOrWhiteSpace(installDir))
            return null;

        return (appId, Field(text, "name") ?? installDir, installDir);
    }

    // ---- Epic -----------------------------------------------------------------------------------

    private static IEnumerable<(string Folder, StoreIdentity Id)> ReadEpicManifests()
    {
        var found = new List<(string, StoreIdentity)>();
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");

        string[] files;
        try
        {
            if (!Directory.Exists(dir)) return found;
            files = Directory.GetFiles(dir, "*.item");
        }
        catch
        {
            return found;
        }

        foreach (var file in files)
        {
            var parsed = ParseEpicManifest(SafeRead(file));
            if (parsed == null) continue;
            found.Add((NormalizeFolder(parsed.Value.InstallLocation),
                new StoreIdentity(GameStore.Epic, parsed.Value.AppName, parsed.Value.DisplayName, 0)));
        }

        return found;
    }

    /// The fields that matter from an Epic launcher .item manifest (JSON). Public for tests.
    public static (string AppName, string DisplayName, string InstallLocation)? ParseEpicManifest(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true });
            var root = doc.RootElement;

            string? Str(string name) =>
                root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

            var location = Str("InstallLocation");
            var appName = Str("AppName");
            if (string.IsNullOrWhiteSpace(location) || string.IsNullOrWhiteSpace(appName)) return null;

            return (appName, Str("DisplayName") ?? appName, location);
        }
        catch
        {
            return null;
        }
    }

    // ---- GOG ------------------------------------------------------------------------------------

    private static IEnumerable<(string Folder, StoreIdentity Id)> ReadGogRegistry()
    {
        var found = new List<(string, StoreIdentity)>();

        try
        {
            using var games = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\Games")
                              ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\GOG.com\Games");
            if (games == null) return found;

            foreach (var name in games.GetSubKeyNames())
            {
                try
                {
                    using var game = games.OpenSubKey(name);
                    if (game?.GetValue("path") is not string path || string.IsNullOrWhiteSpace(path)) continue;

                    var gameId = game.GetValue("gameID") as string ?? name;
                    var title = game.GetValue("gameName") as string;
                    found.Add((NormalizeFolder(path), new StoreIdentity(GameStore.Gog, gameId, title, 0)));
                }
                catch
                {
                    // One unreadable entry costs only itself.
                }
            }
        }
        catch
        {
            // No GOG.
        }

        return found;
    }

    private static string? SafeRead(string path)
    {
        try { return File.ReadAllText(path); }
        catch { return null; }
    }
}
