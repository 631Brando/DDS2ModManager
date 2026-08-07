using System.Security.Cryptography;
using System.Text;

namespace DDS2ModManager.Services;

/// Lists and manages Unreal save games from %LocalAppData%\&lt;Project&gt;\Saved\SaveGames.
///
/// Deliberately format-agnostic: it never parses save contents, only moves/copies/deletes whole
/// saves, so it works for any UE game regardless of how that game serialises its saves. The only
/// structural assumption is the standard SaveGames layout, and both common shapes are handled -
/// a folder per save (DDS2 nests these under a "Cartels" container) and a loose .sav file.
public class SaveGameService
{
    private static readonly string[] SaveExtensions = { ".sav", ".save" };

    private readonly GameInstallation _game;
    private readonly string _disabledDir;

    public SaveGameService(GameInstallation game)
    {
        _game = game;
        // Keyed by game path so managing two different games (or two installs) can't mix their
        // disabled saves together - same reasoning as the per-game mod registry file.
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(game.RootPath.ToLowerInvariant())))[..12];
        _disabledDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DDS2ModManager", "DisabledSaves", hash);
    }

    public string SaveGamesPath => _game.SaveGamesPath;
    public string DisabledSavesPath => _disabledDir;
    public string BackupsPath => Path.Combine(Path.GetDirectoryName(_disabledDir)!, "..", "SaveBackups", Path.GetFileName(_disabledDir));
    public bool SaveFolderExists => Directory.Exists(_game.SaveGamesPath);

    /// Copies a save into a timestamped folder under %AppData%\DDS2ModManager\SaveBackups.
    ///
    /// Kept outside the game's SaveGames folder deliberately: a backup sitting next to the
    /// original would show up as a duplicate save in-game, and would be just as exposed to
    /// whatever corrupted the original. Timestamped rather than overwriting, so taking a second
    /// backup never destroys the first.
    public bool Backup(SaveEntry save)
    {
        var log = LoggingService.Instance;
        try
        {
            var root = Path.GetFullPath(BackupsPath);
            var stamped = Path.Combine(root, $"{save.Name}_{DateTime.Now:yyyyMMdd_HHmmss}");
            Directory.CreateDirectory(stamped);

            if (save.IsFolder)
            {
                foreach (var dir in Directory.GetDirectories(save.Path, "*", SearchOption.AllDirectories))
                    Directory.CreateDirectory(Path.Combine(stamped, Path.GetRelativePath(save.Path, dir)));
                foreach (var file in Directory.GetFiles(save.Path, "*", SearchOption.AllDirectories))
                {
                    var target = Path.Combine(stamped, Path.GetRelativePath(save.Path, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(file, target, true);
                }
            }
            else
            {
                File.Copy(save.Path, Path.Combine(stamped, Path.GetFileName(save.Path)), true);
            }

            log.Success($"Backed up save '{save.Name}' to {stamped}.");
            return true;
        }
        catch (Exception ex)
        {
            log.Error($"Failed to back up '{save.Name}': {ex.Message}");
            return false;
        }
    }

    public List<SaveEntry> GetSaves()
    {
        var results = new List<SaveEntry>();
        if (Directory.Exists(_game.SaveGamesPath))
            CollectFrom(_game.SaveGamesPath, group: null, isEnabled: true, results);

        if (Directory.Exists(_disabledDir))
        {
            foreach (var groupDir in Directory.GetDirectories(_disabledDir))
            {
                // Disabled saves are stored as <disabledRoot>\<group or "_root">\<save>, so the
                // original location is recoverable when re-enabling.
                var group = Path.GetFileName(groupDir);
                var groupName = group == RootGroupMarker ? null : group;
                foreach (var path in Directory.GetFileSystemEntries(groupDir))
                    results.Add(Describe(path, groupName, isEnabled: false));
            }
        }

        return results
            .OrderBy(s => s.GroupDisplay, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(s => s.LastModified)
            .ToList();
    }

    /// Marker folder name for saves that sat directly in SaveGames rather than inside a container.
    private const string RootGroupMarker = "_root";

    private void CollectFrom(string dir, string? group, bool isEnabled, List<SaveEntry> results)
    {
        foreach (var sub in Directory.GetDirectories(dir))
        {
            // A directory whose *subdirectories* hold the saves is a container (DDS2's "Cartels"),
            // not a save itself. Recursing one level here is what makes both layouts work; without
            // it, DDS2 would report a single "Cartels" entry instead of the individual saves, and
            // deleting it would wipe every save at once.
            if (Directory.GetDirectories(sub).Any(ContainsSaveFiles))
            {
                CollectFrom(sub, Path.GetFileName(sub), isEnabled, results);
                continue;
            }

            if (ContainsSaveFiles(sub))
                results.Add(Describe(sub, group, isEnabled));
        }

        // Loose save files only count at the top level. Inside a container they're shared data
        // (DDS2 keeps CartelLocalData.sav / mod data next to the save folders), not a save slot,
        // and offering to "delete" those as if they were saves would be misleading.
        if (group == null)
        {
            foreach (var file in Directory.GetFiles(dir).Where(IsSaveFile))
                results.Add(Describe(file, group: null, isEnabled));
        }
    }

    private static bool IsSaveFile(string path) =>
        SaveExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private static bool ContainsSaveFiles(string dir) =>
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Any(IsSaveFile);

    private static SaveEntry Describe(string path, string? group, bool isEnabled)
    {
        var isFolder = Directory.Exists(path);
        var files = isFolder
            ? Directory.GetFiles(path, "*", SearchOption.AllDirectories)
            : new[] { path };

        return new SaveEntry
        {
            Name = isFolder ? Path.GetFileName(path) : Path.GetFileNameWithoutExtension(path),
            Path = path,
            IsFolder = isFolder,
            GroupName = group,
            IsEnabled = isEnabled,
            FileCount = files.Length,
            SizeBytes = files.Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } }),
            LastModified = files.Length == 0
                ? Directory.GetLastWriteTime(path)
                : files.Max(f => { try { return new FileInfo(f).LastWriteTime; } catch { return DateTime.MinValue; } })
        };
    }

    /// Duplicates a save under a new name.
    ///
    /// Games commonly name a save's inner files after the save itself (DDS2 writes
    /// "&lt;SaveName&gt;_Progress.save"), so a straight folder copy produces a clone the game may not
    /// recognise. Any file whose name starts with the original save name is therefore re-prefixed
    /// to match the new one; files with unrelated names are copied untouched.
    public SaveEntry? Clone(SaveEntry save, string newName)
    {
        var log = LoggingService.Instance;
        try
        {
            if (string.IsNullOrWhiteSpace(newName))
            {
                log.Error("Enter a name for the copy.");
                return null;
            }

            if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                log.Error($"'{newName}' contains characters that aren't allowed in a file name.");
                return null;
            }

            var parent = Path.GetDirectoryName(save.Path)!;

            if (save.IsFolder)
            {
                var dest = Path.Combine(parent, newName);
                if (Directory.Exists(dest) || File.Exists(dest))
                {
                    log.Error($"A save called '{newName}' already exists.");
                    return null;
                }

                Directory.CreateDirectory(dest);
                foreach (var dir in Directory.GetDirectories(save.Path, "*", SearchOption.AllDirectories))
                    Directory.CreateDirectory(Path.Combine(dest, Path.GetRelativePath(save.Path, dir)));

                foreach (var file in Directory.GetFiles(save.Path, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(save.Path, file);
                    var fileName = Path.GetFileName(rel);
                    if (fileName.StartsWith(save.Name, StringComparison.OrdinalIgnoreCase))
                        fileName = newName + fileName[save.Name.Length..];

                    var target = Path.Combine(dest, Path.GetDirectoryName(rel) ?? "", fileName);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(file, target, true);
                }

                log.Success($"Cloned save '{save.Name}' to '{newName}'.");
                return Describe(dest, save.GroupName, isEnabled: save.IsEnabled);
            }
            else
            {
                var dest = Path.Combine(parent, newName + Path.GetExtension(save.Path));
                if (File.Exists(dest))
                {
                    log.Error($"A save called '{newName}' already exists.");
                    return null;
                }
                File.Copy(save.Path, dest);
                log.Success($"Cloned save '{save.Name}' to '{newName}'.");
                return Describe(dest, save.GroupName, isEnabled: save.IsEnabled);
            }
        }
        catch (Exception ex)
        {
            log.Error($"Failed to clone '{save.Name}': {ex.Message}");
            return null;
        }
    }

    public bool Delete(SaveEntry save)
    {
        var log = LoggingService.Instance;
        try
        {
            if (save.IsFolder) Directory.Delete(save.Path, true);
            else File.Delete(save.Path);

            log.Success($"Deleted save '{save.Name}'.");
            return true;
        }
        catch (Exception ex)
        {
            log.Error($"Failed to delete '{save.Name}': {ex.Message}");
            return false;
        }
    }

    /// Moves the save out of the game's SaveGames folder entirely, which is the only reliable way
    /// to hide it - the game enumerates whatever is in that folder, so a rename or flag wouldn't do.
    public bool SetEnabled(SaveEntry save, bool enabled)
    {
        var log = LoggingService.Instance;
        try
        {
            string destParent;
            if (enabled)
            {
                destParent = save.GroupName == null
                    ? _game.SaveGamesPath
                    : Path.Combine(_game.SaveGamesPath, save.GroupName);
            }
            else
            {
                destParent = Path.Combine(_disabledDir, save.GroupName ?? RootGroupMarker);
            }

            Directory.CreateDirectory(destParent);
            var dest = Path.Combine(destParent, Path.GetFileName(save.Path));

            if (Directory.Exists(dest) || File.Exists(dest))
            {
                log.Error($"Can't {(enabled ? "enable" : "disable")} '{save.Name}' - something already exists at {dest}.");
                return false;
            }

            if (save.IsFolder) Directory.Move(save.Path, dest);
            else File.Move(save.Path, dest);

            save.Path = dest;
            save.IsEnabled = enabled;
            log.Info(enabled
                ? $"Enabled save '{save.Name}' - moved back into the game's save folder."
                : $"Disabled save '{save.Name}' - moved out of the game's save folder so it won't show in game.");
            return true;
        }
        catch (Exception ex)
        {
            log.Error($"Failed to {(enabled ? "enable" : "disable")} '{save.Name}': {ex.Message}");
            return false;
        }
    }
}
