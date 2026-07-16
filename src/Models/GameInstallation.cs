namespace DDS2ModManager.Models;

public class GameInstallation
{
    /// The Steam "common\Drug Dealer Simulator 2" folder.
    public string RootPath { get; set; } = "";

    private const string ProjectFolder = "DrugDealerSimulator2";

    public string ProjectPath => Path.Combine(RootPath, ProjectFolder);
    public string Win64Path => Path.Combine(ProjectPath, "Binaries", "Win64");
    public string ContentPath => Path.Combine(ProjectPath, "Content");
    public string PaksPath => Path.Combine(ContentPath, "Paks");
    public string LogicModsPath => Path.Combine(PaksPath, "LogicMods");
    public string UE4SSRootPath => Path.Combine(Win64Path, "ue4ss");
    public string UE4SSModsPath => Path.Combine(UE4SSRootPath, "Mods");
    public string ModsTxtPath => Path.Combine(UE4SSModsPath, "mods.txt");

    public bool IsValid => Directory.Exists(Win64Path);
}
