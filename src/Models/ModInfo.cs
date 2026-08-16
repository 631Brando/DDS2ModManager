using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using DDS2ModManager.Services;

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
    // through Nexus's virus scanning. Hence GitHubUrlParser rejecting anything that isn't a
    // github.com repository, the fact that nothing is ever installed without the user seeing
    // the URL and the changelog, and UpdateUrlChanged below.
    //
    // UpdateSource is the ONE source of truth for what a mod declares. Everything derivable
    // from it below is a computed passthrough rather than a stored copy, so a re-scan that
    // replaces the source can't leave a stale URL or author behind on the row.

    /// Where this mod says its updates come from, if its author declared anywhere (a ModUpdateUrl
    /// variable on the ModActor, or a .dds2mod.json). Null means the author didn't opt in, which
    /// is the normal case and not a problem - that mod simply never gets checked.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModUpdateUrl))]
    [NotifyPropertyChangedFor(nameof(UpdateAuthor))]
    [NotifyPropertyChangedFor(nameof(InstalledVersion))]
    [NotifyPropertyChangedFor(nameof(HasUpdateSource))]
    [NotifyPropertyChangedFor(nameof(UpdateUrlChanged))]
    [NotifyPropertyChangedFor(nameof(TrustedAuthor))]
    [NotifyPropertyChangedFor(nameof(TrustLevel))]
    private ModUpdateSource? updateSource;

    /// The declared update URL as it was when this mod was installed.
    ///
    /// Captured from the copy the user downloaded through Nexus, which was scanned. If a later
    /// version points somewhere else, that is the exact shape of a hijacked update channel - see
    /// UpdateUrlChanged.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateUrlChanged))]
    [NotifyPropertyChangedFor(nameof(TrustedAuthor))]
    private string? installedUpdateUrl;

    /// Latest version seen upstream at the last successful check. Cached so the grid can show
    /// "update available" while offline, instead of going blank whenever GitHub is unreachable.
    [ObservableProperty] private string? latestVersion;

    /// When the last SUCCESSFUL check ran. Null means never checked. Unauthenticated GitHub
    /// allows 60 requests an hour per IP, so this is what stops a user with thirty mods
    /// burning half their quota on every launch.
    [ObservableProperty] private DateTime? lastUpdateCheck;

    /// True when LatestVersion is newer than InstalledVersion. Computed at check time rather
    /// than derived on read - version strings are author-authored free text, and doing the
    /// comparison once where it can be logged beats re-guessing it on every grid refresh.
    [ObservableProperty] private bool updateAvailable;

    /// Set when an update check found something newer. Cleared once the update is applied.
    [ObservableProperty] private string? availableUpdateTag;

    /// Release notes for the available update, shown before the user agrees to install it.
    [ObservableProperty] private string? availableUpdateNotes;

    /// Download URL of the asset the check identified as this update.
    ///
    /// Recorded so the row can say what it found. The install re-fetches the release and re-picks
    /// through ModUpdateService.PickAsset rather than trusting this, because a cached URL can be
    /// hours old by the time someone clicks - but it is the SAME picking rule, which is the part
    /// that used to be wrong: the install had its own inline rule and ignored the author's
    /// declared asset name.
    [ObservableProperty] private string? availableUpdateAssetUrl;

    // ---- the user's own annotations ----------------------------------------------------------
    //
    // Both persist in the registry and belong to the USER, not to the mod. Nothing here is read
    // from a mod's files, so a reinstall or an update never overwrites them.

    /// Starred, so it sorts to the top and is easy to find in a long list.
    [ObservableProperty] private bool isFavourite;

    /// The user's own note about this mod - "breaks saves after chapter 3", "needs the lua half".
    ///
    /// Free text and never parsed. This is the thing people currently keep in a text file beside
    /// the game, and the reason they keep it is that it survives being forgotten for a year.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotes))]
    private string notes = "";

    [JsonIgnore] public bool HasNotes => !string.IsNullOrWhiteSpace(Notes);

    /// Your own labels for this mod, comma separated - "qol, tested, keep".
    ///
    /// Stored as the raw string you typed rather than a parsed list, because that is what round
    /// trips through an editable cell without surprising anyone. Filtering happens through the
    /// ordinary search box: typing a tag finds every mod carrying it, so there is no second
    /// filtering mechanism to learn or to keep in step with the first.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTags))]
    private string tags = "";

    [JsonIgnore] public bool HasTags => !string.IsNullOrWhiteSpace(Tags);

    /// The tags as individual labels, trimmed and empties dropped.
    [JsonIgnore]
    public IEnumerable<string> TagList =>
        (Tags ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // ---- what this mod's files looked like -----------------------------------------------------

    /// Size and timestamp of every file, recorded at install. See ModFileStateService for why
    /// this is not a content hash.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeBytes))]
    [NotifyPropertyChangedFor(nameof(SizeDisplay))]
    private ModFileFingerprint? fingerprint;

    /// Total bytes this mod occupies, from the last fingerprint. Shown in the grid so a
    /// 900 MB mod is visible as one.
    [JsonIgnore] public long SizeBytes => Fingerprint?.TotalBytes ?? 0;

    [JsonIgnore] public string SizeDisplay => ModFileStateService.FormatSize(SizeBytes);

    /// Set when the files on disk no longer match what was recorded at install. Runtime only -
    /// it is re-derived by comparing against disk, and a stored value would go stale the moment
    /// anything changed.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDrifted))]
    [property: JsonIgnore]
    private string? driftSummary;

    [JsonIgnore] public bool HasDrifted => !string.IsNullOrEmpty(DriftSummary);

    // ---- two-part mods -----------------------------------------------------------------------
    //
    // Plenty of mods ship in two halves that install to different places: a lua script under
    // Binaries\Win64\ue4ss\Mods and a pak under Content\Paks\LogicMods. They are one mod to the
    // user and two rows here, and enabling only one half gives them something half-working with
    // no indication why.
    //
    // Computed by the ViewModel after every list change rather than stored, so it cannot go stale
    // and needs no migration - the grouping is derived from names that are already on disk.

    /// How many rows, including this one, belong to the same mod. 1 means it stands alone.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPartOfSet))]
    [property: JsonIgnore]
    private int linkedPartCount = 1;

    [JsonIgnore] public bool IsPartOfSet => LinkedPartCount > 1;

    /// This mod's entry on Nexus, when one could be identified. Drives the hover card.
    ///
    /// Runtime only, never persisted: the Nexus catalogue is already cached by NexusIndexService,
    /// so storing a copy per mod would be a second source of truth that goes stale on its own.
    /// Null is the normal case for anything unpublished, and reads as "no card", not as an error.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNexusInfo))]
    [property: JsonIgnore]
    private NexusModPost? nexusInfo;

    [JsonIgnore] public bool HasNexusInfo => NexusInfo != null;

    /// This mod's Nexus picture, once it has been fetched and decoded.
    ///
    /// Lives on the mod rather than being produced by a value converter, because the card binds
    /// Image.Source directly and Source only accepts an ImageSource - a converter that has to
    /// return something else while a download is in flight cannot satisfy it, which is what left
    /// every card pictureless on first hover.
    ///
    /// Filled in on demand when the card is about to open (MainWindow.Row_ToolTipOpening), so
    /// nothing is downloaded for mods the user never hovers.
    [ObservableProperty]
    [property: JsonIgnore]
    private System.Windows.Media.Imaging.BitmapImage? nexusThumbnail;

    // ---- derived from UpdateSource; never stored ---------------------------------------------

    /// Where this mod publishes its updates, verbatim as the author wrote it. Always a github.com
    /// address - GitHubUrlParser rejects anything else before a source is ever built.
    [JsonIgnore] public string? ModUpdateUrl => UpdateSource?.DeclaredUrl;

    /// The GitHub account that publishes this mod's updates. This is the identity trust is
    /// granted against, because whoever holds the account holds every release under it.
    [JsonIgnore] public string UpdateAuthor => UpdateSource?.Owner ?? "";

    /// The version currently installed, as reported by the mod itself. Free text, because it
    /// is whatever the author wrote - compared leniently, never parsed as a strict Version.
    [JsonIgnore] public string InstalledVersion => UpdateSource?.Version ?? "";

    [JsonIgnore] public bool HasUpdateSource => UpdateSource is { IsUsable: true };
    [JsonIgnore] public bool HasAvailableUpdate => !string.IsNullOrEmpty(AvailableUpdateTag);

    /// True when the mod's declared update URL differs from the one recorded at install time.
    ///
    /// Derived rather than stored, so it can't be left set after a re-scan corrects the source,
    /// and can't be persisted as true by an older build that recorded it wrongly.
    [JsonIgnore]
    public bool UpdateUrlChanged =>
        !string.IsNullOrWhiteSpace(InstalledUpdateUrl)
        && !string.IsNullOrWhiteSpace(ModUpdateUrl)
        && !string.Equals(InstalledUpdateUrl, ModUpdateUrl, StringComparison.OrdinalIgnoreCase);

    /// How far this mod's update source is trusted - by the user, by the maintainers' curated
    /// list, or not at all. Display only; it never decides whether the user is asked.
    [JsonIgnore]
    public ModTrustLevel TrustLevel =>
        UpdateSource == null ? ModTrustLevel.Unknown : ModTrustService.Instance.LevelFor(UpdateSource);

    /// The per-mod "Trusted" tick in the grid.
    ///
    /// Reads and writes ModTrustService, which keys trust by GitHub ACCOUNT rather than by mod.
    /// That is deliberate and it is why this isn't a stored bool: whoever controls the account
    /// controls every release under it, so trusting one of an author's mods but not another would
    /// be a distinction without a difference. Ticking one row therefore lights up that author's
    /// other mods too, which is the honest depiction of what was just granted.
    ///
    /// Trust never skips the confirmation prompt. It only changes how much that prompt has to
    /// explain. An account can be compromised and a curated list can go stale, and either of
    /// those silently installing code would be far worse than one extra click.
    ///
    /// [JsonIgnore] matters: without it, loading the registry would call the setter and silently
    /// re-grant trust that the user may since have revoked.
    [JsonIgnore]
    public bool TrustedAuthor
    {
        get => UpdateSource is { IsUsable: true } src && ModTrustService.Instance.IsTrusted(src.Owner);
        set
        {
            if (UpdateSource is not { IsUsable: true } src) return;

            // Trust cannot be granted while the update address is in dispute. The tick is disabled
            // in that state, but a binding is not a security boundary - enforce it here too.
            if (value && UpdateUrlChanged)
            {
                LoggingService.Instance.Warn(
                    $"'{Name}' can't be trusted while its update address differs from the one it was installed with.");
                OnPropertyChanged();
                return;
            }

            if (value) ModTrustService.Instance.Trust(src.Owner);
            else ModTrustService.Instance.Untrust(src.Owner);
        }
    }

    /// Re-reads the trust-derived properties. Called when ModTrustService changes, since trusting
    /// one mod's author also changes every other row by that author.
    public void RefreshTrust()
    {
        OnPropertyChanged(nameof(TrustedAuthor));
        OnPropertyChanged(nameof(TrustLevel));
    }

    /// Takes on a newly-discovered update source, pinning the address if nothing was pinned yet.
    ///
    /// Assigning UpdateSource directly is not enough, and forgetting the second half is silent:
    /// UpdateUrlChanged compares against InstalledUpdateUrl, so a mod that never pinned one can
    /// never be detected moving. The moved-address warning would simply never fire for it, and
    /// nothing would look wrong.
    ///
    /// Pinning the address the FIRST time it is seen is the strongest claim that can honestly be
    /// made for a mod discovered on disk. For a mod installed through this manager the installer
    /// pins it from the downloaded copy instead, which is a better baseline - so this only fills
    /// the gap, and never overwrites one already set.
    public void AdoptUpdateSource(ModUpdateSource source)
    {
        UpdateSource = source;

        if (string.IsNullOrWhiteSpace(InstalledUpdateUrl) && source.IsUsable)
            InstalledUpdateUrl = source.DeclaredUrl;
    }
}
