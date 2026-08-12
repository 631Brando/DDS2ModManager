namespace DDS2ModManager.Models;

public class AppSettings
{
    /// If set, used instead of the embedded mappings.usmap - handy for testing an
    /// updated mappings file without rebuilding the exe.
    public string? MappingsOverridePath { get; set; }

    /// Name of the CUE4Parse EGame enum member used to parse packages.
    /// Bump this if the game updates to a newer engine version than this build targets.
    public string EGameVersion { get; set; } = "GAME_UE5_3";

    /// Manually pinned game folder. When set, startup skips Steam auto-detection.
    public string? GamePathOverride { get; set; }

    public bool AutoCheckUE4SSUpdatesOnStartup { get; set; } = true;

    /// "Standard" or "Dev" - which UE4SS release asset to install (see UE4SSManagerService).
    /// Remembered as the default pre-selected choice next time, not applied silently - the
    /// build picker always shows before an install/update.
    public string PreferredUE4SSBuild { get; set; } = "Standard";

    /// Checks GitHub for a newer DDS2ModManager release on startup and prompts to install it.
    public bool CheckForAppUpdatesOnStartup { get; set; } = true;

    /// Checks each installed mod's declared ModUpdateUrl for a newer release on startup.
    ///
    /// Only mods that publish an update URL are checked, results are cached for six hours, and
    /// nothing is ever downloaded without asking - see ModUpdateService.
    public bool CheckForModUpdatesOnStartup { get; set; } = true;

    /// Shows a banner when new DDS2 mods have been published on Nexus since you last looked.
    ///
    /// Read-only discovery - it lists what exists and links to the page. Nothing is downloaded,
    /// and no Nexus account or API key is involved.
    public bool ShowNexusNewModBanner { get; set; } = true;

    /// Newest mod publish time already shown in the banner. Everything after this is "new".
    /// Null on first run, which starts the window at two weeks back rather than dumping the
    /// entire history of the game's mod list into a banner.
    public DateTime? NexusFeedLastSeenUtc { get; set; }

    /// Installs updates for mods marked TrustedAuthor without showing the confirmation dialog.
    ///
    /// OFF by default, and it should stay that way unless someone deliberately turns it on.
    /// These updates come from the author's own repository rather than Nexus, so they have not
    /// been virus scanned - silently running unscanned code is a decision a user has to make
    /// explicitly, not one they inherit by ticking "trust" on a single mod.
    ///
    /// Even with this on, a mod whose update address has CHANGED since install still prompts.
    public bool AutoInstallTrustedModUpdates { get; set; } = false;

    /// Optional AES-256 key (hex), only needed if CUE4Parse reports it can't decrypt a pak.
    public string? AesKeyHex { get; set; }

    /// Last window size and whether it was maximized, so the app reopens the way it was left
    /// instead of resetting to a small default every launch. Null until the first close.
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }
}
