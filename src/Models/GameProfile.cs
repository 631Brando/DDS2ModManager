using CUE4Parse.UE4.Versions;

namespace DDS2ModManager.Models;

/// How a game's mod paks are laid out on disk.
public enum PakLayout
{
    /// UE4 up to 4.26: one self-contained .pak per mod, no sibling files.
    SinglePak,

    /// UE5 / IoStore: .pak + .ucas + .utoc, which only work as a set and must be
    /// installed, moved and deleted together.
    IoStoreTriple
}

/// Mod loaders this manager knows how to recognise. Flags because a game can legitimately
/// have more than one present at once - DDS1's scene pairs UnrealModLoader (pak mods) with
/// UnrealModUnlocker (loose .uasset mods), and UE4SS can be installed alongside both.
[Flags]
public enum ModLoaders
{
    None = 0,

    /// UE4SS. DDS2 requires the experimental build specifically; it also runs on DDS1,
    /// though nothing in DDS1's public mod scene uses it.
    UE4SS = 1 << 0,

    /// UnrealModLoader - loads .pak LogicMods, in-game menu on F1. UE4 only. It READS
    /// ModLoaderInfo.ini; it never writes it. Scans LogicMods flat, not recursively, and loads DLL
    /// "core mods" from Content\CoreMods.
    UnrealModLoader = 1 << 1,

    /// UnrealModUnlockerBasic - enables loading loose .uasset files from Content\.
    /// Required by most DDS1 mods; meaningless on an IoStore game.
    UnrealModUnlocker = 1 << 2
}

/// Everything that differs between the games this manager supports.
///
/// The rule for what belongs here: if it is a *value* that changes per game (a folder name, an
/// engine version, a Nexus slug) it goes in a profile. If it is a *mechanism* (how a pak is read,
/// how a conflict is detected) it stays in the services, which are already game-agnostic.
///
/// Paths are deliberately absent - those are derived from a detected install by
/// <see cref="GameInstallation"/>, which resolves the project folder from disk rather than
/// trusting <see cref="ProjectFolderName"/>.
public sealed record GameProfile
{
    /// Stable key for per-game state (settings sections, registry files, disabled-mod folders).
    /// Never change these once shipped - they are written into the user's settings.json.
    public required string Id { get; init; }

    /// Full name, for window titles and prose.
    public required string DisplayName { get; init; }

    /// Short label for the game switcher.
    public required string ShortName { get; init; }

    /// Left-to-right position in the game tab strip. Numbered in game order, so DDS1 sits left of
    /// DDS2 and the strip reads the way someone expects a series to.
    ///
    /// Deliberately NOT the order of GameProfiles.All, which decides which game a brand-new user
    /// with both installed opens on. That still favours DDS2, this tool's original target - the two
    /// are different questions and tying them together would change startup behaviour as a side
    /// effect of rearranging some tabs.
    public required int DisplayOrder { get; init; }

    public required uint SteamAppId { get; init; }

    /// Folder under steamapps\common. Note DDS1's has no spaces and DDS2's does.
    public required string SteamFolderName { get; init; }

    /// The Unreal project folder (the one holding Binaries\Win64 and Content). Only a fallback:
    /// GameInstallation detects this from disk first, so a renamed or repacked install still works.
    public required string ProjectFolderName { get; init; }

    /// Unreal's per-platform config directory under Saved\Config. UE4 writes "WindowsNoEditor";
    /// UE5 shortened it to "Windows". Getting this wrong means the manager finds no .ini files at all.
    public required string ConfigPlatformDir { get; init; }

    /// Which CUE4Parse serialisation rules apply.
    ///
    /// Beware when changing this: a wrong value still *lists* every path in the pak, because the pak
    /// index is version-agnostic. Only deserialisation fails. Validate by reading an asset, never by
    /// counting files.
    public required EGame EngineVersion { get; init; }

    /// Whether CUE4Parse needs a .usmap to read this game's assets. Unversioned property
    /// serialisation is a UE4.25+ opt-in and the UE5 default; older games carry their own property
    /// tags and need no mappings at all.
    public required bool NeedsMappings { get; init; }

    /// Folders under Saved\ that hold save games, in the order they should be presented.
    ///
    /// DDS1 splits these: Saved\SaveGames holds only a GVAS slot index and the graphics settings,
    /// while the actual playable saves are RamaSave containers in Saved\Serialized. Looking in
    /// SaveGames alone would report that the user has no saves.
    public required string[] SaveSubfolders { get; init; }

    public required PakLayout PakLayout { get; init; }

    /// Whether third-party DLL plugins are a thing on this game.
    ///
    /// True on DDS1, whose scene has frameworks that ship as a native DLL plus a data folder. False
    /// on DDS2, where UE4SS is the extension mechanism and a loose DLL has nothing to load it.
    public required bool SupportsDllPlugins { get; init; }

    /// Whether logic mods go in their own subfolder of Content\Paks\LogicMods, or flat in it.
    ///
    /// This is about the LOADER, not the engine, which is why it is its own flag rather than
    /// something derived from PakLayout - the two happen to correlate today and mean different things.
    ///
    /// UE4SS's BPModLoaderMod walks LogicMods recursively and expects LogicMods\&lt;Name&gt;\&lt;Name&gt;.pak.
    /// UnrealModLoader, which is what DDS1's scene actually uses, scans that folder **flat** with a
    /// non-recursive directory iterator filtered on .pak, and that scan is the only thing that
    /// populates its mod list. A pak in a subfolder there still MOUNTS - Unreal discovers paks under
    /// Content\Paks recursively - so its assets appear, but its ModActor is never spawned. Assets
    /// present, logic dead, nothing logged anywhere. Installing a DDS1 logic mod the DDS2 way is
    /// therefore silently broken, which is the worst shape a failure can take.
    public required bool LogicModsUseSubfolders { get; init; }

    /// Whether loose .uasset files dropped into Content\&lt;Category&gt;\ are loadable. True on UE4 with
    /// UnrealModUnlocker; impossible once a game ships as IoStore, because there is no loose-file
    /// path left for the engine to prefer.
    public required bool SupportsLooseAssets { get; init; }

    /// Loaders that are plausible for this game, so detection knows what to look for. This is not a
    /// claim about what is installed - that is resolved at runtime against the actual install.
    public required ModLoaders SupportedLoaders { get; init; }

    /// Loaders this manager may DOWNLOAD AND INSTALL. Deliberately separate from SupportedLoaders:
    /// being able to recognise a loader is not permission to install it.
    ///
    /// UE4SS is absent from DDS1's, and that is a safety rule rather than an omission. **Stock and
    /// experimental UE4SS both crash DDS1 immediately.** DDS1 gets UnrealModLoader instead, which is
    /// what its mod scene actually runs. DDS2 is the mirror image: it needs the experimental build
    /// specifically, because stock v3.0.1 crashes reading its cartel TMaps (see UE4SSStableCaveat).
    ///
    /// A generic profile computes this from the engine version it read, through
    /// LoaderCompatibility.InstallableFor - every loader's published version range.
    public required ModLoaders InstallableLoaders { get; init; }

    /// What a player must know before choosing stable UE4SS v3.0.1 on this game, when this game has
    /// something specific to say. Null falls back to LoaderCompatibility's general advice.
    public string? UE4SSStableCaveat { get; init; }

    /// Nexus Mods game domain slug, used for the mod index, the new-mod feed and browse links.
    public required string NexusDomain { get; init; }

    /// This manager's own Nexus mod id ON THIS GAME, so its row can be badged. Null when it has no
    /// page there.
    ///
    /// Per game because Nexus ids restart per game: hardcoding one meant that on the other game the
    /// same number belongs to somebody else's mod entirely, which would then be labelled as this app.
    public int? ManagerNexusModId { get; init; }

    /// Whether copying a save under a new name produces something the game will actually show.
    ///
    /// True when saves are self-describing - DDS2 gives each cartel a folder and records its own
    /// name inside it, so a renamed copy is a real, loadable save. False when the game reads a
    /// fixed set of slots from an index instead: DDS1 loads saveSlot-N.save entries listed in
    /// saveSlotsFull.sav, so a copy called anything else is simply never looked at. Cloning there
    /// would appear to succeed and then silently do nothing, which is worse than refusing.
    public required bool SupportsSaveCloning { get; init; }

    /// The file extensions that together make up ONE mod container for this game.
    ///
    /// Derived from the layout rather than stored separately, so the two can never disagree. An
    /// IoStore mod only works as a complete set, which is why installing, moving and deleting all
    /// treat these as a unit; a UE4 mod is a single self-contained .pak with no siblings, and
    /// looking for the other two there would find nothing and conclude the mod was broken.
    public string[] ContainerExtensions => PakLayout == PakLayout.IoStoreTriple
        ? [".pak", ".ucas", ".utoc"]
        : [".pak"];

    /// True for the hand-written profiles in <see cref="GameProfiles"/>; false for one built at
    /// runtime for an Unreal game this manager has no specific knowledge of.
    ///
    /// The distinction gates everything that only makes sense for a game someone actually studied:
    /// an embedded usmap, a Nexus domain, a base-pak naming rule trusted enough to delete by,
    /// installing a loader. A generic profile answers every one of those with "no".
    public bool IsBuiltIn { get; init; }

    /// Whether this game's .usmap is compiled into the app.
    ///
    /// Deliberately separate from NeedsMappings. The two used to be one flag, which meant "needs
    /// mappings" was answered by extracting the only embedded file there is - DDS2's - so any other
    /// UE5 game would have been read against DDS2's schema. That produces no error: a wrong usmap
    /// still lists every path, and only the property reads come back as plausible garbage.
    public bool HasEmbeddedMappings { get; init; }

    /// Whether this game has a Nexus Mods catalogue the manager can query.
    ///
    /// An empty domain is NOT "no filter" to the Nexus API - it was live-tested to ignore a blank
    /// gameDomainName and return every mod on the site, across all games. So this has to be checked
    /// before any request is built, never left to the server to sort out.
    public bool HasNexus => !string.IsNullOrWhiteSpace(NexusDomain);

    /// The engine version as a person would write it, e.g. "UE 4.21", for display only.
    public string EngineLabel { get; init; } = "";

    /// True when <see cref="EngineVersion"/> was inferred from the install's files rather than
    /// known. Shown as "estimated" so a user whose assets misread knows the setting to check.
    public bool EngineIsEstimated { get; init; }
}

/// The games this manager supports, and the single place a new one gets added.
public static class GameProfiles
{
    /// Drug Dealer Simulator 2 - UE 5.3.2, IoStore, UE4SS.
    public static readonly GameProfile Dds2 = new()
    {
        Id                  = "dds2",
        DisplayName         = "Drug Dealer Simulator 2",
        ShortName           = "DDS2",
        DisplayOrder        = 2,
        SteamAppId          = 1708850,
        SteamFolderName     = "Drug Dealer Simulator 2",
        ProjectFolderName   = "DrugDealerSimulator2",
        ConfigPlatformDir   = "Windows",
        EngineVersion       = EGame.GAME_UE5_3,
        NeedsMappings       = true,
        SaveSubfolders      = ["SaveGames"],
        PakLayout           = PakLayout.IoStoreTriple,
        LogicModsUseSubfolders = true,
        SupportsDllPlugins  = false,
        SupportsLooseAssets = false,
        SupportedLoaders    = ModLoaders.UE4SS,
        InstallableLoaders  = ModLoaders.UE4SS,
        UE4SSStableCaveat   = "Stable v3.0.1 crashes Drug Dealer Simulator 2 when it reads the game's cartel data. " +
                              "DDS2 needs the experimental build, and its mods are made against it.",
        NexusDomain         = "drugdealersimulator2",
        ManagerNexusModId   = 118,
        SupportsSaveCloning = true,
        IsBuiltIn           = true,
        HasEmbeddedMappings = true,
        EngineLabel         = "UE 5.3"
    };

    /// Drug Dealer Simulator 1 - UE 4.21 on its default Steam branch, UE 4.27 on another.
    ///
    /// The 4.21 here is only the default. GenericGameProfiles.ForInstall replaces it with whatever
    /// the installed executable's version resource says, so the 4.27 branch is read with 4.27's
    /// rules. The 4.21 branch's exe reads 4.21.2 and its build string ++UE4+Release-4.21-CL-4753647,
    /// and its pak is version 7 - version 8 arrived in 4.22.
    public static readonly GameProfile Dds1 = new()
    {
        Id                  = "dds1",
        DisplayName         = "Drug Dealer Simulator",
        ShortName           = "DDS1",
        DisplayOrder        = 1,
        SteamAppId          = 682990,
        SteamFolderName     = "DrugDealerSimulator",
        ProjectFolderName   = "DrugDealerSimulator",
        ConfigPlatformDir   = "WindowsNoEditor",
        EngineVersion       = EGame.GAME_UE4_21,
        NeedsMappings       = false,
        SaveSubfolders      = ["SaveGames", "Serialized"],
        PakLayout           = PakLayout.SinglePak,
        LogicModsUseSubfolders = false,
        SupportsDllPlugins  = true,
        SupportsLooseAssets = true,
        SupportedLoaders    = ModLoaders.UnrealModLoader | ModLoaders.UnrealModUnlocker | ModLoaders.UE4SS,
        InstallableLoaders  = ModLoaders.UnrealModLoader,
        NexusDomain         = "drugdealersimulator",
        SupportsSaveCloning = false,
        IsBuiltIn           = true,
        EngineLabel         = "UE 4.21"
    };

    /// Every supported game. DDS2 first: it is this tool's original target, so it stays the
    /// default for anyone whose settings predate multi-game support.
    public static readonly IReadOnlyList<GameProfile> All = [Dds2, Dds1];

    /// The profile used when nothing else is known - notably when migrating a settings file
    /// written before profiles existed, which can only ever have described DDS2.
    public static GameProfile Default => Dds2;

    /// The games in the order they should be shown to the user, left to right.
    public static IReadOnlyList<GameProfile> InDisplayOrder =>
        All.OrderBy(p => p.DisplayOrder).ToList();

    public static GameProfile? ById(string? id) =>
        All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    /// Resolves by the Unreal project folder name, which is what a detected install gives us.
    public static GameProfile? ByProjectFolder(string? projectFolder) =>
        All.FirstOrDefault(p => string.Equals(p.ProjectFolderName, projectFolder, StringComparison.OrdinalIgnoreCase));

    /// Resolves a built-in by Steam app id - the strongest identity a Steam install has, and the
    /// reason a DDS1 found through its appmanifest becomes "dds1" rather than a generic "steam:682990"
    /// whose settings would be split from the section every earlier version wrote.
    public static GameProfile? BySteamAppId(uint appId) =>
        appId == 0 ? null : All.FirstOrDefault(p => p.SteamAppId == appId);

    // ---- profiles discovered at runtime -------------------------------------------------------

    /// Generic profiles built this session, so a persisted Id can be turned back into its profile
    /// (a settings section, an exported mod profile). Never part of <see cref="All"/>: that list is
    /// pinned to the hand-written games, decides which one a new user opens on, and every caller of
    /// ById relies on it returning null for anything it doesn't specifically know.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, GameProfile> Discovered =
        new(StringComparer.OrdinalIgnoreCase);

    public static void Register(GameProfile profile)
    {
        if (profile.IsBuiltIn) return;
        Discovered[profile.Id] = profile;
    }

    /// ById, then whatever has been discovered this session. Use this where an Id from disk has to
    /// be shown to the user; use ById where only a built-in game is acceptable.
    public static GameProfile? Resolve(string? id) =>
        ById(id) ?? (id != null && Discovered.TryGetValue(id, out var p) ? p : null);
}
