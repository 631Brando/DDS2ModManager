using CUE4Parse.UE4.Versions;

namespace DDS2ModManager.Services;

/// Which UE4SS release line to install from.
///
/// Experimental is the rolling "experimental-latest" tag and the one recommended everywhere: it
/// targets far more engine versions, and it carries the BPModLoaderMod fix without which Blueprint
/// mods don't load on UE 5.2 and newer. Stable is v3.0.1, published February 2024 and not updated
/// since - offered because some players want it, never preselected.
public enum UE4SSChannel
{
    Experimental = 0,
    Stable = 1
}

/// What each mod loader this manager can install actually supports, and what that means for one
/// particular game.
///
/// The ranges are the loaders' own published claims, not guesses:
///   - UE4SS experimental: "Targeting UE Versions: From 4.7 To 5.8" (README on the main branch).
///     Its LessEqual421 build definition is optional - the release notes say using it on
///     UE&lt;=4.21 "is not mandatory for UE4SS to function", it only fixes container alignment.
///   - UE4SS stable v3.0.1: "From 4.12 To 5.3" (README at the v3.0.1 tag).
///   - UnrealModLoader v2.2.1: Unreal Engine 4 only. It reads a per-game profile named after the
///     game's executable and does nothing at all without one ("Profile ... Not Detected!"), and the
///     minimal profile it ships is three engine-version flags - which is what makes writing one
///     from a detected version possible.
///
/// Knowledge about ONE game - "stable UE4SS crashes DDS2" - lives in that game's profile, not here.
public static class LoaderCompatibility
{
    public static readonly (int Major, int Minor) ExperimentalMin = (4, 7);
    public static readonly (int Major, int Minor) ExperimentalMax = (5, 8);
    public static readonly (int Major, int Minor) StableMin = (4, 12);
    public static readonly (int Major, int Minor) StableMax = (5, 3);

    /// The stable release's tag. Shown in the picker so "stable" never reads as "newer".
    public const string StableVersion = "v3.0.1";

    /// The engine version an EGame stands for. CUE4Parse encodes base versions as
    /// (major &lt;&lt; 24) | (minor &lt;&lt; 16) - see UnrealEngineProbe.ToEGame, which builds them that way.
    public static (int Major, int Minor) VersionOf(EGame game) => ((int)game >> 24, ((int)game >> 16) & 0xFF);

    /// Whether one loader choice is available for a game, and what the player should know first.
    /// Warning is advice shown beside an available choice; BlockedReason explains an unavailable one.
    public sealed record Choice(bool Available, string? BlockedReason, string? Warning);

    public static Choice ForUE4SS(GameProfile profile, UE4SSChannel channel)
    {
        var version = VersionOf(profile.EngineVersion);
        var (min, max) = channel == UE4SSChannel.Stable ? (StableMin, StableMax) : (ExperimentalMin, ExperimentalMax);
        var name = channel == UE4SSChannel.Stable ? $"Stable {StableVersion}" : "Experimental";

        // The range first, so a game outside it is told the range - the reason that actually applies -
        // rather than a bare "not installed here".
        if (Compare(version, min) < 0 || Compare(version, max) > 0)
            return new(false,
                $"{name} supports UE {min.Major}.{min.Minor} to {max.Major}.{max.Minor}, and this game is {Describe(profile)}.",
                null);

        if (!profile.InstallableLoaders.HasFlag(ModLoaders.UE4SS))
            return new(false, "This manager doesn't install UE4SS on this game.", null);

        var warnings = new List<string>();

        // Both lines are in range here, and neither is built for it. UE4SS's own notes call its
        // LessEqual421 build optional on UE 4.21 and older, but that build fixes container alignment
        // and isn't published as a download - and the standard one crashes Drug Dealer Simulator 1,
        // a 4.21 game, on startup. Warned rather than refused: on other old games it may well run.
        if (version.Major == 4 && version.Minor <= 21)
            warnings.Add("UE 4.21 and older are best served by UE4SS's LessEqual421 build, which isn't published as a " +
                         "download. The standard build can crash these games on startup - it does on Drug Dealer Simulator 1.");

        if (channel == UE4SSChannel.Stable)
        {
            if (!string.IsNullOrWhiteSpace(profile.UE4SSStableCaveat))
                warnings.Add(profile.UE4SSStableCaveat!);
            else if (version.Major >= 5)
                warnings.Add("On most UE5 games Blueprint (logic) mods need the experimental build: the Blueprint mod " +
                             "loader in stable v3.0.1 doesn't work on UE 5.2 and newer.");
        }

        if (profile.EngineIsEstimated)
            warnings.Add($"This game's engine version ({profile.EngineLabel}) was worked out from its files, not read exactly.");

        return new(true, null, warnings.Count == 0 ? null : string.Join(" ", warnings));
    }

    public static Choice ForUnrealModLoader(GameProfile profile)
    {
        if (!profile.InstallableLoaders.HasFlag(ModLoaders.UnrealModLoader))
            return new(false, "This manager doesn't install UnrealModLoader on this game.", null);

        var (major, _) = VersionOf(profile.EngineVersion);
        if (major != 4)
            return new(false, $"UnrealModLoader only supports Unreal Engine 4, and this game is {Describe(profile)}.", null);

        return new(true, null, profile.EngineIsEstimated
            ? $"This game's engine version ({profile.EngineLabel}) was worked out from its files, not read exactly, and " +
              "UnrealModLoader's profile is written from it. If mods don't load, check the version in Settings."
            : null);
    }

    /// Which loaders a game nobody wrote a profile for may have installed, from its engine version.
    ///
    /// This is read from disk like everything else a generic profile claims, so it stays inside the
    /// rule that a generic profile answers "no" to anything it can't read. What it can't know - anti-
    /// cheat, a game-specific crash - is a confirmation at install time, not a guess made here.
    public static ModLoaders InstallableFor(EGame engine)
    {
        var version = VersionOf(engine);
        var loaders = ModLoaders.None;

        if (Compare(version, ExperimentalMin) >= 0 && Compare(version, ExperimentalMax) <= 0)
            loaders |= ModLoaders.UE4SS;

        if (version.Major == 4)
            loaders |= ModLoaders.UnrealModLoader;

        return loaders;
    }

    /// The profile UnrealModLoader needs for a game it ships none for, written from the engine version.
    ///
    /// These are the three flags UML's own BasicExampleGame.profile documents; everything else it finds
    /// by pattern scanning when the profile leaves it out. The comments are UML's, so a player who
    /// opens the file to fix a flag reads the same guidance its author wrote.
    public static string BuildUnrealModLoaderProfile(int major, int minor)
    {
        var namePool = major > 4 || (major == 4 && minor >= 23);
        var chunked = major > 4 || (major == 4 && minor >= 18);
        var is422 = major == 4 && minor == 22;

        return string.Join("\r\n",
            $"# Written by {AppPaths.AppDisplayName} for a UE {major}.{minor} game, from UnrealModLoader's BasicExampleGame profile.",
            "# If mods don't load, these three flags are the first thing to check.",
            "[GameInfo]",
            "",
            "#Set to 1 (true) if the games engine version is 4.23 and up",
            $"UsesFNamePool={(namePool ? 1 : 0)}",
            "",
            "#Set to 1 (true) if the game engine version is 4.18 and up (this can vary)",
            $"IsUsingFChunkedFixedUObjectArray={(chunked ? 1 : 0)}",
            "",
            "#Fallback if Spawn Actor can't be found or refuses to work. You should almost NEVER use.",
            "IsUsingDeferedSpawn=0",
            "",
            "#UE4.22 changes the namepool weird, only set this to 1 if the game uses 4.22",
            $"IsUsing4_22={(is422 ? 1 : 0)}",
            "");
    }

    private static int Compare((int Major, int Minor) a, (int Major, int Minor) b) =>
        a.Major != b.Major ? a.Major.CompareTo(b.Major) : a.Minor.CompareTo(b.Minor);

    private static string Describe(GameProfile profile) =>
        string.IsNullOrWhiteSpace(profile.EngineLabel)
            ? $"UE {VersionOf(profile.EngineVersion).Major}.{VersionOf(profile.EngineVersion).Minor}"
            : profile.EngineLabel + (profile.EngineIsEstimated ? " (estimated)" : "");
}
