namespace DDS2ModManager.Models;

public enum ConflictKind
{
    BaseGameOverride,   // two mods edit the same underlying game asset path
    ModFolderNameClash  // two mods happen to use the same private MODS/<X> folder name
}

public class ModConflict
{
    public string AssetPath { get; set; } = "";
    public ConflictKind Kind { get; set; }
    public List<string> ConflictingModNames { get; set; } = new();

    /// Best-effort guess only - see CompatibilityCheckerService remarks.
    public string LikelyWinningModName { get; set; } = "";
}
