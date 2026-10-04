namespace DDS2ModManager.Services;

/// Where a listed game stands.
public enum CatalogState
{
    /// A playable install the manager can open.
    Installed,

    /// The game was uninstalled but its folder is still there - UE4SS, dumps and mods outlive the
    /// game, because a launcher only removes the files it put there itself.
    Leftover,

    /// A built-in game that isn't on this PC at all. Listed anyway, so "this manager supports DDS1"
    /// is visible to someone who doesn't have it yet.
    NotFound
}

/// One game the catalog found. An INSTALL, not a game: two copies of one game are two entries,
/// keyed by <see cref="Key"/>, never by <see cref="GameProfile.Id"/> (which names the game and is
/// shared by both copies).
public sealed record DetectedGame
{
    public required string RootPath { get; init; }
    public required GameProfile Profile { get; init; }
    public required CatalogState State { get; init; }
    public string? ProjectName { get; init; }
    public StoreIdentity Identity { get; init; } = StoreIdentity.Unknown;
    public AntiCheat AntiCheat { get; init; }
    public GameArtPaths Art { get; init; } = GameArtPaths.None;
    public string? ExecutablePath { get; init; }

    /// Added through "Add game folder" rather than found in a launcher.
    public bool AddedByHand { get; init; }

    /// Which install this is, for matching entries to each other and to the open game. Built from
    /// the NORMALISED folder, so two spellings of one folder are one entry.
    ///
    /// Deliberately not what per-install files are named by. Those use AppPaths.GameKey over
    /// RootPath exactly as it was stored, and RootPath is kept in that original spelling - a
    /// remembered "D:\Games\X\" normalised to "D:\Games\X" would hash differently and that install's
    /// registry, backups and disabled mods would all silently go missing.
    public string Key => AppPaths.GameKey(GameStoreIndex.NormalizeFolder(RootPath));

    public uint SteamAppId => Identity.SteamAppId != 0 ? Identity.SteamAppId : Profile.SteamAppId;

    public GameInstallation ToInstallation() => new() { RootPath = RootPath, Profile = Profile };
}

/// Finds every installed Unreal game this manager can work on.
///
/// Sources, all read-only: every folder in every Steam library's steamapps\common (walked
/// directly, not only through Steam's manifests - an install without one is real, and the deleted
/// DDS2 on the test machine had none), every install Epic and GOG list, and every folder the user
/// has added or opened before. Each folder is classified by UnrealInstallInspector and given a
/// profile by GenericGameProfiles.Resolve - the same resolver startup and "Add game folder" use, so
/// a game can't be one thing in the list and another once opened.
///
/// Cheap per folder (directory listings and a 1 KB pak footer read), so a full scan of a large
/// library is well under a second - but it is still disk work, and callers run it off the UI thread.
public sealed class GameCatalogService
{
    /// Every game found, de-duplicated by install folder. Built-in games come first and are always
    /// present - as NotFound when absent - so the list never hides that DDS1 or DDS2 is supported.
    public IReadOnlyList<DetectedGame> Scan()
    {
        var index = GameStoreIndex.Refresh();
        var settings = AppSettingsService.Instance.Current;

        // Keyed by the normalised folder so each install is listed once however it was reached; the
        // value keeps the spelling to use as RootPath (see DetectedGame.Key for why that matters).
        var candidates = new Dictionary<string, (string Spelling, bool ByHand)>(StringComparer.OrdinalIgnoreCase);

        foreach (var lib in index.SteamLibraries)
        {
            var common = Path.Combine(lib, "steamapps", "common");
            foreach (var dir in SafeDirectories(common))
                candidates.TryAdd(GameStoreIndex.NormalizeFolder(dir), (dir, false));
        }

        foreach (var known in index.KnownInstalls)
            candidates.TryAdd(known.Key, (known.Key, false));

        // Remembered folders: anything added by hand, plus the last folder each game was opened
        // from. These win on spelling - every per-install file for them was named from exactly this
        // string. Read straight from the dictionary rather than through ForGame(), which would create
        // an empty section for every key as a side effect of merely asking.
        foreach (var (_, game) in settings.Games)
        {
            if (string.IsNullOrWhiteSpace(game.GamePathOverride)) continue;
            var folder = GameStoreIndex.NormalizeFolder(game.GamePathOverride);
            var byHand = index.Identify(folder) == null;
            candidates[folder] = (game.GamePathOverride, byHand || (candidates.TryGetValue(folder, out var c) && c.ByHand));
        }

        var found = new List<DetectedGame>();
        foreach (var (_, (spelling, byHand)) in candidates)
        {
            var entry = Inspect(spelling, index, byHand);
            if (entry != null) found.Add(entry);
        }

        // Every built-in game gets exactly one row when it has no install: its leftover folder if
        // there is one (useful to know about), else a NotFound placeholder.
        foreach (var builtIn in GameProfiles.All)
        {
            if (found.Any(g => g.State == CatalogState.Installed && g.Profile.Id == builtIn.Id)) continue;

            var leftovers = found.Where(g => g.State == CatalogState.Leftover && g.Profile.Id == builtIn.Id).ToList();
            if (leftovers.Count > 0)
            {
                // Keep one leftover row for the built-in; the rest are noise.
                foreach (var extra in leftovers.Skip(1)) found.Remove(extra);
                continue;
            }

            found.Add(new DetectedGame
            {
                RootPath = "",
                Profile = builtIn,
                State = CatalogState.NotFound,
                Art = SteamLibraryArt.Find(index.SteamRoot, builtIn.SteamAppId)
            });
        }

        return Order(found);
    }

    /// One folder, as the catalog would list it - or null when it isn't an Unreal game at all.
    /// Used for "Add game folder" and for re-checking a remembered folder at startup, so those go
    /// through exactly the same classification as a full scan.
    ///
    /// <paramref name="folder"/> is kept exactly as given for RootPath - only comparisons normalise.
    public DetectedGame? Inspect(string folder, bool addedByHand = false) =>
        Inspect(folder, GameStoreIndex.Shared, addedByHand);

    private static DetectedGame? Inspect(string folder, GameStoreIndex index, bool addedByHand)
    {
        var state = UnrealInstallInspector.Inspect(folder, out var project);
        if (state == UnrealInstallState.NotUnreal) return null;

        var identity = index.Identify(folder) ?? StoreIdentity.Unknown;
        var profile = GenericGameProfiles.Resolve(folder, project, identity, readExecutable: false);
        var appId = identity.SteamAppId != 0 ? identity.SteamAppId : profile.SteamAppId;

        var installed = state == UnrealInstallState.Installed;
        return new DetectedGame
        {
            RootPath = folder,
            Profile = profile,
            State = installed ? CatalogState.Installed : CatalogState.Leftover,
            ProjectName = project,
            Identity = identity,
            AntiCheat = installed ? UnrealInstallInspector.DetectAntiCheat(folder, project) : AntiCheat.None,
            Art = SteamLibraryArt.Find(index.SteamRoot, appId),
            ExecutablePath = project == null ? null : UnrealInstallInspector.FindGameExecutable(Path.Combine(folder, project)),
            AddedByHand = addedByHand
        };
    }

    /// Built-in games first in their series order, then everything else by name. Installed rows
    /// before leftovers within a game, so the copy you can open is the one you see first.
    public static IReadOnlyList<DetectedGame> Order(IEnumerable<DetectedGame> games) =>
        games
            .OrderByDescending(g => g.Profile.IsBuiltIn)
            .ThenBy(g => g.Profile.DisplayOrder)
            .ThenBy(g => g.Profile.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(g => g.State)
            .ToList();

    /// The install to open on a first launch with nothing remembered: a built-in game, in
    /// GameProfiles.All order (DDS2 first, as it always has been), or nothing.
    ///
    /// Deliberately never a generic game. Opening one mounts and reads every pak it has - minutes
    /// on a large install - and nothing about having it installed says the user wants to mod it.
    /// With no built-in game present the window shows the game picker instead.
    public static DetectedGame? DefaultStartupGame(IEnumerable<DetectedGame> games)
    {
        var list = games.ToList();
        foreach (var builtIn in GameProfiles.All)
        {
            var hit = list.FirstOrDefault(g => g.State == CatalogState.Installed && g.Profile.Id == builtIn.Id);
            if (hit != null) return hit;
        }

        return null;
    }

    /// The fast path for startup: only the built-in games, only in Steam libraries. A full scan
    /// walks every library folder, which is quick but not free, and startup only needs to know
    /// whether DDS1 or DDS2 is there - the full list is built in the background afterwards.
    public DetectedGame? FindBuiltInStartupGame()
    {
        var index = GameStoreIndex.Shared;
        foreach (var builtIn in GameProfiles.All)
        {
            foreach (var lib in index.SteamLibraries)
            {
                var folder = GameStoreIndex.NormalizeFolder(Path.Combine(lib, "steamapps", "common", builtIn.SteamFolderName));
                var entry = Inspect(folder, index, addedByHand: false);
                if (entry is { State: CatalogState.Installed } && entry.Profile.Id == builtIn.Id) return entry;
            }
        }

        return null;
    }

    private static IEnumerable<string> SafeDirectories(string path)
    {
        try
        {
            return Directory.Exists(path) ? Directory.GetDirectories(path) : Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
