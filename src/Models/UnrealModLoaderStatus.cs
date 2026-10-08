namespace DDS2ModManager.Models;

/// UnrealModLoader as found on one install, plus whether this manager may install it there.
public class UnrealModLoaderStatus
{
    public bool IsInstalled { get; init; }

    /// True only when this manager installed it, so its exact file list is known and it can be
    /// updated or removed. A hand-made setup is read, never overwritten.
    public bool IsManagedByUs { get; init; }

    public string? InstalledVersion { get; init; }

    public bool CanInstall { get; init; }

    /// Why installing isn't offered, when it isn't.
    public string? InstallBlockedReason { get; init; }

    /// Advice to show before installing, e.g. that the engine version was estimated.
    public string? Warning { get; init; }

    /// Whether the card belongs on screen at all. UE5 games, which UML doesn't run on, never see it.
    public bool IsRelevant => IsInstalled || CanInstall;

    public string StatusLabel =>
        !IsInstalled ? "UnrealModLoader not installed"
        : IsManagedByUs ? $"UnrealModLoader {InstalledVersion ?? ""}".TrimEnd()
        : "UnrealModLoader installed (set up by hand)";
}

/// What this manager wrote for UnrealModLoader, so an update replaces exactly that and a removal
/// takes exactly that - never a profile or core mod the player added themselves.
public class UnrealModLoaderManifest
{
    public string Version { get; set; } = "";
    public string AssetName { get; set; } = "";
    public DateTime InstalledAt { get; set; }

    /// Relative to Binaries\Win64.
    public List<string> Files { get; set; } = [];

    /// The profile this manager wrote because UML ships none for this game, if it did.
    public string? GeneratedProfile { get; set; }
}
