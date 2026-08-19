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

    /// Two mods happen to use the same private MODS/&lt;X&gt; folder name. Also used for two lua
    /// mods installed into the same ue4ss\Mods\&lt;X&gt; folder, which is the same failure with a
    /// different folder root: one copy of the files, one mods.txt entry, one surviving mod.
    ModFolderNameClash,

    /// Two lua mods call RegisterConsoleCommandHandler with the same command name. Both mods
    /// still load and everything else they do keeps working - only that one command is contested.
    LuaConsoleCommandClash,

    /// Two lua mods call RegisterKeyBind with the same key + modifier combination. As above:
    /// only the key is contested, not the mod.
    LuaKeybindClash,

    /// A loose .uasset on disk and a pak mod both target the same game asset.
    ///
    /// Which one the game actually uses is NOT something this tool can predict: a loose file wins
    /// through UnrealModUnlocker's filesystem hook, a _P pak wins through chunk priority, and which
    /// beats which has to be observed in-game rather than reasoned about. So this is reported as a
    /// contest without naming a winner - see ShowsWinner.
    LooseOverridesPak
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
    /// Prefixes written in front of every contested lua registration stored in AssetPaths.
    ///
    /// Two lua mods can clash on a command name AND on a key, which arrives here as two groups
    /// naming the same pair - and the pair merge in CompatibilityCheckerService folds those into
    /// one card that keeps only one Kind. Labelling each entry is what stops that merged card
    /// from silently presenting a keybind as a console command.
    public const string LuaCommandLabel = "console command ";
    public const string LuaKeybindLabel = "keybind ";

    public List<string> ModNames { get; set; } = new();
    public ConflictKind Kind { get; set; }
    public ConflictSeverity Severity { get; set; }

    /// Asset paths both mods ship (file-replacement conflicts only), or - for the lua kinds -
    /// the contested registrations, each prefixed with one of the labels above.
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
        ConflictKind.LuaConsoleCommandClash or ConflictKind.LuaKeybindClash => LuaSummary,
        ConflictKind.LooseOverridesPak =>
            $"A loose file and a pak mod both target the same {Plural(AssetPaths.Count, "asset")}",
        _ => "Overlapping content"
    };

    /// Both lua kinds share one summary because a pair can clash on BOTH a command and a key,
    /// and the existing per-pair merge folds that into a single card keeping only one Kind.
    ///
    /// Counting only the entries matching that surviving Kind made the card disagree with
    /// itself: the summary said "1 console command" while the list header above it said
    /// "Contested registrations (2)", and the keybind half went unmentioned. A user reading
    /// that has no way to tell which number is wrong.
    private string LuaSummary
    {
        get
        {
            var commands = CountLuaEntries(LuaCommandLabel);
            var keys = CountLuaEntries(LuaKeybindLabel);

            if (commands > 0 && keys > 0)
                return $"Both claim {Plural(commands, "console command")} and {Plural(keys, "key")}";

            return commands > 0
                ? $"Both register {Plural(commands, "console command")} under the same name"
                : $"Both bind {Plural(keys, "key")}";
        }
    }

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
        // Same merge problem as the summary: a card carrying both kinds must explain both, or
        // the half that lost the Kind coin-toss is silently unexplained.
        ConflictKind.LuaConsoleCommandClash or ConflictKind.LuaKeybindClash => LuaExplanation,
        ConflictKind.LooseOverridesPak =>
            "One of them will be ignored for these assets, but which one can't be predicted from here - a loose "
            + "file and a pak reach the engine by different routes. Test in game, or disable one to be certain.",
        _ => "No action needed."
    };

    private string LuaExplanation
    {
        get
        {
            var commands = CountLuaEntries(LuaCommandLabel) > 0;
            var keys = CountLuaEntries(LuaKeybindLabel) > 0;

            if (commands && keys)
                return "Everything else both mods do still works. The contested command and key each end up " +
                       "belonging to one mod - check each mod's readme for another way to reach the same thing.";

            return commands
                ? "Everything else both mods do still works. Typing the contested command runs one mod's handler, " +
                  "and which one is decided by the order UE4SS loads them in mods.txt."
                : "Everything else both mods do still works. The contested key ends up belonging to one of them - " +
                  "check each mod's readme for a console command that does the same thing.";
        }
    }

    /// The lua clashes deliberately claim no winner.
    ///
    /// The panel's winner line is worded "last loaded", which is the pak mount rule. UE4SS resolves
    /// a duplicate keybind the other way round - its own Keybinds mod skips any bind that
    /// IsKeyBindRegistered already reports, so there the FIRST registration keeps the key. Rather
    /// than print a confident answer that is backwards half the time, print none.
    /// Loose-vs-pak deliberately shows no winner. A loose file loads through a filesystem hook and
    /// a _P pak through chunk priority; which one the engine actually serves has to be seen in-game.
    /// Printing a confident guess would be worse than printing nothing.
    public bool ShowsWinner =>
        Severity != ConflictSeverity.Info
        && !IsLuaRegistrationClash
        && Kind != ConflictKind.LooseOverridesPak;
    public bool HasOverlappingTables => OverlappingTables.Count > 0;
    public bool HasCompatibleTables => CompatibleTables.Count > 0;
    public bool HasAssetPaths => AssetPaths.Count > 0;

    /// Compact single-line list of the harmlessly-shared tables, so seven of them cost one wrapped
    /// line rather than seven cards.
    public string CompatibleTablesDisplay => string.Join(", ", CompatibleTables.Select(t => t.TableName));

    public string CompatibleTablesHeader => $"Shared tables ({CompatibleTables.Count}):";

    /// Same list, two meanings - the lua kinds put registrations in AssetPaths rather than paths,
    /// so the header has to follow or the card claims a keybind is a file.
    public string AssetPathsHeader => IsLuaRegistrationClash
        ? $"Contested registrations ({AssetPaths.Count}):"
        : $"Files ({AssetPaths.Count}):";

    private bool IsLuaRegistrationClash =>
        Kind is ConflictKind.LuaConsoleCommandClash or ConflictKind.LuaKeybindClash;

    private int CountLuaEntries(string label) =>
        AssetPaths.Count(p => p.StartsWith(label, StringComparison.OrdinalIgnoreCase));

    private static string Plural(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";
}
