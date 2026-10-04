namespace DDS2ModManager.Services;

/// Builds a profile for an Unreal game this manager has no specific knowledge of.
///
/// Everything a generic profile claims is read from the install itself, and everything it can't
/// read is answered with the cautious "no":
///
///   - never installs a mod loader. The only UE4SS build this app can fetch is the experimental one
///     DDS2 needs, and it crashes other engines on startup; on a multiplayer game with anti-cheat an
///     injected DLL can also cost the player their account.
///   - no Nexus. A game's Nexus slug can't be read from its files, and a guessed one either finds
///     nothing or - worse - another game's catalogue under this game's name.
///   - no embedded mappings. The only usmap compiled in is DDS2's, and another game read against it
///     produces silent garbage, not an error.
///   - no loose assets, DLL plugins or save cloning: each needs knowledge of how this particular
///     game loads things or names its saves, and guessing wrong fails silently.
///
/// What remains is what works the same on any Unreal game: reading paks, detecting conflicts
/// between them, and installing pak mods and (where UE4SS is already present) logic and lua mods.
public static class GenericGameProfiles
{
    public const string SteamPrefix = "steam:";
    public const string EpicPrefix = "epic:";
    public const string GogPrefix = "gog:";
    public const string PathPrefix = "path:";

    /// The settings key for a game found in a launcher, or for a folder added by hand.
    ///
    /// Store ids first because they name the GAME and survive the install moving drives. A folder
    /// with no launcher behind it falls back to the per-install key every per-install file already
    /// uses, so no second hash scheme is invented. Never derived from the project folder name alone:
    /// unrelated games can share one, and before this change every Mordhau path resolved to
    /// "Engine". Always prefixed, so it can never equal a built-in id ("dds1", "dds2").
    ///
    /// These go in settings.json as dictionary KEYS only - never into a file or folder name, where
    /// the colon would be illegal on Windows.
    public static string IdFor(StoreIdentity? identity, string rootPath) => identity switch
    {
        { Store: GameStore.Steam, SteamAppId: > 0 } => SteamPrefix + identity.SteamAppId,
        { Store: GameStore.Epic, StoreId: { Length: > 0 } epic } => EpicPrefix + epic.ToLowerInvariant(),
        { Store: GameStore.Gog, StoreId: { Length: > 0 } gog } => GogPrefix + gog.ToLowerInvariant(),
        _ => PathPrefix + AppPaths.GameKey(GameStoreIndex.NormalizeFolder(rootPath))
    };

    public static bool IsGenericId(string? id) =>
        id != null && (id.StartsWith(SteamPrefix, StringComparison.OrdinalIgnoreCase)
                       || id.StartsWith(EpicPrefix, StringComparison.OrdinalIgnoreCase)
                       || id.StartsWith(GogPrefix, StringComparison.OrdinalIgnoreCase)
                       || id.StartsWith(PathPrefix, StringComparison.OrdinalIgnoreCase));

    /// Builds (and registers) the profile for one install.
    ///
    /// <paramref name="readExecutable"/> enables the exact engine version from the executable's
    /// build string. It can take up to a second on a large game, so a scan over many games leaves it
    /// off and the game the user actually opens turns it on.
    ///
    /// Reads only <paramref name="rootPath"/> and <paramref name="projectName"/> - never a
    /// GameInstallation - because GameInstallation.Profile calls this, and GameInstallation.ProjectName
    /// reads Profile. Going through either would recurse until the stack died.
    public static GameProfile Create(string rootPath, string projectName, StoreIdentity? identity, bool readExecutable)
    {
        var projectPath = Path.Combine(rootPath, projectName);
        var paks = Path.Combine(projectPath, "Content", "Paks");
        var ioStore = UnrealInstallInspector.UsesIoStore(paks);
        var engine = UnrealEngineProbe.Probe(projectPath, readExecutable);

        var name = !string.IsNullOrWhiteSpace(identity?.DisplayName) ? identity!.DisplayName! : Prettify(projectName);

        var profile = new GameProfile
        {
            Id = IdFor(identity, rootPath),
            DisplayName = name,
            ShortName = name,

            // After every built-in, so a list sorted by DisplayOrder keeps the fully supported games
            // first and the rest can be ordered by name among themselves.
            DisplayOrder = 1000,

            SteamAppId = identity?.SteamAppId ?? 0,
            SteamFolderName = Path.GetFileName(GameStoreIndex.NormalizeFolder(rootPath)),
            ProjectFolderName = projectName,
            ConfigPlatformDir = UnrealInstallInspector.ResolveConfigPlatformDir(projectPath, projectName, engine.IsUe5),
            EngineVersion = engine.Game,

            // Unversioned property serialisation is the UE5 default and an opt-in from 4.25, so a UE5
            // or IoStore game almost certainly needs a usmap for its assets' values to be readable.
            // This only decides whether to look for one and say so when there isn't any - nothing is
            // embedded for a generic game either way.
            NeedsMappings = engine.IsUe5 || ioStore,

            // Unreal's own default location. Games that save to Documents or Steam's cloud folder
            // simply show no saves, which is honest; inventing a location would not be.
            SaveSubfolders = ["SaveGames"],
            PakLayout = ioStore ? PakLayout.IoStoreTriple : PakLayout.SinglePak,

            // UE4SS's convention, which is the only loader a generic game is assumed to have.
            LogicModsUseSubfolders = true,
            SupportsDllPlugins = false,
            SupportsLooseAssets = false,
            SupportedLoaders = ModLoaders.UE4SS,
            InstallableLoaders = ModLoaders.None,
            NexusDomain = "",
            ManagerNexusModId = null,
            SupportsSaveCloning = false,
            IsBuiltIn = false,
            HasEmbeddedMappings = false,
            EngineLabel = engine.Label,
            EngineIsEstimated = !engine.IsExact
        };

        GameProfiles.Register(profile);
        return profile;
    }

    /// The profile for a folder: a built-in when the folder IS one of those games, a generic one
    /// otherwise. The single resolver every route goes through - scanning, browsing, startup - so a
    /// game can't be one thing when found and another when remembered.
    ///
    /// A built-in is claimed by Steam app id first (the strongest identity a Steam install has), then
    /// by project folder. Never the other way round from a tab or a "wanted" game: a folder that
    /// isn't DDS2 must not become DDS2 because the user clicked DDS2 before picking it, which is how
    /// another game's path used to end up in DDS2's settings and get UE4SS offered to it.
    public static GameProfile Resolve(string rootPath, string? projectName, StoreIdentity? identity, bool readExecutable)
    {
        if (identity is { SteamAppId: > 0 } && GameProfiles.BySteamAppId(identity.SteamAppId) is { } bySteam)
            return bySteam;

        if (GameProfiles.ByProjectFolder(projectName) is { } byFolder)
            return byFolder;

        return projectName == null
            ? GameProfiles.Default
            : Create(rootPath, projectName, identity, readExecutable);
    }

    /// "DrugDealerSimulator" -> "Drug Dealer Simulator", "ABInfinite" -> "AB Infinite",
    /// "Subnautica2" -> "Subnautica 2". Only used when no launcher has a proper name for the game.
    public static string Prettify(string projectName)
    {
        if (string.IsNullOrWhiteSpace(projectName)) return projectName;

        var sb = new System.Text.StringBuilder(projectName.Length + 8);
        for (var i = 0; i < projectName.Length; i++)
        {
            var c = projectName[i];
            if (i > 0)
            {
                var prev = projectName[i - 1];
                var next = i + 1 < projectName.Length ? projectName[i + 1] : '\0';

                var lowerToUpper = char.IsLower(prev) && char.IsUpper(c);
                var acronymEnd = char.IsUpper(prev) && char.IsUpper(c) && char.IsLower(next);
                var letterToDigit = char.IsLetter(prev) && char.IsDigit(c);

                if (lowerToUpper || acronymEnd || letterToDigit) sb.Append(' ');
            }

            sb.Append(c == '_' ? ' ' : c);
        }

        return System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }
}
