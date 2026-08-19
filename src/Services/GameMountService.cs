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

        // Mappings are only fetched for a game that needs them. DDS1 is UE 4.21, which carries its
        // own property tags, so extracting a usmap for it would be work done to be ignored - and
        // handing it DDS2's mappings would be worse than none.
        var mappings = !string.IsNullOrWhiteSpace(settings.MappingsOverridePath)
                       && File.Exists(settings.MappingsOverridePath)
            ? settings.MappingsOverridePath!
            : game.Profile.NeedsMappings ? MappingsProviderService.EnsureExtracted() : "";

        // The profile is the default; the setting is only ever a deliberate override.
        var egame = Enum.TryParse<EGame>(settings.EGameVersion, out var parsed)
            ? parsed
            : game.Profile.EngineVersion;

        return new MountOptions(game.PaksPath, mappings, egame, settings.AesKeyHex);
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
