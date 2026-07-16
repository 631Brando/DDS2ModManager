namespace DDS2ModManager.Models;

public enum ConflictKind
{
    BaseGameOverride,   // two mods edit the same underlying game asset path
    ModFolderNameClash  // two mods happen to use the same private MODS/<X> folder name
}

/// One card in the Compatibility panel: a specific set of installed mods that overwrite each
/// other's files, and every asset path they collide on. Grouped by mod set (rather than one
/// entry per colliding path) so two mods that share several files show up as a single, clearly
/// labeled "these two mods conflict" card instead of a repeated, near-identical card per file.
public class ModConflictGroup
{
    public List<string> ModNames { get; set; } = new();
    public List<string> AssetPaths { get; set; } = new();
    public ConflictKind Kind { get; set; }

    /// Best-effort guess only - see CompatibilityCheckerService remarks.
    public string LikelyWinningModName { get; set; } = "";

    /// "ModA vs. ModB" (or "ModA vs. ModB vs. ModC" for a rarer 3-way clash) - the panel's headline.
    public string ModNamesDisplay => string.Join(" vs. ", ModNames);
}
