namespace DDS2ModManager.Services;

/// Recognises archives that are laid out by DESTINATION rather than as one mod.
///
/// A mod with both a pak half and a lua half has to put them in two different places, so its
/// archive names them:
///
///     UE4SSMods\MyMod\Scripts\main.lua        -> Binaries\Win64\ue4ss\Mods\MyMod\
///     LogicMods\MyMod\MyMod.pak               -> Content\Paks\LogicMods\MyMod\
///     INSTALL.txt
///
/// or, game-root-relative, which is just as common:
///
///     Content\Paks\LogicMods\MyMod\...
///     Binaries\Win64\ue4ss\Mods\MyMod\...
///
/// Without this, ModVariantDetectionService sees two sibling folders that are each
/// independently installable and concludes they are VARIANTS - the x2/x5/x10 pattern - so the
/// user gets asked to choose one and ends up with half a mod. That is not hypothetical: it is
/// what the release archives this project's own mods ship look like.
///
/// Returns the individual mod folders to install, each of which is then analyzed and installed
/// exactly like any other mod root. Empty when the archive is not laid out this way, which
/// leaves the existing behaviour untouched.
public static class ModArchiveLayoutService
{
    /// Directory names that mean "the thing inside me belongs in a specific game folder".
    /// Matched on the directory name alone, so both the short form (UE4SSMods\) and the
    /// game-root-relative form (Binaries\Win64\ue4ss\Mods\) are covered by the same entry.
    private static readonly string[] LuaMarkers = { "UE4SSMods", "Mods" };
    private static readonly string[] PakMarkers = { "LogicMods" };

    public static List<string> DetectParts(string extractedRoot)
    {
        var parts = new List<string>();
        if (!Directory.Exists(extractedRoot)) return parts;

        foreach (var marker in EnumerateMarkerDirectories(extractedRoot))
        {
            // The marker folder holds one folder per mod. A marker containing installable files
            // directly (no per-mod folder) is the mod itself.
            var children = Directory.GetDirectories(marker);
            var installableChildren = children.Where(ModVariantDetectionService.IsInstallableRoot).ToList();

            if (installableChildren.Count > 0) parts.AddRange(installableChildren);
            else if (ModVariantDetectionService.IsInstallableRoot(marker)) parts.Add(marker);
        }

        // One part is not a multi-destination archive in any meaningful sense - it is a normal
        // mod that happens to sit in a named folder, and the existing path handles that fine.
        // Requiring two also means a single stray folder called "Mods" cannot hijack an install.
        return parts.Count >= 2 ? parts.Distinct(StringComparer.OrdinalIgnoreCase).ToList() : new List<string>();
    }

    /// Which destination a part belongs to, from the marker folder above it.
    public static ModType KindOf(string partFolder)
    {
        var current = new DirectoryInfo(partFolder);

        // Walk up looking for a marker. The part itself may BE the marker.
        while (current != null)
        {
            if (PakMarkers.Contains(current.Name, StringComparer.OrdinalIgnoreCase)) return ModType.LogicMod;
            if (LuaMarkers.Contains(current.Name, StringComparer.OrdinalIgnoreCase)) return ModType.LuaMod;
            current = current.Parent;
        }

        return ModType.Unknown;
    }

    /// Every directory under root whose name is one of the markers. Depth-limited because an
    /// archive that happens to contain a folder called "Mods" six levels down is not declaring
    /// a destination, and a full recursive walk of a large mod is wasted work.
    private static IEnumerable<string> EnumerateMarkerDirectories(string root)
    {
        var all = LuaMarkers.Concat(PakMarkers).ToArray();

        foreach (var dir in SafeEnumerate(root, maxDepth: 5))
        {
            var name = Path.GetFileName(dir);
            if (all.Contains(name, StringComparer.OrdinalIgnoreCase)) yield return dir;
        }
    }

    private static IEnumerable<string> SafeEnumerate(string root, int maxDepth, int depth = 0)
    {
        if (depth >= maxDepth) yield break;

        string[] subs;
        try { subs = Directory.GetDirectories(root); }
        catch { yield break; }

        foreach (var s in subs)
        {
            yield return s;
            foreach (var nested in SafeEnumerate(s, maxDepth, depth + 1)) yield return nested;
        }
    }
}
