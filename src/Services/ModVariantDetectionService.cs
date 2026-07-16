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
}
