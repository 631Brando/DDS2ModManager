namespace DDS2ModManager.Models;

/// Where a mod's ModUpdateUrl came from.
///
/// Worth recording rather than inferring from the mod type, because it is the difference
/// between "this mod does not offer updates" and "we could not read where its updates come
/// from" - which look identical if you only store the URL.
public enum ModUpdateSource
{
    /// No update URL - the mod never declared one.
    None,

    /// Read from the ModUpdateUrl variable on the mod's ModActor. LogicMods only; it is the
    /// ModActor that makes a mod a LogicMod in the first place.
    ModActor,

    /// Read from a .dds2mod.json manifest shipped alongside the mod. The fallback for patch
    /// mods and lua mods, which have no ModActor to carry the variable.
    Manifest
}
