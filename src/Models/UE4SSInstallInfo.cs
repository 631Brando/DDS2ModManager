namespace DDS2ModManager.Models;

public class UE4SSInstallInfo
{
    public bool IsInstalled { get; set; }

    /// True only if this manager installed it (and therefore we know for certain it's the experimental build).
    public bool IsManagedByUs { get; set; }

    public string? InstalledVersionTag { get; set; }
    public string? InstalledAssetName { get; set; }
    public DateTime? InstalledAt { get; set; }

    /// We only ever install from experimental-latest, so this mirrors IsManagedByUs.
    public bool IsConfirmedExperimental { get; set; }

    /// Which on-disk arrangement was found. Legacy means UE4SS.dll sits directly in Binaries\Win64
    /// rather than in a ue4ss\ subfolder - the layout DDS1's scene still runs, and one this manager
    /// can read but must not try to replace or delete.
    public LoaderLayout Layout { get; set; } = LoaderLayout.None;

    /// Whether this manager may install or update UE4SS for this game at all.
    ///
    /// False on a game whose required build is not the one we can fetch. Installing anyway would not
    /// be a missing feature, it would break a working game - so the button has to be absent and
    /// explained, not merely likely to fail.
    public bool CanInstall { get; set; }

    /// Shown in place of the install button when CanInstall is false, so its absence is explained.
    public string? InstallBlockedReason { get; set; }

    /// Version string as UE4SS reports it in its own log, when there is one.
    public string? DetectedVersion { get; set; }

    /// The one-line status shown on the toolbar card.
    ///
    /// Built here rather than from XAML triggers because the honest wording depends on the GAME, not
    /// just on two booleans. The old triggers said "installed (unverified experimental)" for anything
    /// they had not installed themselves - which on DDS1 names the exact build that crashes it on
    /// startup, and implies the user should go and get it.
    public string StatusLabel
    {
        get
        {
            if (!IsInstalled)
                return CanInstall ? "UE4SS not installed" : "UE4SS not installed (managed manually)";

            // The layout IS the identifying fact here: it is what DDS1's mods expect, and it is why
            // this manager reads it but never replaces it.
            if (Layout == LoaderLayout.Legacy) return "UE4SS installed (older layout)";

            if (IsManagedByUs) return "UE4SS experimental - up to date";

            return CanInstall
                ? "UE4SS installed (unverified experimental)"
                : "UE4SS installed (unverified)";
        }
    }
}

public class UE4SSManifest
{
    public string InstalledTag { get; set; } = "";
    public string InstalledAssetName { get; set; } = "";
    public DateTime InstalledAt { get; set; }
}
