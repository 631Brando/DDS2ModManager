namespace DDS2ModManager.Models;

/// NUMBERS ARE PINNED AND MUST NOT BE REORDERED.
///
/// ModRegistryService writes this as a NAME, but ModProfileService and ModBackupService both
/// serialise it as an INTEGER - neither passes a JsonStringEnumConverter. Inserting a member
/// anywhere but the end silently remaps every saved profile and every backup index entry already
/// on disk: a user's "LogicMod" would come back as something else, with nothing reporting a problem.
/// Append only.
public enum ModType
{
    Unknown = 0,
    PatchMod = 1,   // .pak/.ucas/.utoc, no ModActor -> Content\Paks
    LogicMod = 2,   // .pak/.ucas/.utoc, contains ModActor.uasset -> Content\Paks\LogicMods
    LuaMod = 3,     // UE4SS lua script -> Binaries\Win64\ue4ss\Mods\<Name>

    /// Loose cooked assets copied into Content\&lt;Category&gt;\, overriding what the base pak ships.
    ///
    /// How most DDS1 mods are distributed. Only loadable on a game that can prefer a file on disk
    /// over the packed copy - UnrealModUnlocker is what enables that - which is why it is gated on
    /// GameProfile.SupportsLooseAssets and impossible on an IoStore title like DDS2.
    LooseAsset = 4,

    /// A native DLL loaded by a mod loader or DLL injector, usually with a data folder beside it.
    ///
    /// DDS1's third-party frameworks ship this way - a .dll dropped into the loader's plugin folder,
    /// which then creates its own folder next to itself for settings and content. It is not a pak,
    /// not a lua script and not a cooked asset, so nothing the manager modelled before could
    /// describe it and the install was simply refused.
    DllPlugin = 5
}
