namespace DDS2ModManager.Services;

/// Works out which mod loaders are actually present in a game folder.
///
/// The manager used to ask one question - "is UE4SS in Binaries\Win64\ue4ss?" - and treat "no" as
/// "nothing is installed, offer to install it". That is wrong in both directions once a second game
/// exists:
///
///  * DDS1's scene runs UE4SS in the OLD layout (UE4SS.dll straight in Binaries\Win64), which the
///    old check reports as absent. The Install button would then go live and drop a modern build on
///    top of a working install.
///  * DDS1 also uses UnrealModUnlocker, which the manager could not see at all, so it could not tell
///    a user that the thing most of DDS1's mods depend on was missing.
///
/// Detection is by what is on disk. What we are ALLOWED to install is a separate question, answered
/// by GameProfile.InstallableLoaders - see the note there about why DDS1 installs nothing.
public class ModLoaderService
{
    /// Every loader plausible for this game that is actually present, plus an entry describing the
    /// absence of the ones that are not, so callers can report "X is missing" without re-deriving it.
    public IReadOnlyList<ModLoaderInstallation> DetectAll(GameInstallation game)
    {
        var found = new List<ModLoaderInstallation>();

        if (game.Profile.SupportedLoaders.HasFlag(ModLoaders.UE4SS)) found.Add(DetectUE4SS(game));
        if (game.Profile.SupportedLoaders.HasFlag(ModLoaders.UnrealModLoader)) found.Add(DetectUnrealModLoader(game));
        if (game.Profile.SupportedLoaders.HasFlag(ModLoaders.UnrealModUnlocker)) found.Add(DetectUnrealModUnlocker(game));

        return found;
    }

    public ModLoaderInstallation? Detect(GameInstallation game, ModLoaders loader) =>
        DetectAll(game).FirstOrDefault(l => l.Loader == loader);

    /// UE4SS, in either of the two layouts it has shipped.
    ///
    /// The proxy DLL alone is not enough to claim UE4SS: several unrelated tools inject the same way.
    /// What identifies it is UE4SS.dll, in one of the two places it lives.
    private static ModLoaderInstallation DetectUE4SS(GameInstallation game)
    {
        var modernRoot = Path.Combine(game.Win64Path, "ue4ss");
        var modernDll = Path.Combine(modernRoot, "UE4SS.dll");
        var legacyDll = Path.Combine(game.Win64Path, "UE4SS.dll");
        var proxy = Path.Combine(game.Win64Path, "dwmapi.dll");

        if (File.Exists(modernDll) || Directory.Exists(modernRoot))
        {
            return new ModLoaderInstallation
            {
                Loader = ModLoaders.UE4SS,
                Layout = LoaderLayout.Modern,
                ModsPath = Path.Combine(modernRoot, "Mods"),
                SettingsPath = FirstExisting(Path.Combine(modernRoot, "UE4SS-settings.ini")),
                ConfigFolder = modernRoot,
                Version = ReadUE4SSVersion(Path.Combine(modernRoot, "UE4SS.log")),
                IsManagedByUs = File.Exists(Path.Combine(modernRoot, ManifestFileName)),

                // Safe: this folder holds only UE4SS's own files.
                RemovableRoot = modernRoot,
                RemovableFiles = File.Exists(proxy) ? [proxy] : []
            };
        }

        if (File.Exists(legacyDll))
        {
            return new ModLoaderInstallation
            {
                Loader = ModLoaders.UE4SS,
                Layout = LoaderLayout.Legacy,
                ModsPath = Path.Combine(game.Win64Path, "Mods"),
                SettingsPath = FirstExisting(Path.Combine(game.Win64Path, "UE4SS-settings.ini")),

                // Under this layout the settings file sits among the game's binaries. Listing that
                // folder is the only way to find it, and it is safe to offer: a UE game's Win64
                // folder holds executables and tool configs, not game settings, and the generated-
                // state filter already keeps things like imgui.ini out.
                ConfigFolder = game.Win64Path,
                Version = ReadUE4SSVersion(Path.Combine(game.Win64Path, "UE4SS.log")),

                // Deliberately NO RemovableRoot. Under this layout "UE4SS's folder" is
                // Binaries\Win64 itself - the folder holding the game's executable - and its Mods\
                // subfolder holds the user's own mods mixed in with UE4SS's built-in ones. There is
                // nothing here that can be deleted wholesale without deleting something that is not
                // UE4SS, so removal is refused and explained rather than attempted.
                RemovableRoot = null,
                RemovableFiles = [],
                RemovalBlockedReason =
                    "This is UE4SS's older layout, where its files sit directly in Binaries\\Win64 next to the " +
                    "game's own executable and your mods. There's no folder that can be removed without taking " +
                    "something else with it, so it has to be removed by hand."
            };
        }

        return Absent(ModLoaders.UE4SS);
    }

    /// UnrealModUnlocker - what lets DDS1 load loose .uasset files, which is how most DDS1 mods ship.
    ///
    /// It is only ever a renamed proxy DLL, so there is no folder and no version to read. dxgi.dll is
    /// the name it ships under; it coexists with UE4SS because the two hook different proxies.
    ///
    /// Identified by content rather than by that filename - see below for why that stopped being
    /// good enough.
    private static ModLoaderInstallation DetectUnrealModUnlocker(GameInstallation game)
    {
        var dxgi = Path.Combine(game.Win64Path, "dxgi.dll");
        if (!File.Exists(dxgi)) return Absent(ModLoaders.UnrealModUnlocker);

        // dxgi.dll is one of the most-reused proxy names there is - ReShade, Special K and half a
        // dozen overlays ship one. The filename was enough while this only ever REPORTED presence,
        // but it now decides where a native DLL plugin gets installed, and installing into some
        // other tool's folder produces a mod that silently never loads.
        //
        // So the file has to identify itself. UnrealModUnlocker's own module name is embedded in the
        // binary; when it is absent this is some other tool wearing the same filename, which is a
        // "not installed" answer rather than a lower-confidence yes.
        if (!ContainsAscii(dxgi, "UnrealModUnlocker")) return Absent(ModLoaders.UnrealModUnlocker);

        return new ModLoaderInstallation
        {
            Loader = ModLoaders.UnrealModUnlocker,
            Layout = LoaderLayout.Flat,

            // Created by the loader itself on first launch after patching, so it may not exist yet.
            PluginFolder = Path.Combine(game.Win64Path, "UnrealModPlugins"),

            // A single file we put no folder around, so it is removable on its own.
            RemovableRoot = null,
            RemovableFiles = [dxgi]
        };
    }

    /// Whether a binary contains a given ASCII marker, read in bounded chunks.
    ///
    /// Chunked because a proxy DLL is small but nothing guarantees it, and with an overlap so a
    /// marker straddling a chunk boundary is still found. An unreadable file answers "no": that
    /// direction only ever costs a report of absence, while a wrong yes picks an install target.
    private static bool ContainsAscii(string path, string marker)
    {
        var needle = System.Text.Encoding.ASCII.GetBytes(marker);

        try
        {
            using var fs = File.OpenRead(path);
            var buffer = new byte[64 * 1024];
            var carry = 0;

            while (true)
            {
                var read = fs.Read(buffer, carry, buffer.Length - carry);
                if (read == 0) return false;

                var have = carry + read;
                if (buffer.AsSpan(0, have).IndexOf(needle) >= 0) return true;

                // Keep the last (needle-1) bytes so a marker split across two reads still matches.
                carry = Math.Min(needle.Length - 1, have);
                buffer.AsSpan(have - carry, carry).CopyTo(buffer);
            }
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// UnrealModLoader - the loader DDS1's public scene uses for pak/logic mods.
    ///
    /// ModLoaderInfo.ini is correct for the AutoInjector install method - the loader reads exactly
    /// &lt;game exe dir&gt;\ModLoaderInfo.ini - but note the user hand-creates that file; the loader only
    /// ever reads it. The real limitation is the OTHER install method: launching through
    /// UnrealModLoader's own launcher writes nothing into the game folder at all, so an install done
    /// that way is undetectable from here. A false negative only means we report it absent, which is
    /// the safe direction given we never install or remove it.
    private static ModLoaderInstallation DetectUnrealModLoader(GameInstallation game)
    {
        var info = Path.Combine(game.Win64Path, "ModLoaderInfo.ini");
        if (!File.Exists(info)) return Absent(ModLoaders.UnrealModLoader);

        return new ModLoaderInstallation
        {
            Loader = ModLoaders.UnrealModLoader,
            Layout = LoaderLayout.Flat,
            PluginFolder = Path.Combine(game.Win64Path, "coremods"),
            SettingsPath = info,
            ModsPath = game.LogicModsPath,

            // Not ours to remove: we did not install it and do not know its full file set.
            RemovableRoot = null,
            RemovableFiles = [],
            RemovalBlockedReason =
                "UnrealModLoader wasn't installed by this manager and its full file list isn't known, so removing " +
                "it automatically could leave the game in a half-patched state. Remove it the way you installed it."
        };
    }

    private static ModLoaderInstallation Absent(ModLoaders loader) =>
        new() { Loader = loader, Layout = LoaderLayout.None };

    private static string? FirstExisting(string path) => File.Exists(path) ? path : null;

    /// UE4SS writes its build to the second line of its own log.
    ///
    /// Worth reading because the version STRING does not identify the build: a stock release and the
    /// experimental nightly both report "v3.0.1 Beta", and only the git SHA on that line tells them
    /// apart. That distinction matters - DDS2 needs the experimental build and DDS1 needs a custom
    /// one, and neither game runs the other's.
    public const string ManifestFileName = ".dds2modmanager_manifest.json";

    private static string? ReadUE4SSVersion(string logPath)
    {
        try
        {
            if (!File.Exists(logPath)) return null;

            foreach (var line in File.ReadLines(logPath).Take(10))
            {
                var i = line.IndexOf("UE4SS - ", StringComparison.OrdinalIgnoreCase);
                if (i >= 0) return line[(i + "UE4SS - ".Length)..].Trim();
            }
        }
        catch { /* a locked or unreadable log is not worth failing detection over */ }

        return null;
    }
}
