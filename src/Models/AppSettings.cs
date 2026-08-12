namespace DDS2ModManager.Models;

/// The two update streams the manager can follow.
///
/// Both publish real GitHub releases so the in-app updater works identically for either; the only
/// difference is that experimental builds are tagged with an "-exp" suffix and marked as
/// prereleases, which is what keeps them out of the stable channel.
public static class UpdateChannels
{
    public const string Stable = "Stable";
    public const string Experimental = "Experimental";

    public static bool IsExperimental(string? channel) =>
        string.Equals(channel, Experimental, StringComparison.OrdinalIgnoreCase);

    /// Anything unrecognised falls back to Stable - the safe default for a settings file written
    /// by a newer build, or edited by hand.
    public static string Normalize(string? channel) => IsExperimental(channel) ? Experimental : Stable;
}

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

    /// Which release channel updates come from - see UpdateChannels.
    ///
    /// Stored as a string rather than an enum so an unrecognised value from a future build
    /// degrades to the stable channel instead of throwing while loading settings.
    public string UpdateChannel { get; set; } = UpdateChannels.Stable;

    /// Optional AES-256 key (hex), only needed if CUE4Parse reports it can't decrypt a pak.
    public string? AesKeyHex { get; set; }

    /// Last window size and whether it was maximized, so the app reopens the way it was left
    /// instead of resetting to a small default every launch. Null until the first close.
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }
}
