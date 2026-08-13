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

    /// Checks each installed mod's declared update source for a newer release on startup.
    ///
    /// Only mods that publish an update address are checked, results are cached for six hours,
    /// and nothing is ever downloaded without asking - see ModUpdateService.
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

    /// Shows a mod's Nexus picture and description when you hover its row.
    ///
    /// The details come from a list of the game's mods fetched from Nexus and cached locally, so
    /// hovering does no network work of its own and the cards keep working offline. Turning this
    /// off stops the manager contacting Nexus for mod details at all.
    public bool ShowNexusModDetails { get; set; } = true;

    /// When the cached Nexus mod list was last refreshed. The list is small and changes slowly,
    /// so it is re-fetched on a slow cadence rather than on every launch.
    public DateTime? NexusIndexRefreshedUtc { get; set; }

    /// How the mod list was last sorted, so it comes back the way it was left.
    ///
    /// Stored as the property name the grid sorts on rather than a column index, so reordering or
    /// adding a column can't silently start sorting by something else. An unrecognised name is
    /// ignored and the default (starred first, then name) applies.
    public string? ModListSortColumn { get; set; }
    public bool ModListSortDescending { get; set; }

    /// What the game's executable looked like last run, so a patch can be noticed. See
    /// GameVersionWatchService for why size and timestamp are kept alongside the version.
    public string? LastSeenGameVersion { get; set; }
    public long LastSeenGameSize { get; set; }
    public DateTime? LastSeenGameWrittenUtc { get; set; }

    // There is deliberately NO "install trusted updates automatically" setting.
    //
    // An earlier revision had one, off by default. It was dropped rather than kept behind that
    // default, because the thing it would skip is the only place a user ever sees where an
    // update came from. A mod update is executable content from the author's own repository
    // rather than from Nexus, so it has not been virus scanned, and a lua mod runs code in the
    // game's process. An author's account can be compromised and the curated verified list can
    // go stale; either of those silently installing code would be far worse than one click.
    //
    // Trust (see ModTrustService) therefore changes how much the prompt has to EXPLAIN, never
    // whether there is one.

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
