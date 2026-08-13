using System.IO;

namespace DDS2ModManager.Services;

/// What a mod's files looked like at a point in time.
///
/// Size and last-write-time per file, NOT a content hash. That is a deliberate trade: a pak mod
/// can be hundreds of megabytes, and hashing every installed mod on every launch would turn a
/// two-second startup into a disk-bound crawl for a check that runs constantly. Size plus
/// timestamp catches everything this is actually for - a file replaced by hand, a half-finished
/// copy, an update that was applied outside the manager, a mod the user edited and forgot about.
///
/// What it does NOT catch is a deliberate same-size same-timestamp substitution. That is a
/// tamper-detection problem, and this is not a tamper-detection feature: it answers "did
/// something change behind my back?", not "is this file authentic".
public class ModFileFingerprint
{
    /// Relative path -> "size:ticks". Relative so the record survives the game folder moving.
    public Dictionary<string, string> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public long TotalBytes { get; set; }
    public DateTime TakenUtc { get; set; } = DateTime.UtcNow;
}

/// Measures a mod's files on disk: how much room it takes, and whether it has changed.
public static class ModFileStateService
{
    /// Walks everything a mod owns. InstallFiles holds loose files for pak mods and a directory
    /// for lua mods, so both shapes have to be handled - a directory is expanded, a file is taken
    /// as it is.
    public static ModFileFingerprint Capture(ModInfo mod)
    {
        var print = new ModFileFingerprint();

        foreach (var entry in mod.InstallFiles)
        {
            try
            {
                if (Directory.Exists(entry))
                {
                    foreach (var file in Directory.EnumerateFiles(entry, "*", SearchOption.AllDirectories))
                        Add(print, Path.GetRelativePath(entry, file), file);
                }
                else if (File.Exists(entry))
                {
                    Add(print, Path.GetFileName(entry), entry);
                }
            }
            catch (Exception ex)
            {
                // A mod half of whose files are unreadable is worth knowing about, but not worth
                // failing the whole measurement over.
                LoggingService.Instance.Warn($"Couldn't measure part of '{mod.Name}': {ex.Message}");
            }
        }

        return print;
    }

    private static void Add(ModFileFingerprint print, string key, string fullPath)
    {
        var info = new FileInfo(fullPath);
        print.Files[key] = $"{info.Length}:{info.LastWriteTimeUtc.Ticks}";
        print.TotalBytes += info.Length;
    }

    /// What changed between the recorded state and what is on disk now.
    public record Drift(List<string> Modified, List<string> Missing, List<string> Added)
    {
        public bool Any => Modified.Count > 0 || Missing.Count > 0 || Added.Count > 0;

        public string Summary
        {
            get
            {
                var parts = new List<string>();
                if (Modified.Count > 0) parts.Add($"{Modified.Count} changed");
                if (Missing.Count > 0) parts.Add($"{Missing.Count} missing");
                if (Added.Count > 0) parts.Add($"{Added.Count} added");
                return string.Join(", ", parts);
            }
        }
    }

    /// Compares what was recorded against what is there now.
    ///
    /// A mod with no recorded fingerprint returns no drift rather than reporting everything as
    /// new - it was installed before this existed, which is not a change.
    public static Drift Compare(ModInfo mod)
    {
        var empty = new Drift(new(), new(), new());
        if (mod.Fingerprint is not { Files.Count: > 0 } recorded) return empty;

        var now = Capture(mod);

        var modified = new List<string>();
        var missing = new List<string>();

        foreach (var (path, stamp) in recorded.Files)
        {
            if (!now.Files.TryGetValue(path, out var current)) missing.Add(path);
            else if (current != stamp) modified.Add(path);
        }

        var added = now.Files.Keys.Where(k => !recorded.Files.ContainsKey(k)).ToList();

        return new Drift(modified, missing, added);
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024 / 1024:0.#} GB",
        >= 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.#} MB",
        >= 1024 => $"{bytes / 1024.0:0} KB",
        > 0 => $"{bytes} B",
        _ => ""
    };
}
