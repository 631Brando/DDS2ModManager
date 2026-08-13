using System.IO;

namespace DDS2ModManager.Services;

/// A copy of a mod's files, taken before something replaces or removes them.
public class ModBackup
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ModName { get; set; } = "";
    public ModType Type { get; set; }
    public string Version { get; set; } = "";
    public string Reason { get; set; } = "";
    public DateTime TakenUtc { get; set; } = DateTime.UtcNow;

    /// Where each file came from, so restoring puts it back where it belongs rather than
    /// guessing from the current layout.
    public Dictionary<string, string> OriginalPaths { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public long TotalBytes { get; set; }

    public string TakenDisplay => TakenUtc.ToLocalTime().ToString("d MMM yyyy, HH:mm");
    public string SizeDisplay => ModFileStateService.FormatSize(TotalBytes);
}

/// Keeps a copy of a mod before an update overwrites it, so a bad version can be put back.
///
/// The update path already downloads before it uninstalls, which protects against a failed
/// DOWNLOAD. This protects against the other case, which is more common and currently
/// unrecoverable: the download succeeded, the install succeeded, and the new version is worse -
/// it broke a save, dropped a feature, or does not work with the rest of the load order. Without
/// a copy the only route back is finding the old release yourself, and authors delete old
/// releases.
///
/// Bounded on purpose. Mod paks are large, and an unbounded backup folder inside %AppData% would
/// quietly consume a disk.
public class ModBackupService
{
    private static readonly Lazy<ModBackupService> _instance = new(() => new ModBackupService());
    public static ModBackupService Instance => _instance.Value;

    /// Keep the most recent few. Enough to undo the update you just did and the one before it,
    /// which is the realistic window in which someone notices a mod broke something.
    private const int MaxBackups = 8;

    /// Never back up something enormous. A mod this size is a content pack, restoring it is not
    /// the bottleneck, and silently eating gigabytes to protect one update is a bad trade.
    private const long MaxBytesPerBackup = 600L * 1024 * 1024;

    private readonly string _root;
    private readonly string _indexPath;
    private List<ModBackup> _backups = new();

    private ModBackupService()
    {
        _root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DDS2ModManager", "Backups");
        Directory.CreateDirectory(_root);
        _indexPath = Path.Combine(_root, "index.json");
        Load();
    }

    public IReadOnlyList<ModBackup> Backups => _backups;
    public string Folder => _root;

    public long TotalBytes => _backups.Sum(b => b.TotalBytes);

    /// Copies a mod's files aside. Returns null when there was nothing to copy, or when the mod
    /// is too large to be worth keeping - neither is an error.
    public ModBackup? Capture(ModInfo mod, string reason)
    {
        try
        {
            var print = ModFileStateService.Capture(mod);
            if (print.Files.Count == 0) return null;

            if (print.TotalBytes > MaxBytesPerBackup)
            {
                LoggingService.Instance.Info(
                    $"'{mod.Name}' is {ModFileStateService.FormatSize(print.TotalBytes)}, too large to keep a rollback " +
                    "copy of. The update will go ahead without one.");
                return null;
            }

            var backup = new ModBackup
            {
                ModName = mod.Name,
                Type = mod.Type,
                Version = mod.InstalledVersion,
                Reason = reason,
                TotalBytes = print.TotalBytes
            };

            var target = Path.Combine(_root, backup.Id);
            Directory.CreateDirectory(target);

            var index = 0;
            foreach (var entry in mod.InstallFiles)
            {
                if (Directory.Exists(entry))
                {
                    foreach (var file in Directory.EnumerateFiles(entry, "*", SearchOption.AllDirectories))
                        CopyIn(target, file, Path.Combine(Path.GetFileName(entry), Path.GetRelativePath(entry, file)), backup, ref index);
                }
                else if (File.Exists(entry))
                {
                    CopyIn(target, entry, Path.GetFileName(entry), backup, ref index);
                }
            }

            if (backup.OriginalPaths.Count == 0)
            {
                Directory.Delete(target, true);
                return null;
            }

            _backups.Insert(0, backup);
            Trim();
            Save();

            LoggingService.Instance.Info(
                $"Kept a copy of '{mod.Name}' {mod.InstalledVersion} ({backup.SizeDisplay}) in case you want to go back.");

            return backup;
        }
        catch (Exception ex)
        {
            // A failed backup must NOT stop the update. The user asked to update; not being able
            // to keep a rollback copy is worth saying, not worth refusing over.
            LoggingService.Instance.Warn($"Couldn't back up '{mod.Name}' before updating: {ex.Message}");
            return null;
        }
    }

    private static void CopyIn(string target, string source, string relative, ModBackup backup, ref int index)
    {
        var stored = $"{index++:D4}_{Path.GetFileName(source)}";
        File.Copy(source, Path.Combine(target, stored), overwrite: true);
        backup.OriginalPaths[stored] = source;
    }

    /// Puts a backed-up copy back where it came from.
    ///
    /// Restores to the ORIGINAL absolute paths recorded at capture time. If the game folder has
    /// moved since, those paths no longer exist and the restore is refused rather than scattering
    /// files into a folder that is not there any more.
    public bool Restore(ModBackup backup)
    {
        try
        {
            var source = Path.Combine(_root, backup.Id);
            if (!Directory.Exists(source))
            {
                LoggingService.Instance.Error($"The backup of '{backup.ModName}' is no longer on disk.");
                return false;
            }

            foreach (var (stored, original) in backup.OriginalPaths)
            {
                var from = Path.Combine(source, stored);
                if (!File.Exists(from)) continue;

                var folder = Path.GetDirectoryName(original);
                if (string.IsNullOrEmpty(folder))
                {
                    LoggingService.Instance.Error($"Couldn't work out where '{stored}' belongs.");
                    return false;
                }

                Directory.CreateDirectory(folder);
                File.Copy(from, original, overwrite: true);
            }

            LoggingService.Instance.Success($"Restored '{backup.ModName}' {backup.Version}.");
            return true;
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Error($"Couldn't restore '{backup.ModName}': {ex.Message}");
            return false;
        }
    }

    public void Delete(string id)
    {
        try
        {
            var folder = Path.Combine(_root, id);
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            _backups.RemoveAll(b => b.Id == id);
            Save();
        }
        catch (Exception ex) { LoggingService.Instance.Warn($"Couldn't delete a backup: {ex.Message}"); }
    }

    public void Clear()
    {
        foreach (var backup in _backups.ToList()) Delete(backup.Id);
    }

    private void Trim()
    {
        while (_backups.Count > MaxBackups)
        {
            var oldest = _backups[^1];
            _backups.RemoveAt(_backups.Count - 1);

            try
            {
                var folder = Path.Combine(_root, oldest.Id);
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
            catch { /* an orphaned folder is not worth interrupting anything for */ }
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_indexPath)) return;
            _backups = JsonSerializer.Deserialize<List<ModBackup>>(File.ReadAllText(_indexPath)) ?? new();

            // Drop entries whose folder has gone, so the list can't offer a restore that fails.
            _backups.RemoveAll(b => !Directory.Exists(Path.Combine(_root, b.Id)));
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Warn($"Couldn't read the backup index: {ex.Message}");
            _backups = new();
        }
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(_indexPath,
                JsonSerializer.Serialize(_backups, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { LoggingService.Instance.Warn($"Couldn't save the backup index: {ex.Message}"); }
    }
}
