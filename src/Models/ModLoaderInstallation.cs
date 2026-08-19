namespace DDS2ModManager.Models;

/// How a detected loader is arranged on disk.
public enum LoaderLayout
{
    /// Not present.
    None,

    /// UE4SS 3.1+: everything under Binaries\Win64\ue4ss\, with a proxy DLL beside the game exe.
    Modern,

    /// UE4SS 3.0.x and earlier: UE4SS.dll, UE4SS-settings.ini and Mods\ sit DIRECTLY in
    /// Binaries\Win64, mixed in with the game's own files. This is what DDS1's scene still runs.
    Legacy,

    /// A loader that is only ever a proxy DLL beside the game exe, with no folder of its own -
    /// UnrealModUnlocker is the example.
    Flat
}

/// One mod loader, as actually found in a game folder.
public class ModLoaderInstallation
{
    public required ModLoaders Loader { get; init; }
    public required LoaderLayout Layout { get; init; }

    public bool IsInstalled => Layout != LoaderLayout.None;

    /// Where this loader reads mods from, when it has such a folder.
    public string? ModsPath { get; init; }

    /// The loader's own settings file, if it has one on disk.
    public string? SettingsPath { get; init; }

    /// Where this loader loads third-party DLL plugins from, when it does that at all.
    ///
    /// Different loaders use different folders and there is no shared convention: UnrealModUnlocker
    /// reads Binaries\Win64\UnrealModPlugins, UnrealModLoader reads its coremods folder. UE4SS loads
    /// lua mods rather than arbitrary DLLs, so it has none. Null means "this loader cannot take a
    /// DLL plugin", which is a refusal reason rather than a fallback.
    public string? PluginFolder { get; init; }

    /// The folder to look in for this loader's configuration.
    ///
    /// Separate from SettingsPath because the config list enumerates the folder rather than looking
    /// the file up by name - a build that renames its settings file should still appear rather than
    /// silently vanish. Null when the loader keeps no config of its own.
    public string? ConfigFolder { get; init; }

    /// Version as the loader itself reports it, when that can be read without running it.
    public string? Version { get; init; }

    /// True only when this manager installed it, so we know exactly which build it is.
    public bool IsManagedByUs { get; init; }

    /// A folder that can be deleted outright to remove this loader — or NULL when no such folder
    /// exists, which is the important case.
    ///
    /// Under the Legacy and Flat layouts the loader's files live directly in Binaries\Win64,
    /// alongside the game's own executable and, for Legacy, alongside the user's mods. There is no
    /// folder to delete that would not also delete something that is not the loader. Callers must
    /// treat null as "no safe automatic removal" and say so, never fall back to a parent directory.
    public string? RemovableRoot { get; init; }

    /// Individual files that belong to this loader and can be removed on their own. Used instead of
    /// RemovableRoot for a Flat layout, and alongside it for a proxy DLL that sits outside the folder.
    public string[] RemovableFiles { get; init; } = [];

    /// Why this loader can't be removed automatically, when it can't. Shown to the user rather than
    /// silently disabling a button.
    public string? RemovalBlockedReason { get; init; }

    public bool CanRemoveAutomatically => RemovableRoot != null || RemovableFiles.Length > 0;

    public string DisplayName => Loader switch
    {
        ModLoaders.UE4SS => "UE4SS",
        ModLoaders.UnrealModLoader => "UnrealModLoader",
        ModLoaders.UnrealModUnlocker => "UnrealModUnlocker",
        _ => Loader.ToString()
    };
}
