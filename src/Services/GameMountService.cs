using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.MappingsProvider;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.Versions;

namespace DDS2ModManager.Services;

/// One place that knows how to mount the game's Content\Paks with CUE4Parse. Three separate
/// features need this (install-time analysis, deep-scan conflict checking, and scanning for
/// pre-existing unmanaged mods) and they must all mount it identically - a difference in any
/// step here silently changes what those features can read.
public static class GameMountService
{
    /// Everything needed to read a game's paks, resolved from that game's profile and its settings.
    ///
    /// This exists because the same four values were assembled from settings in five separate places
    /// (three in MainViewModel, plus the asset search and GameResetService), each restating the
    /// DDS2 defaults inline. A game whose profile said UE4 could be read as UE5 by whichever copy
    /// somebody forgot to update - and that failure is silent, because a wrong engine version still
    /// lists every path in a pak and only fails when an asset is actually deserialized.
    public readonly record struct MountOptions(string PaksPath, string MappingsPath, EGame EGame, string? AesKeyHex);

    /// The mount settings for a game: profile first, per-game overrides on top.
    public static MountOptions OptionsFor(GameInstallation game)
    {
        var settings = AppSettingsService.Instance.ForGame(game.Profile);

        var mappings = ResolveMappings(game, settings.MappingsOverridePath);

        // The profile is the default; the setting is only ever a deliberate override.
        // Case-insensitive, and only a defined member: Enum.TryParse alone rejects "game_ue4_27" and
        // happily accepts "42", either of which a hand-edited settings file can contain.
        var egame = Enum.TryParse<EGame>(settings.EGameVersion, ignoreCase: true, out var parsed)
                    && Enum.IsDefined(parsed)
                    && !int.TryParse(settings.EGameVersion, out _)
            ? parsed
            : game.Profile.EngineVersion;

        return new MountOptions(game.PaksPath, mappings, egame, settings.AesKeyHex);
    }

    /// Which .usmap to read a game's assets with, or "" for none.
    ///
    /// One rule above all: a game is never handed another game's mappings. A wrong usmap produces
    /// no error - every path still lists - and the property reads it affects (a ModActor's update
    /// address, DataTable rows) come back as plausible garbage that looks like real results. "None"
    /// at least fails visibly. So, in order:
    ///
    ///   1. The user's override for THIS game, when the file exists.
    ///   2. The usmap compiled in for this game - DDS2's, and only DDS2's.
    ///   3. For a game with no profile of its own, a usmap UE4SS dumped into that game's folder.
    ///      UE4SS writes it from the running game, so it describes exactly this game and version.
    ///      Never done for a built-in profile: DDS1's install holds a stray 4.27 usmap left by a
    ///      manual engine-version override, and DDS2 has its own.
    ///   4. Nothing.
    public static string ResolveMappings(GameInstallation game, string? overridePath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath)) return overridePath;

        if (game.Profile.HasEmbeddedMappings) return MappingsProviderService.EnsureExtracted();

        if (game.Profile.IsBuiltIn) return "";

        var dumped = FindDumpedMappings(game);
        ReportMappingsOnce(game, dumped);
        return dumped ?? "";
    }

    /// A .usmap UE4SS generated for this game, newest first. UE4SS's dumper writes it beside itself
    /// (ue4ss\ in the current layout, Binaries\Win64 in the older one) and names it either
    /// "Mappings.usmap" or after the game and engine build.
    ///
    /// Only those two names are accepted. UE4SS names a dump after the project it came from -
    /// "DrugDealerSimulator2-5.3.2-0+UE5-....usmap" - and installing UE4SS by copying its folder over
    /// from another game is common, so "the newest .usmap here" can be another game's. That is the
    /// exact failure this whole lookup exists to avoid, and it fails silently.
    public static string? FindDumpedMappings(GameInstallation game)
    {
        var places = new[] { game.UE4SSRootPath, game.Win64Path };
        var project = game.ProjectName;

        bool IsThisGames(string path)
        {
            var name = Path.GetFileName(path);
            return name.Equals("Mappings.usmap", StringComparison.OrdinalIgnoreCase)
                   || name.StartsWith(project + "-", StringComparison.OrdinalIgnoreCase)
                   || name.StartsWith(project + ".", StringComparison.OrdinalIgnoreCase);
        }

        try
        {
            return places
                .Where(Directory.Exists)
                .SelectMany(p => Directory.EnumerateFiles(p, "*.usmap", SearchOption.TopDirectoryOnly))
                .Where(IsThisGames)
                .Where(f => new FileInfo(f).Length > 0)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static readonly HashSet<string> MappingsReported = new(StringComparer.OrdinalIgnoreCase);

    /// Says once per game per session what deep reads will be able to do, so a user wondering why a
    /// mod's update address wasn't found has the answer in the log - without it repeating on every
    /// mount.
    private static void ReportMappingsOnce(GameInstallation game, string? dumped)
    {
        lock (MappingsReported)
        {
            if (!MappingsReported.Add(AppPaths.GameKey(game.RootPath))) return;
        }

        var log = LoggingService.Instance;
        if (dumped != null)
            log.Info($"Using {Path.GetFileName(dumped)} for {game.Profile.DisplayName}'s mappings (dumped by UE4SS).");
        else if (game.Profile.NeedsMappings)
            log.Info($"No mappings (.usmap) found for {game.Profile.DisplayName}. Installing mods and detecting " +
                     "conflicts work without one; reading values inside mods (like an update address) needs one - " +
                     "UE4SS can dump it, or set one in Settings.");
    }

    public static DefaultFileProvider Mount(MountOptions options, bool warnOnMappingsFailure = false) =>
        Mount(options.PaksPath, options.MappingsPath, options.EGame, options.AesKeyHex, warnOnMappingsFailure);

    /// Mounts Content\Paks (recursively, so LogicMods is included) and returns the provider with
    /// every unencrypted archive already mounted into Files. Caller owns disposal.
    ///
    /// warnOnMappingsFailure: install-time analysis surfaces a bad mappings file to the user;
    /// background scans stay quiet about it since mappings only affect deep property parsing,
    /// not the asset-path listing all three callers actually rely on.
    public static DefaultFileProvider Mount(
        string paksPath, string mappingsPath, EGame egame, string? aesKeyHex, bool warnOnMappingsFailure = false)
    {
        // NOTE: CUE4Parse marked this 4-arg constructor obsolete in favor of one taking an explicit
        // StringComparer, but the replacement's exact parameter order varies between library versions.
        // This overload still works correctly, so we suppress the deprecation warning rather than risk
        // a signature mismatch. If you upgrade CUE4Parse and want to silence it "properly", switch to
        // the StringComparer overload your version exposes.
#pragma warning disable CS0618
        var provider = new DefaultFileProvider(paksPath, SearchOption.AllDirectories, true, new VersionContainer(egame));
#pragma warning restore CS0618

        // An empty path means this game needs no mappings at all (UE4 titles carry their own
        // property tags), which is a normal state rather than a failure worth reporting.
        if (!string.IsNullOrWhiteSpace(mappingsPath))
        {
            try { provider.MappingsContainer = new FileUsmapTypeMappingsProvider(mappingsPath); }
            catch (Exception mex)
            {
                if (warnOnMappingsFailure)
                    LoggingService.Instance.Warn($"Mappings file couldn't be loaded ({mex.Message}) - continuing without it. " +
                        "This only affects deep property parsing, not mod type detection or conflict checking.");
            }
        }

        provider.Initialize();

        if (!string.IsNullOrWhiteSpace(aesKeyHex))
        {
            // Mounts only the archives whose EncryptionKeyGuid matches this guid - irrelevant to
            // DDS2, which has no AES encryption at all, but harmless to keep for games that do.
            try { provider.SubmitKey(new CUE4Parse.UE4.Objects.Core.Misc.FGuid(), new FAesKey(aesKeyHex)); }
            catch (Exception ex) { LoggingService.Instance.Warn($"Failed to submit AES key: {ex.Message}"); }
        }

        // Initialize() only scans the directory and registers each .pak/.utoc into UnloadedVfs - it
        // never mounts anything into Files, and neither does PostMount() (that one only reconciles a
        // DefaultGame.EncryptionKeyGuid ini edge case, unrelated to normal mounting). The call that
        // actually mounts unencrypted archives into Files is Mount()/MountAsync() - SubmitKey above
        // only covers archives that need a specific AES key, which DDS2 has none of, so without this
        // call every mount produces zero files regardless of Oodle/EGame/AES being correct.
        provider.Mount();
        return provider;
    }

    /// Union of the asset paths contributed by exactly the named archives (by file name), read
    /// straight from each archive reader's own Files dictionary. NOT a diff against the rest of the
    /// mount: a path a mod legitimately overrides, or one two mods both touch, would vanish from a
    /// diff even though everything mounted and read correctly.
    public static HashSet<string> ReadArchivePaths(DefaultFileProvider provider, IEnumerable<string> archiveNames)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in archiveNames)
        {
            if (provider.TryGetArchive(name, out var archive))
                foreach (var p in archive.Files.Keys) paths.Add(p);
        }
        return paths;
    }
}
