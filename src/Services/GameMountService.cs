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

        try { provider.MappingsContainer = new FileUsmapTypeMappingsProvider(mappingsPath); }
        catch (Exception mex)
        {
            if (warnOnMappingsFailure)
                LoggingService.Instance.Warn($"Mappings file couldn't be loaded ({mex.Message}) - continuing without it. " +
                    "This only affects deep property parsing, not mod type detection or conflict checking.");
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
