namespace DDS2ModManager.Services;

/// Some mods ship several complete, independent copies of themselves in one archive -
/// e.g. a folder each for "x2", "x5", "x10", "x20", "x50" damage multipliers - where the
/// user is meant to install exactly one. If we just copied "the first .pak we find" in
/// that situation, InstallPakTriple would end up mixing files from different variants.
/// This walks the extracted archive and reports back when that pattern is detected so the
/// caller can ask the user to pick one before anything gets installed.
public static class ModVariantDetectionService
{
    /// Returns the list of folders the user should choose between, or a single-item list
    /// (the original root) when there's nothing to choose - i.e. the normal case.
    public static List<string> DetectCandidates(string extractedRoot)
    {
        var subdirs = Directory.GetDirectories(extractedRoot);

        var qualifying = subdirs.Where(IsInstallableRoot).ToList();

        // 2+ sibling folders that are each independently a complete, installable mod on
        // their own -> ask the user which one they want.
        if (qualifying.Count >= 2)
            return qualifying;

        // Anything else (loose files directly in root, a single wrapper folder, or a
        // legitimately-nested structure) - just use the root as-is, same as before.
        return new List<string> { extractedRoot };
    }

    public static bool IsInstallableRoot(string dir)
    {
        var hasPak = Directory.GetFiles(dir, "*.pak", SearchOption.AllDirectories).Any();
        if (hasPak) return true;

        return Directory.GetDirectories(dir, "Scripts", SearchOption.AllDirectories)
            .Any(s => File.Exists(Path.Combine(s, "main.lua")));
    }

    /// The two HALVES of one mod, shipped as siblings named after the MOD rather than after their
    /// destinations - "EddieWiki" beside "EddieWiki_P" - or empty.
    ///
    /// ModArchiveLayoutService already recognises an archive that names its destinations
    /// (UE4SSMods\, LogicMods\). This is the same statement made by naming the folders after the
    /// mod instead, which is what a real user hit: both halves qualify as installable roots, so
    /// DetectCandidates counted them as variants and asked which ONE to install. Either answer
    /// gives half a mod - a script half calling into a pak that was never installed - and nothing
    /// on screen says so.
    ///
    /// Telling halves from variants needs BOTH tests below, and neither is sufficient alone:
    ///
    ///   - Name alone loses. "MyMod" beside "MyMod_P" where BOTH carry a pak is the _P
    ///     load-priority convention this app already models elsewhere: two alternatives bound for
    ///     ONE folder. Installing both mounts two copies of the same mod, which is the file
    ///     mixing this file exists to prevent.
    ///   - Kind alone loses. A pak variant folder beside an unrelated lua helper folder has two
    ///     kinds and is emphatically not one mod.
    ///
    /// PartKind is total over what IsInstallableRoot admits and has exactly two values, so "all
    /// kinds distinct" caps the set at two by pigeonhole - no archive with 3+ installable siblings
    /// can pass, whatever its folders are called. That cap is a CONSEQUENCE, never a rule of its
    /// own: a two-folder variant set ("Normal" beside "Hardcore") is ordinary, so counting to two
    /// would install both. **If IsInstallableRoot ever grows DDS1's loose-asset or DLL-plugin
    /// shapes, the cap disappears and this rule has to be re-derived in the same commit.**
    public static List<string> DetectTwoPartSiblings(string extractedRoot)
    {
        var none = new List<string>();

        // ModArchiveLayoutService.DetectParts guards this; DetectCandidates does not and throws.
        // This path must not inherit that.
        if (!Directory.Exists(extractedRoot)) return none;

        var qualifying = Directory.GetDirectories(extractedRoot).Where(IsInstallableRoot).ToList();
        if (qualifying.Count < 2) return none;

        // (1) One mod identity, taken from the FOLDER name.
        //
        // Deliberately the same reduction the mod list uses to group installed rows, so what the
        // installer calls "one mod" and what the grid calls "one mod" cannot disagree.
        //
        // Folder names, never InferModName's output: only one half of the reported archive carries
        // the manifest that names the mod, so inferred names do not match on the very archive this
        // exists for.
        var keys = qualifying
            .Select(d => NexusModMatcher.KeyForInstalled(
                Path.GetFileName(d.TrimEnd(Path.DirectorySeparatorChar))))
            .ToList();

        if (keys.Any(k => k.Length == 0)) return none;                      // punctuation-only names
        if (keys.Distinct(StringComparer.Ordinal).Count() != 1) return none;

        // (2) Every part goes somewhere different. Two folders bound for the same place are
        // alternatives however they are named.
        //
        // Distinct-count must EQUAL member-count, not merely be 2 or more: ">= 2" would pass
        // {pak, pak, lua} and mix two paks.
        var kinds = qualifying.Select(PartKindOf).ToList();
        if (kinds.Distinct().Count() != kinds.Count) return none;

        return qualifying;
    }

    /// What a folder would install AS, at the coarse level that decides its destination FAMILY.
    ///
    /// Not a destination: a pak part still resolves to a LogicMod or a PatchMod on the ModActor
    /// test, which needs a CUE4Parse mount and cannot run before the user has chosen anything.
    /// Two pak siblings are therefore refused above rather than split.
    private enum PartKind { Pak, Lua }

    /// Pak beats lua, matching ModAnalyzerService's own precedence - it requires no pak at all
    /// before it will call something a lua mod - and IsInstallableRoot's ordering above. A naive
    /// "has lua ? Lua : Pak" would mistype a folder holding both, which is the one way this could
    /// drop files.
    private static PartKind PartKindOf(string dir) =>
        Directory.GetFiles(dir, "*.pak", SearchOption.AllDirectories).Any() ? PartKind.Pak : PartKind.Lua;
}
