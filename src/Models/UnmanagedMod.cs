using CommunityToolkit.Mvvm.ComponentModel;

namespace DDS2ModManager.Models;

/// A mod found sitting in the game folders that the manager's registry doesn't know about -
/// i.e. installed by hand (or by an older/other tool) before this manager was used. Observable
/// because the import dialog binds a per-row "Import" checkbox to Selected.
public partial class UnmanagedMod : ObservableObject
{
    [ObservableProperty] private bool selected = true;

    public string Name { get; set; } = "";

    /// True when this mod's files sit in a folder inside the game that only LOOKS like it disables
    /// them - Content\Paks\DisabledMods. Unreal enumerates Content\Paks recursively, so the game
    /// loads them regardless of the folder name. Importing therefore has to MOVE them out rather
    /// than just record them as disabled, and CurrentFolder alone does not survive into that
    /// decision - hence its own flag.
    public bool ParkedInGameFolder { get; set; }

    /// True for a row that represents a FOLDER of loose assets rather than one identifiable mod.
    ///
    /// Ownership of a loose .uasset cannot be recovered from disk - an overriding file at a vanilla
    /// path is byte-indistinguishable from the vanilla one, and nothing records who put it there.
    /// So the row is honest about being a folder, and Reset-to-Vanilla refuses to delete it: the
    /// manager cannot promise those files are all mod files.
    public bool IsLooseAssetGroup { get; set; }

    /// What the mod actually is, according to reading its pak with CUE4Parse (or, for lua mods,
    /// its folder layout). This is what decides where it *should* live - see IsMisplaced.
    public ModType DetectedType { get; set; }

    /// Set when the pak couldn't be read and DetectedType was inferred from the folder it sits in
    /// instead. Safe as a fallback specifically for adoption (the file is already installed and
    /// working where it is, and enable/disable will only ever put it back in that same folder),
    /// but it's surfaced in the UI so the user knows it wasn't actually verified.
    public bool TypeAssumedFromLocation { get; set; }

    /// Folder the files currently sit in.
    public string CurrentFolder { get; set; } = "";

    /// Where DetectedType says they belong. Differs from CurrentFolder only when misplaced.
    public string CorrectFolder { get; set; } = "";

    public List<string> Files { get; set; } = new();
    public List<string> ContainedAssetPaths { get; set; } = new();
    public bool HasModActor { get; set; }

    /// DataTable merges read from this mod's ModActor, captured during the same mount that
    /// identified it - so an imported mod gets row-level conflict checking straight away rather
    /// than only after the user happens to run a Deep Scan.
    public List<DataTableAppend> DataTableAppends { get; set; } = new();

    /// For lua mods, whether mods.txt currently has them switched on. Pak mods are always
    /// "enabled" if they're sitting in a game folder at all - UE loads any pak it finds.
    public bool IsEnabled { get; set; } = true;

    /// Where this mod says its updates come from, read during the same mount that identified
    /// it - so an adopted mod gets update checking straight away, exactly like an installed one.
    public ModUpdateSource? UpdateSource { get; set; }

    /// Human-readable problems found with this mod, shown in the dialog. Empty means it looks fine.
    public List<string> Issues { get; set; } = new();

    public bool IsMisplaced =>
        !string.IsNullOrEmpty(CorrectFolder) &&
        !string.Equals(
            CurrentFolder.TrimEnd(Path.DirectorySeparatorChar),
            CorrectFolder.TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    public bool HasIssues => Issues.Count > 0;

    public string IssuesDisplay => Issues.Count == 0 ? "Looks correctly installed." : string.Join("  ", Issues);

    public string TypeDisplay => TypeAssumedFromLocation ? $"{DetectedType} (assumed)" : DetectedType.ToString();
}
