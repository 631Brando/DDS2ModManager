namespace DDS2ModManager.Services;

/// Whether a folder on disk is a playable Unreal game, a husk left behind by one, or neither.
public enum UnrealInstallState
{
    /// Nothing here looks like an Unreal game at all.
    NotUnreal,

    /// An Unreal project folder whose game has been uninstalled. Steam removes the files it put
    /// there and nothing else, so mods, UE4SS and everything UE4SS dumped stay behind - often
    /// thousands of files - and the folder still has a Binaries\Win64 in it. It is not a game.
    Leftover,

    /// A real install: an executable to run and the game's own paks to read.
    Installed
}

/// Anti-cheat systems whose presence makes modding a multiplayer game a ban risk.
[Flags]
public enum AntiCheat
{
    None = 0,
    EasyAntiCheat = 1 << 0,
    BattlEye = 1 << 1
}

/// Reads an Unreal install's shape from disk, with no knowledge of which game it is.
///
/// Everything here works on any UE game. That matters because this is what decides whether a
/// folder the scanner found is worth showing - and a wrong answer in either direction is visible:
/// a husk listed as a game invites the user to "manage" a folder of leftovers, and a real game
/// rejected simply never appears.
public static class UnrealInstallInspector
{
    /// Folders that hold Binaries\Win64 in every shipped Unreal game without being the project.
    private static readonly HashSet<string> NotProjects = new(StringComparer.OrdinalIgnoreCase)
    {
        "Engine"
    };

    /// The Unreal project folder - the one holding the game's own Binaries\Win64 and Content.
    ///
    /// The obvious approach, "the first subfolder with Binaries\Win64", is wrong on most games:
    /// Engine has one too, and Directory.GetDirectories returns it first whenever the project's
    /// name sorts after "E". Mordhau is the live example - it resolved to Engine, so every path
    /// derived from it (Content, Paks, Saved) pointed into the engine. DDS1 only escaped because
    /// "DrugDealerSimulator" happens to sort first.
    ///
    /// So Engine is excluded, and among what is left a folder whose Binaries\Win64 contains an
    /// executable named after it wins - that is how Unreal names a packaged game's binary
    /// (Mordhau\Binaries\Win64\Mordhau-Win64-Shipping.exe). Ordered by name last, purely so the
    /// answer is the same on every machine.
    public static string? FindProjectFolder(string root)
    {
        string[] dirs;
        try
        {
            if (!Directory.Exists(root)) return null;
            dirs = Directory.GetDirectories(root);
        }
        catch
        {
            return null;
        }

        var candidates = dirs
            .Where(d => !NotProjects.Contains(Path.GetFileName(d)))
            .Where(d => Directory.Exists(Path.Combine(d, "Binaries", "Win64")))
            .OrderByDescending(d => HasExeNamedAfter(d))
            .ThenByDescending(d => Directory.Exists(Path.Combine(d, "Content", "Paks")))
            .ThenBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)
            .ToList();

        return candidates.Count == 0 ? null : Path.GetFileName(candidates[0]);
    }

    private static bool HasExeNamedAfter(string projectDir)
    {
        var name = Path.GetFileName(projectDir);
        var win64 = Path.Combine(projectDir, "Binaries", "Win64");
        try
        {
            return Directory.EnumerateFiles(win64, "*.exe")
                .Any(f => Path.GetFileName(f).StartsWith(name, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    /// The game's executable inside the project's Binaries\Win64, preferring the packaged
    /// "-Shipping" build. Null when there is none, which is the main sign of an uninstalled husk.
    public static string? FindGameExecutable(string projectPath)
    {
        var win64 = Path.Combine(projectPath, "Binaries", "Win64");
        try
        {
            if (!Directory.Exists(win64)) return null;

            var exes = Directory.GetFiles(win64, "*.exe");
            return exes.FirstOrDefault(f => f.EndsWith("-Shipping.exe", StringComparison.OrdinalIgnoreCase))
                   ?? exes.FirstOrDefault(f => Path.GetFileName(f)
                          .StartsWith(Path.GetFileName(projectPath), StringComparison.OrdinalIgnoreCase))
                   // A crash reporter or a mod's helper tool is not the game.
                   ?? exes.FirstOrDefault(f => !IsHelperExe(f));
        }
        catch
        {
            return null;
        }
    }

    private static bool IsHelperExe(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return name.Contains("CrashReport", StringComparison.OrdinalIgnoreCase)
               // UnrealModLoader's launcher, which guides tell people to drop beside the game.
               || name.Contains("UnrealEngineModLauncher", StringComparison.OrdinalIgnoreCase)
               || name.Contains("UnrealCEFSubProcess", StringComparison.OrdinalIgnoreCase)
               || name.Contains("EpicWebHelper", StringComparison.OrdinalIgnoreCase);
    }

    /// The game's own container files - the ones it ships with, not mods.
    ///
    /// Only the top level of Content\Paks counts. Mods live in subfolders (LogicMods, ~mods, a
    /// per-mod folder), and they survive an uninstall exactly like UE4SS does, so counting them
    /// would make a husk with a few mods left in it look installed.
    public static IReadOnlyList<string> FindBaseContainers(string paksPath)
    {
        try
        {
            if (!Directory.Exists(paksPath)) return Array.Empty<string>();

            return Directory.GetFiles(paksPath, "*.*", SearchOption.TopDirectoryOnly)
                .Where(f => f.EndsWith(".pak", StringComparison.OrdinalIgnoreCase)
                            || f.EndsWith(".utoc", StringComparison.OrdinalIgnoreCase))
                .Where(f => !LooksLikeModContainer(f))
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// A mod dropped straight into Content\Paks is conventionally suffixed _P (the priority
    /// marker that makes it override the base game), so one sitting at the top level is still a
    /// mod and not evidence that the game is present.
    private static bool LooksLikeModContainer(string path) =>
        Path.GetFileNameWithoutExtension(path).EndsWith("_P", StringComparison.OrdinalIgnoreCase);

    /// The biggest base .pak, which is the one worth reading a version out of - small paks can be
    /// optional chunks or patches built at a different time.
    public static string? FindMainPak(string paksPath) =>
        FindBaseContainers(paksPath)
            .Where(f => f.EndsWith(".pak", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(SafeLength)
            .FirstOrDefault();

    public static bool UsesIoStore(string paksPath) =>
        FindBaseContainers(paksPath).Any(f => f.EndsWith(".utoc", StringComparison.OrdinalIgnoreCase));

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return 0; }
    }

    /// What the folder at <paramref name="root"/> actually is.
    ///
    /// Installed needs BOTH an executable and base containers. Either alone is a husk: a folder
    /// with only Binaries\Win64 is what UE4SS leaves behind, and one with only paks is a partial
    /// copy that would fail in confusing ways the first time anything tried to read it.
    public static UnrealInstallState Inspect(string root, out string? projectName)
    {
        projectName = FindProjectFolder(root);
        if (projectName == null) return UnrealInstallState.NotUnreal;

        var project = Path.Combine(root, projectName);
        var hasExe = FindGameExecutable(project) != null;
        var hasContainers = FindBaseContainers(Path.Combine(project, "Content", "Paks")).Count > 0;

        return hasExe && hasContainers ? UnrealInstallState.Installed : UnrealInstallState.Leftover;
    }

    /// Anti-cheat installed alongside the game.
    ///
    /// Looked for so the manager can say so, not to refuse: single-player games ship anti-cheat
    /// too, and the user is the one who knows whether they play online. But modding a protected
    /// multiplayer game is the one way this tool can cost someone their account, so it is worth a
    /// sentence before they find out the hard way.
    public static AntiCheat DetectAntiCheat(string root, string? projectName)
    {
        var found = AntiCheat.None;
        var places = new List<string> { root };
        if (projectName != null) places.Add(Path.Combine(root, projectName, "Binaries", "Win64"));

        foreach (var place in places)
        {
            try
            {
                if (!Directory.Exists(place)) continue;
                foreach (var entry in Directory.EnumerateFileSystemEntries(place))
                {
                    var name = Path.GetFileName(entry);

                    // Matched on the files each system actually ships, not only its brand name:
                    // BattlEye's service is BEService_x64.exe and Epic's EasyAntiCheat launches
                    // games through start_protected_game.exe, neither of which says what it is.
                    if (name.Contains("EasyAntiCheat", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("start_protected_game.exe", StringComparison.OrdinalIgnoreCase))
                        found |= AntiCheat.EasyAntiCheat;

                    if (name.Contains("BattlEye", StringComparison.OrdinalIgnoreCase)
                        || name.StartsWith("BEService", StringComparison.OrdinalIgnoreCase))
                        found |= AntiCheat.BattlEye;
                }
            }
            catch
            {
                // Unreadable folder: say nothing rather than guess.
            }
        }

        return found;
    }

    /// The folder under Saved\Config that this game writes its .ini files to.
    ///
    /// It is named after the platform the game was COOKED for, and that varies: UE4 games are
    /// usually "WindowsNoEditor", UE5 shortened it to "Windows", and client builds of multiplayer
    /// games use "WindowsClient" (Mordhau). Getting it wrong finds no config at all - and Reset
    /// would aim at a folder that isn't the game's - so the answer comes from evidence, strongest
    /// first:
    ///
    ///   1. A folder that already exists and holds the files Unreal writes there.
    ///   2. The cook platform in the base pak's own name: "pakchunk0-WindowsClient.pak" was cooked
    ///      for WindowsClient. Available before the game has ever been launched.
    ///   3. The engine generation.
    ///
    /// Never returns an empty string. Path.Combine with "" would make the config folder Saved\Config
    /// itself, and Reset deletes that folder.
    public static string ResolveConfigPlatformDir(string projectPath, string projectName, bool isUe5)
    {
        var configRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            projectName, "Saved", "Config");

        try
        {
            if (Directory.Exists(configRoot))
            {
                var withSettings = Directory.GetDirectories(configRoot)
                    .Where(d => !Path.GetFileName(d).StartsWith("CrashReport", StringComparison.OrdinalIgnoreCase))
                    .FirstOrDefault(d => File.Exists(Path.Combine(d, "GameUserSettings.ini"))
                                         || File.Exists(Path.Combine(d, "Engine.ini")));
                if (withSettings != null) return Path.GetFileName(withSettings);
            }
        }
        catch
        {
            // fall through to the pak name
        }

        var fromPak = CookPlatformFromPakName(FindMainPak(Path.Combine(projectPath, "Content", "Paks")));
        if (fromPak != null) return fromPak;

        return isUe5 ? "Windows" : "WindowsNoEditor";
    }

    /// "pakchunk0-WindowsClient.pak" -> "WindowsClient". Null when the name carries no Windows
    /// platform, so a custom-named pak can't produce a nonsense folder name. Public for tests.
    public static string? CookPlatformFromPakName(string? pakPath)
    {
        if (string.IsNullOrWhiteSpace(pakPath)) return null;

        var name = Path.GetFileNameWithoutExtension(pakPath);
        var dash = name.LastIndexOf('-');
        if (dash < 0 || dash == name.Length - 1) return null;

        var platform = name[(dash + 1)..];

        // A patch pak's suffix ("Windows_0_P") isn't part of the platform name.
        var underscore = platform.IndexOf('_');
        if (underscore > 0) platform = platform[..underscore];

        return platform.StartsWith("Windows", StringComparison.OrdinalIgnoreCase)
               && platform.All(char.IsLetter)
            ? platform
            : null;
    }
}
