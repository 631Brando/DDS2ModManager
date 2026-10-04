namespace DDS2ModManager.Models;

public class GameInstallation
{
    /// The Steam "common\<game>" folder.
    public string RootPath { get; set; } = "";

    /// Which game this is. Set explicitly by the game catalog, which resolves it once; otherwise
    /// inferred here through the same resolver.
    ///
    /// An Unreal project that isn't DDS1 or DDS2 gets a GENERIC profile, never DDS2's. It used to
    /// fall back to DDS2, and that one line was the root of most of what made "any Unreal game"
    /// unsafe: a browsed Mordhau became DDS2, was offered DDS2's experimental UE4SS, was read with
    /// DDS2's usmap, got DDS2's Nexus cards, and had its path saved into DDS2's settings section -
    /// overwriting the user's real DDS2 folder. Only a folder with nothing on disk to identify
    /// (missing, or no Unreal project inside) keeps the old default, since it can't be managed
    /// either way and legacy settings migration expects it.
    ///
    /// Note this reads <see cref="DetectedProjectName"/> and not <see cref="ProjectName"/> -
    /// ProjectName falls back to the profile, so going through it would recurse forever.
    public GameProfile Profile
    {
        get => _profile ??= GenericGameProfiles.Resolve(
            RootPath, DetectedProjectName, GameStoreIndex.Shared.Identify(RootPath), readExecutable: false);
        set => _profile = value;
    }
    private GameProfile? _profile;

    /// The Unreal project folder as it actually exists on disk, or null if nothing looks right.
    /// Detected rather than assumed so a renamed or repacked install still resolves correctly.
    ///
    /// Never "Engine": every packaged Unreal game has an Engine folder with its own Binaries\Win64,
    /// and taking the first such folder pointed Mordhau's every path into the engine. See
    /// UnrealInstallInspector.FindProjectFolder.
    public string? DetectedProjectName => _detected ??= UnrealInstallInspector.FindProjectFolder(RootPath);
    private string? _detected;

    /// The project folder name to use: what's on disk, else what the profile expects.
    public string ProjectName => DetectedProjectName ?? Profile.ProjectFolderName;

    public string ProjectPath => Path.Combine(RootPath, ProjectName);
    public string Win64Path => Path.Combine(ProjectPath, "Binaries", "Win64");
    public string ContentPath => Path.Combine(ProjectPath, "Content");
    public string PaksPath => Path.Combine(ContentPath, "Paks");
    public string LogicModsPath => Path.Combine(PaksPath, "LogicMods");

    /// UE4SS 3.1+ keeps its files in a ue4ss\ subfolder; 3.0.x and earlier put UE4SS.dll and Mods\
    /// straight into Binaries\Win64. DDS1's scene still runs the older layout, so detect rather
    /// than assume - but only for locating *mods*, see the warning on UE4SSRootPath.
    public bool HasLegacyUE4SSLayout =>
        !Directory.Exists(Path.Combine(Win64Path, "ue4ss"))
        && File.Exists(Path.Combine(Win64Path, "Mods", "mods.txt"));

    /// UE4SS's own folder.
    ///
    /// DELIBERATELY not layout-aware: callers delete this recursively (GameResetService), and under
    /// the legacy layout "UE4SS's folder" is Binaries\Win64 itself - which holds the game executable.
    /// Resolving that here would turn "remove UE4SS" into "delete the game". This always points at
    /// the ue4ss\ subfolder, which is also the only layout we ever install.
    public string UE4SSRootPath => Path.Combine(Win64Path, "ue4ss");

    /// Where UE4SS looks for mods. Layout-aware, unlike UE4SSRootPath, because reading an existing
    /// install's mod list has to work against whichever layout is actually there.
    public string UE4SSModsPath => HasLegacyUE4SSLayout
        ? Path.Combine(Win64Path, "Mods")
        : Path.Combine(UE4SSRootPath, "Mods");

    public string ModsTxtPath => Path.Combine(UE4SSModsPath, "mods.txt");

    /// Unreal writes per-user save/config data to %LocalAppData%\<ProjectName>\Saved. This is the
    /// standard engine layout, not a DDS2 special case, so it resolves for other UE games too.
    public string SavedPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProjectName, "Saved");

    public string SaveGamesPath => Path.Combine(SavedPath, "SaveGames");

    /// Every folder under Saved\ that can hold save games, in presentation order.
    ///
    /// Usually just SaveGames, but DDS1 splits them: SaveGames holds only a GVAS slot index and the
    /// graphics settings, while the playable saves are RamaSave containers in Saved\Serialized.
    /// Looking at SaveGames alone would tell a DDS1 player they have no saves.
    public IEnumerable<string> SaveRootPaths =>
        Profile.SaveSubfolders.Select(sub => Path.Combine(SavedPath, sub));

    /// UE4 writes "WindowsNoEditor"; UE5 shortened it to "Windows". Getting this wrong means finding
    /// no .ini files at all rather than failing loudly, so it comes from the profile.
    public string ConfigPath => Path.Combine(SavedPath, "Config", Profile.ConfigPlatformDir);

    /// Whether the folder is shaped like an Unreal project at all.
    ///
    /// Structural only, and deliberately left that way: tests and the settings window build
    /// installs from nothing but a Binaries\Win64 folder. Whether a GAME is actually there - an
    /// executable to run and paks to read - is <see cref="InstallState"/>, which is what anything
    /// deciding to open, list or modify a game must ask.
    public bool IsValid => Directory.Exists(Win64Path);

    /// Installed, a husk an uninstall left behind, or not an Unreal game at all.
    ///
    /// Re-read every time rather than cached: the game can be installed or removed while the
    /// manager is open, and a stale answer here is exactly the bug it exists to prevent - a deleted
    /// DDS2 still listed as installed because UE4SS had left Binaries\Win64 behind.
    public UnrealInstallState InstallState => UnrealInstallInspector.Inspect(RootPath, out _);

    public bool IsInstalled => InstallState == UnrealInstallState.Installed;

    /// The game's executable, or null when none is present (the clearest sign of a husk).
    public string? ExecutablePath => UnrealInstallInspector.FindGameExecutable(ProjectPath);
}
