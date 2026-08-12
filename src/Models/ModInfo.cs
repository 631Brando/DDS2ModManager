using CommunityToolkit.Mvvm.ComponentModel;

namespace DDS2ModManager.Models;

/// ObservableObject (not a plain POCO) is required here: the DataGrid's Enable/Disable
/// buttons and the Status column are bound directly to properties on this class. Without
/// INotifyPropertyChanged, mutating mod.IsEnabled after install does nothing visible -
/// the UI has no way to know the value changed.
public partial class ModInfo : ObservableObject
{
    [ObservableProperty] private string id = Guid.NewGuid().ToString("N");
    [ObservableProperty] private string name = "";
    [ObservableProperty] private ModType type;
    [ObservableProperty] private bool isEnabled = true;
    [ObservableProperty] private bool isInstalled;

    /// Where the mod originally came from (archive path or source folder) - kept for reference.
    [ObservableProperty] private string sourcePath = "";

    /// Folder the mod's files currently live in (active game folder OR the disabled-cache folder).
    [ObservableProperty] private string installPath = "";

    /// Absolute paths of the actual .pak/.ucas/.utoc (or lua folder) files currently on disk for this mod.
    /// Used so Disable/Enable/Uninstall know exactly what to move or delete.
    [ObservableProperty] private List<string> installFiles = new();

    /// Virtual asset paths found inside the mod's pak (via CUE4Parse) - used for conflict detection
    /// and the "View Files" tree. For lua mods this is the list of real relative file paths instead.
    [ObservableProperty] private List<string> containedAssetPaths = new();

    [ObservableProperty] private bool hasModActor;
    [ObservableProperty] private DateTime installedAt = DateTime.Now;

    /// For LogicMods: which base-game DataTables this mod merges its own tables into at runtime,
    /// and which row keys it contributes (see DataTableAppendScanner). Captured by Deep Scan and
    /// persisted so the fast conflict check can do row-level comparison without re-mounting the
    /// game. Empty for patch mods and for logic mods that don't touch DataTables.
    [ObservableProperty] private List<DataTableAppend> dataTableAppends = new();

    /// Whether the DataTable scan has actually run for this mod. Needed to tell "we looked and it
    /// merges nothing" apart from "we never looked" - an empty DataTableAppends means both, and
    /// without this the auto-refresh below would re-scan mods that genuinely have no appends on
    /// every single launch.
    [ObservableProperty] private bool dataTableScanCompleted;

    // ---- update tracking ------------------------------------------------------------------
    //
    // Mods are downloaded from Nexus but declare their own update source: a ModUpdateUrl
    // variable on the ModActor (LogicMods) or a .dds2mod.json manifest (everything else).
    // That keeps update checks off the Nexus API entirely - no API key, no rate limit, no
    // premium gate on downloads.
    //
    // The trade is real and deliberate: an update fetched from the author's repo has NOT been
    // through Nexus's virus scanning. Hence the host allowlist in ModUpdateManifest, the fact
    // that nothing is ever installed without the user seeing the URL and the changelog, and
    // UpdateUrlChanged below.

    /// Where this mod publishes its updates. Always a github.com URL - see
    /// ModUpdateManifest.IsAllowedUpdateUrl for why anything else is rejected.
    [ObservableProperty] private string? modUpdateUrl;

    /// How ModUpdateUrl was obtained, so "declares no updates" stays distinguishable from
    /// "we could not read it".
    [ObservableProperty] private ModUpdateSource updateSource;

    /// The version currently installed, as reported by the mod itself. Free text, because it
    /// is whatever the author wrote - compared leniently, never parsed as a strict Version.
    [ObservableProperty] private string installedVersion = "";

    /// Latest version seen upstream at the last successful check. Cached so the grid can show
    /// "update available" while offline, instead of going blank whenever GitHub is unreachable.
    [ObservableProperty] private string? latestVersion;

    /// When the last SUCCESSFUL check ran. Null means never checked. Unauthenticated GitHub
    /// allows 60 requests an hour per IP, so this is what stops a user with thirty mods
    /// burning half their quota on every launch.
    [ObservableProperty] private DateTime? lastUpdateCheck;

    /// Set when a mod's declared update URL differs from the one recorded at install time.
    ///
    /// The URL is captured from the copy the user downloaded through Nexus, which was scanned.
    /// If a later version points somewhere else, that is the exact shape of a hijacked update
    /// channel, so it is surfaced and the update is not offered until the user re-confirms.
    [ObservableProperty] private bool updateUrlChanged;

    /// True when LatestVersion is newer than InstalledVersion. Computed at check time rather
    /// than derived on read - version strings are author-authored free text, and doing the
    /// comparison once where it can be logged beats re-guessing it on every grid refresh.
    [ObservableProperty] private bool updateAvailable;
}
