namespace DDS2ModManager.Services;

/// Writes a state file so that it is always either the old version or the new one, never half of one.
///
/// File.WriteAllText truncates first and writes second, so a crash, a power cut or a full disk in
/// between leaves an empty or cut-off JSON file - and for the mod registry that reads exactly like
/// "you have no mods". Writing beside it and renaming over it closes that window: on NTFS a rename
/// within one folder replaces the target in a single step.
public static class AtomicFile
{
    public static void WriteAllText(string path, string contents)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var temp = path + ".tmp";
        File.WriteAllText(temp, contents);
        File.Move(temp, path, overwrite: true);
    }
}
