namespace DDS2ModManager.Models;

public enum ConflictKind
{
    /// Two mods ship the same asset path. Whichever loads last wins outright and the other's
    /// version of that file is simply never seen - there's no merging.
    FullFileReplacement,

    /// Two LogicMods append rows into the same base-game DataTable, and at least one row key is
    /// contributed by both. Only one mod's version of those rows survives.
    DataTableRowOverlap,

    /// Two LogicMods append into the same base-game DataTable(s) but touch entirely different
    /// rows. Not a problem - surfaced so users understand why two mods that both "edit items"
    /// can coexist.
    DataTableSharedNoOverlap,

    /// A patch mod replaces a whole DataTable that a LogicMod also appends into at runtime. Both
    /// "work", but what the appended rows land on top of depends on ordering.
    PatchReplacesAppendedTable,

    /// Two mods happen to use the same private MODS/&lt;X&gt; folder name.
    ModFolderNameClash
}

public enum ConflictSeverity
{
    /// The mods coexist fine; the card is purely explaining what they share.
    Info,

    /// Works, but the outcome depends on load order or is otherwise worth knowing.
    Warning,

    /// One mod's content is definitively lost.
    Critical
}

/// One base-game DataTable that two mods both merge into, and which row keys (if any) they both
/// contribute. An empty SharedRows means they extend the same table harmlessly.
public class TableInteraction
{
    public string TableName { get; set; } = "";
    public List<string> SharedRows { get; set; } = new();
    public bool HasOverlap => SharedRows.Count > 0;

    public string RowsDisplay => string.Join(", ", SharedRows);
}

/// One card in the Compatibility panel, aggregated per pair of mods.
///
/// Deliberately one card per pair rather than per colliding table/file: two mods that both extend
/// seven of the same tables were previously producing seven near-identical cards, which buried the
/// one thing the user actually needs to know (which two mods interact, and whether it matters)
/// under repetition.
public class ModConflictGroup
{
    public List<string> ModNames { get; set; } = new();
    public ConflictKind Kind { get; set; }
    public ConflictSeverity Severity { get; set; }

    /// Asset paths both mods ship (file-replacement conflicts only).
    public List<string> AssetPaths { get; set; } = new();

    /// Every base-game table these two mods both touch, whether or not they collide in it.
    public List<TableInteraction> TableInteractions { get; set; } = new();

    /// Best-effort guess only - see CompatibilityCheckerService remarks.
    public string LikelyWinningModName { get; set; } = "";

    public string ModNamesDisplay => string.Join("  vs.  ", ModNames);

    public List<TableInteraction> OverlappingTables => TableInteractions.Where(t => t.HasOverlap).ToList();
    public List<TableInteraction> CompatibleTables => TableInteractions.Where(t => !t.HasOverlap).ToList();

    public int TotalSharedRows => OverlappingTables.Sum(t => t.SharedRows.Count);

    /// One line naming exactly what overlaps.
    public string Summary => Kind switch
    {
        ConflictKind.ModFolderNameClash =>
            "Both installed under the same mod folder name",
        ConflictKind.FullFileReplacement =>
            $"Both replace the same {Plural(AssetPaths.Count, "file")}",
        ConflictKind.DataTableRowOverlap =>
            $"Overwrite each other on {Plural(TotalSharedRows, "row")} across {Plural(OverlappingTables.Count, "table")}",
        ConflictKind.DataTableSharedNoOverlap =>
            $"Both extend {Plural(CompatibleTables.Count, "game table")}, with no shared rows",
        ConflictKind.PatchReplacesAppendedTable =>
            $"One replaces {Plural(TableInteractions.Count, "table")} outright that the other adds rows to",
        _ => "Overlapping content"
    };

    /// What it means for the player. Kept short for Info, where the summary already says it all
    /// and a paragraph repeated on every card is just noise.
    public string Explanation => Kind switch
    {
        ConflictKind.ModFolderNameClash =>
            "One mod will overwrite the other's files. Rename or remove one of them.",
        ConflictKind.FullFileReplacement =>
            "Only one version of these files can load, so one mod will be partly or completely inactive.",
        ConflictKind.DataTableRowOverlap =>
            "Each mod's other content still works - only the rows listed below are contested, and just one mod's version of them survives.",
        ConflictKind.PatchReplacesAppendedTable =>
            "The added rows usually still apply on top, but the replaced table's contents win over the original. Worth testing in game.",
        _ => "No action needed."
    };

    public bool ShowsWinner => Severity != ConflictSeverity.Info;
    public bool HasOverlappingTables => OverlappingTables.Count > 0;
    public bool HasCompatibleTables => CompatibleTables.Count > 0;
    public bool HasAssetPaths => AssetPaths.Count > 0;

    /// Compact single-line list of the harmlessly-shared tables, so seven of them cost one wrapped
    /// line rather than seven cards.
    public string CompatibleTablesDisplay => string.Join(", ", CompatibleTables.Select(t => t.TableName));

    public string CompatibleTablesHeader => $"Shared tables ({CompatibleTables.Count}):";
    public string AssetPathsHeader => $"Files ({AssetPaths.Count}):";

    private static string Plural(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";
}
