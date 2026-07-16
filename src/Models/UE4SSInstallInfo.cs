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
}

public class UE4SSManifest
{
    public string InstalledTag { get; set; } = "";
    public string InstalledAssetName { get; set; } = "";
    public DateTime InstalledAt { get; set; }
}
