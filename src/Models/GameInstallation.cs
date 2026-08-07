namespace DDS2ModManager.Models;

public class GameInstallation
{
    /// The Steam "common\Drug Dealer Simulator 2" folder.
    public string RootPath { get; set; } = "";

    private const string DefaultProjectFolder = "DrugDealerSimulator2";

    /// The Unreal project folder name (the one containing Binaries\Win64 and Content). Detected
    /// from disk rather than assumed, so the save/config paths below resolve correctly if this is
    /// ever pointed at another UE game; falls back to DDS2's name when nothing can be detected.
    public string ProjectName
    {
        get
        {
            if (!string.IsNullOrEmpty(_projectName)) return _projectName;

            if (Directory.Exists(RootPath))
            {
                var match = Directory.GetDirectories(RootPath)
                    .FirstOrDefault(d => Directory.Exists(Path.Combine(d, "Binaries", "Win64")));
                if (match != null) return _projectName = Path.GetFileName(match);
            }

            return DefaultProjectFolder;
        }
    }
    private string? _projectName;

    public string ProjectPath => Path.Combine(RootPath, ProjectName);
    public string Win64Path => Path.Combine(ProjectPath, "Binaries", "Win64");
    public string ContentPath => Path.Combine(ProjectPath, "Content");
    public string PaksPath => Path.Combine(ContentPath, "Paks");
    public string LogicModsPath => Path.Combine(PaksPath, "LogicMods");
    public string UE4SSRootPath => Path.Combine(Win64Path, "ue4ss");
    public string UE4SSModsPath => Path.Combine(UE4SSRootPath, "Mods");
    public string ModsTxtPath => Path.Combine(UE4SSModsPath, "mods.txt");

    /// Unreal writes per-user save/config data to %LocalAppData%\<ProjectName>\Saved. This is the
    /// standard engine layout, not a DDS2 special case, so it resolves for other UE games too.
    public string SavedPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProjectName, "Saved");

    public string SaveGamesPath => Path.Combine(SavedPath, "SaveGames");
    public string ConfigPath => Path.Combine(SavedPath, "Config", "Windows");

    public bool IsValid => Directory.Exists(Win64Path);
}
