using System.Security.Cryptography;
using System.Text;

namespace DDS2ModManager.Services;

/// The one place this app's own storage is named and laid out.
///
/// Every one of these paths used to be spelled out at its call site - the %AppData% folder name alone
/// appeared in fourteen files with no shared constant. That made the app's name a fourteen-file change
/// and made it easy for a new store to quietly pick a slightly different folder.
///
/// Note what is NOT here: anything inside the *game's* folders. Those come from GameInstallation,
/// which derives them from a detected install.
///
/// Kept deliberately dependency-free (no Models, no CUE4Parse): the setup project links this file
/// rather than referencing the main project, precisely to avoid dragging that graph into the
/// installer. See setup/DDS2ModManagerSetup.csproj. Do not take a GameInstallation here.
public static class AppPaths
{
    /// The folder under %AppData% holding settings, logs, caches and disabled mods.
    ///
    /// DELIBERATELY still the old name while the app displays a new one. It is invisible to users,
    /// and renaming it would mean rewriting the absolute paths recorded inside registry_*.json for
    /// every disabled mod - real risk, no benefit. The visible name is AppDisplayName.
    public const string AppDataFolderName = "DDS2ModManager";

    /// What the app calls itself to the user. Not "DDS2" any more: it manages both Drug Dealer
    /// Simulator games, and a name that claims otherwise is wrong on half the tabs.
    ///
    /// Only ever a display string. The assembly, the %AppData% folder, the GitHub repository, the
    /// release asset name and every registry KEY keep their original names - those are identifiers
    /// that existing installs already depend on, and the release asset in particular is matched by
    /// exact filename, so renaming it would silently strand every copy already out there.
    public const string AppDisplayName = "DDS Mod Manager";

    public static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppDataFolderName);

    /// Root, guaranteed to exist. Most callers want this - they are about to write into it.
    public static string EnsureRoot()
    {
        Directory.CreateDirectory(Root);
        return Root;
    }

    public static string Logs => Path.Combine(Root, "Logs");

    /// Where a disabled pak mod's files are parked. UE loads any pak it finds in the game folder, so
    /// disabling has to physically move the files out rather than just flip a flag.
    public static string DisabledMods => Path.Combine(Root, "DisabledMods");

    public static string DisabledSaves => Path.Combine(Root, "DisabledSaves");
    public static string Profiles => Path.Combine(Root, "Profiles");
    public static string Backups => Path.Combine(Root, "Backups");
    public static string NexusImages => Path.Combine(Root, "NexusImages");

    // ---- per-game locations --------------------------------------------------------------------
    //
    // Everything below is scoped by GameKey. The unscoped properties above are deliberately kept:
    // they are the legacy locations, and the one-time migration has to be able to address them.
    //
    // These take the install's root PATH rather than a GameInstallation, on purpose - see the note
    // at the top of this file about staying dependency-free for the setup project.

    public static string DisabledModsFor(string gameRootPath) => DisabledModsForKey(GameKey(gameRootPath));
    public static string DisabledSavesFor(string gameRootPath) => DisabledSavesForKey(GameKey(gameRootPath));
    public static string ProfilesFor(string gameRootPath) => ProfilesForKey(GameKey(gameRootPath));
    public static string BackupsFor(string gameRootPath) => BackupsForKey(GameKey(gameRootPath));
    public static string ModHistoryFor(string gameRootPath) => ModHistoryForKey(GameKey(gameRootPath));

    // The same locations addressed by an already-computed key. The migration works from the key it
    // recovers out of a registry_<key>.json filename, where the install path is not available.

    public static string DisabledModsForKey(string gameKey) => Path.Combine(DisabledMods, gameKey);
    public static string DisabledSavesForKey(string gameKey) => Path.Combine(DisabledSaves, gameKey);
    public static string ProfilesForKey(string gameKey) => Path.Combine(Profiles, gameKey);
    public static string BackupsForKey(string gameKey) => Path.Combine(Backups, gameKey);

    /// A file rather than a folder, mirroring registry_&lt;key&gt;.json which sits beside it.
    public static string ModHistoryForKey(string gameKey) => Path.Combine(Root, $"mod-history_{gameKey}.json");

    public static string RegistryForKey(string gameKey) => Path.Combine(Root, $"registry_{gameKey}.json");

    /// Recovers the key from a registry filename. Returns null for anything else in the folder.
    public static string? KeyFromRegistryPath(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return name.StartsWith("registry_", StringComparison.OrdinalIgnoreCase) && name.Length > "registry_".Length
            ? name["registry_".Length..]
            : null;
    }

    public static string Settings => Path.Combine(Root, "settings.json");
    public static string Mappings => Path.Combine(Root, "mappings.usmap");

    /// Short stable key identifying one game *install*, for per-game state filenames and folders.
    ///
    /// Keyed on the install path rather than the game id on purpose: two copies of the same game
    /// (a Steam install and a second one kept for testing) are separate worlds and must not share
    /// tracked mods, disabled mods or disabled saves. ModRegistryService established this and
    /// SaveGameService's DisabledSaves\&lt;key&gt;\ copied it; it now lives in one place so the
    /// stores that still need scoping cannot drift into a second scheme.
    ///
    /// Do not change the algorithm: it names files that already exist on users' disks.
    public static string GameKey(string rootPath) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rootPath.ToLowerInvariant())))[..12];
}
