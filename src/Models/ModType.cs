namespace DDS2ModManager.Models;

public enum ModType
{
    Unknown,
    PatchMod,   // .pak/.ucas/.utoc, no ModActor -> Content\Paks
    LogicMod,   // .pak/.ucas/.utoc, contains ModActor.uasset -> Content\Paks\LogicMods
    LuaMod      // UE4SS lua script -> Binaries\Win64\ue4ss\Mods\<Name>
}
