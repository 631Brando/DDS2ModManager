namespace DDS2ModManager.Services;

/// Builds a profile for an Unreal game this manager has no specific knowledge of.
///
/// Everything a generic profile claims is read from the install itself, and everything it can't
/// read is answered with the cautious "no":
///
///   - installs only the mod loaders whose published engine range covers the version read from the
///     game's files (LoaderCompatibility.InstallableFor) - UE4SS on UE 4.7-5.8, UnrealModLoader on
///     UE4. What can't be read from disk is asked instead: on a game with anti-cheat, every loader
///     install is confirmed, because an injected DLL can cost the player their account.
///   - no Nexus. A game's Nexus slug can't be read from its files, and a guessed one either finds
///     nothing or - worse - another game's catalogue under this game's name.
///   - no embedded mappings. The only usmap compiled in is DDS2's, and another game read against it
///     produces silent garbage, not an error.
///   - no loose assets, DLL plugins or save cloning: each needs knowledge of how this particular
///     game loads things or names its saves, and guessing wrong fails silently.
///
/// What remains is what works the same on any Unreal game: reading paks, detecting conflicts
/// between them, installing pak, logic and lua mods, and installing a loader to run the last two.
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

    /// The id already in use for this folder, if a different one was derived this time.
    ///
    /// The derived id depends on what the launchers say right now, and that can change between
    /// sessions: a game added by hand before Steam had written its manifest was "path:...", and is
    /// "steam:..." once the manifest exists. Switching keys would orphan everything stored under the
    /// old one - the folder, the engine override, the AES key. So when no settings section exists
    /// for the derived id but one already records this exact folder, that one keeps being used.
    public static string StableId(string derived, string rootPath)
    {
        var games = AppSettingsService.Instance.Current.Games;
        if (games.ContainsKey(derived)) return derived;

        var folder = GameStoreIndex.NormalizeFolder(rootPath);
        foreach (var (key, game) in games)
        {
            if (!IsGenericId(key) || string.IsNullOrWhiteSpace(game.GamePathOverride)) continue;
            if (string.Equals(GameStoreIndex.NormalizeFolder(game.GamePathOverride), folder, StringComparison.OrdinalIgnoreCase))
                return key;
        }

        return derived;
    }

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
            Id = StableId(IdFor(identity, rootPath), rootPath),
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

            // UE4SS's convention. ModInstallerService installs flat instead once UnrealModLoader is
            // present, because UML scans LogicMods flat and UE4SS finds a flat pak as well.
            LogicModsUseSubfolders = true,
            SupportsDllPlugins = false,
            SupportsLooseAssets = false,
            SupportedLoaders = ModLoaders.UE4SS | (engine.IsUe5 ? ModLoaders.None : ModLoaders.UnrealModLoader),
            InstallableLoaders = LoaderCompatibility.InstallableFor(engine.Game),
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
            return ForInstall(bySteam, rootPath, projectName);

        if (GameProfiles.ByProjectFolder(projectName) is { } byFolder)
            return ForInstall(byFolder, rootPath, projectName);

        return projectName == null
            ? GameProfiles.Default
            : Create(rootPath, projectName, identity, readExecutable);
    }

    /// A built-in profile with its engine-dependent values taken from THIS install.
    ///
    /// A game can ship on more than one engine at once - DDS1 has a 4.21 branch and a 4.27 one on
    /// Steam - and the profile's version is only the default. The executable's version resource says
    /// which one is installed, and a wrong one is not cosmetic: CUE4Parse reads assets with the wrong
    /// rules, and UnrealModLoader's profile is written with the wrong flags (FNamePool arrived in
    /// 4.23). Everything else - the id, the settings slot, which loaders may be installed - stays the
    /// profile's: those describe the GAME, not the build. Returns the profile itself when nothing differs.
    public static GameProfile ForInstall(GameProfile builtIn, string rootPath, string? projectName)
    {
        var projectPath = Path.Combine(rootPath, projectName ?? builtIn.ProjectFolderName);
        var exe = UnrealInstallInspector.FindGameExecutable(projectPath);
        if (exe == null || UnrealEngineProbe.ReadExeFileVersion(exe) is not { } version) return builtIn;

        var paks = Path.Combine(projectPath, "Content", "Paks");
        var mainPak = UnrealInstallInspector.FindMainPak(paks);
        if (!UnrealEngineProbe.IsConsistentWithPak(version, mainPak == null ? null : UnrealEngineProbe.ReadPakVersion(mainPak)))
            return builtIn;

        var engine = UnrealEngineProbe.ToEGame(version.Major, version.Minor);
        var ioStore = UnrealInstallInspector.UsesIoStore(paks);
        var layout = ioStore ? PakLayout.IoStoreTriple : PakLayout.SinglePak;
        if (engine == builtIn.EngineVersion && layout == builtIn.PakLayout) return builtIn;

        return builtIn with
        {
            EngineVersion = engine,
            EngineLabel = $"UE {version.Major}.{version.Minor}",
            EngineIsEstimated = false,
            PakLayout = layout,
            // Loose assets only override packed ones without IoStore.
            SupportsLooseAssets = builtIn.SupportsLooseAssets && !ioStore
        };
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
