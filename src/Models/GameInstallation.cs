namespace DDS2ModManager.Models;

public class GameInstallation
{
    /// The Steam "common\<game>" folder.
    public string RootPath { get; set; } = "";

    /// Which game this is. Set explicitly by detection, which knows what it went looking for;
    /// otherwise inferred from the project folder found on disk.
    ///
    /// Note this reads <see cref="DetectedProjectName"/> and not <see cref="ProjectName"/> -
    /// ProjectName falls back to the profile, so going through it would recurse forever.
    public GameProfile Profile
    {
        get => _profile ??= GameProfiles.ByProjectFolder(DetectedProjectName) ?? GameProfiles.Default;
        set => _profile = value;
    }
    private GameProfile? _profile;

    /// The Unreal project folder as it actually exists on disk, or null if nothing looks right.
    /// Detected rather than assumed so a renamed or repacked install still resolves correctly.
    public string? DetectedProjectName
    {
        get
        {
            if (_detected != null) return _detected;
            if (!Directory.Exists(RootPath)) return null;

            var match = Directory.GetDirectories(RootPath)
                .FirstOrDefault(d => Directory.Exists(Path.Combine(d, "Binaries", "Win64")));
            return match == null ? null : _detected = Path.GetFileName(match);
        }
    }
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

    public bool IsValid => Directory.Exists(Win64Path);
}
