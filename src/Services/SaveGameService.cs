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
        _disabledDir = AppPaths.DisabledSavesFor(game.RootPath);
    }

    /// The root a save with no recorded RootName belongs to. First entry of the profile's list.
    private string PrimaryRoot => _game.Profile.SaveSubfolders.FirstOrDefault() ?? "SaveGames";

    private bool IsPrimaryRoot(string? rootName) =>
        string.IsNullOrEmpty(rootName) || rootName.Equals(PrimaryRoot, StringComparison.OrdinalIgnoreCase);

    private string RootPathFor(string? rootName) =>
        Path.Combine(_game.SavedPath, IsPrimaryRoot(rootName) ? PrimaryRoot : rootName!);

    private const char RootQualifierSeparator = '#';

    /// The folder a disabled save is parked in, beneath _disabledDir.
    ///
    /// The primary root keeps the historical name unchanged, so a save disabled by an earlier build
    /// still re-enables correctly. Only an additional root gets a qualified name - without it a save
    /// taken out of Serialized would be restored into SaveGames, a folder the game never reads for
    /// playthroughs, so the save would look lost.
    ///
    /// NOTE: the qualifier goes in the GROUP segment, never into _disabledDir itself. BackupsPath is
    /// derived from _disabledDir by walking up a level, so adding a segment there would silently
    /// relocate every save backup.
    private string DisabledKeyFor(string? rootName, string? group)
    {
        var g = group ?? RootGroupMarker;
        return IsPrimaryRoot(rootName) ? g : rootName + RootQualifierSeparator + g;
    }

    private (string? Root, string? Group) ParseDisabledKey(string key)
    {
        var i = key.IndexOf(RootQualifierSeparator);
        if (i > 0)
        {
            var root = key[..i];
            // Only treat it as qualified when the prefix really is one of this game's save roots,
            // so a container whose name happens to contain the separator is not misread.
            if (_game.Profile.SaveSubfolders.Contains(root, StringComparer.OrdinalIgnoreCase))
            {
                var g = key[(i + 1)..];
                return (root, g == RootGroupMarker ? null : g);
            }
        }
        return (null, key == RootGroupMarker ? null : key);
    }

    public string SaveGamesPath => _game.SaveGamesPath;
    public string DisabledSavesPath => _disabledDir;
    public string BackupsPath => Path.Combine(Path.GetDirectoryName(_disabledDir)!, "..", "SaveBackups", Path.GetFileName(_disabledDir));
    /// True when the game has any save root on disk. Checks them all: DDS1's playthroughs live in
    /// Serialized, so keying this on SaveGames alone could report "no saves yet" to someone with a
    /// folder full of them.
    public bool SaveFolderExists => _game.SaveRootPaths.Any(Directory.Exists);

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

        // Every root the game uses, not just SaveGames. DDS1 keeps only a slot index and the
        // graphics settings there; the actual playthroughs live in Saved\Serialized, so looking at
        // one folder would tell a DDS1 player they have no saves at all.
        foreach (var rootName in _game.Profile.SaveSubfolders)
        {
            var rootPath = Path.Combine(_game.SavedPath, rootName);
            if (Directory.Exists(rootPath))
                CollectFrom(rootPath, group: null, isEnabled: true, results, rootName);
        }

        if (Directory.Exists(_disabledDir))
        {
            foreach (var groupDir in Directory.GetDirectories(_disabledDir))
            {
                // Disabled saves are stored as <disabledRoot>\<key>\<save>, where the key records the
                // container and, for a non-primary root, which root it came out of - so the original
                // location is fully recoverable when re-enabling.
                var (root, groupName) = ParseDisabledKey(Path.GetFileName(groupDir));
                foreach (var path in Directory.GetFileSystemEntries(groupDir))
                    results.Add(Describe(path, groupName, isEnabled: false, root));
            }
        }

        return results
            .OrderBy(s => s.GroupDisplay, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(s => s.LastModified)
            .ToList();
    }

    /// Marker folder name for saves that sat directly in SaveGames rather than inside a container.
    private const string RootGroupMarker = "_root";

    private void CollectFrom(string dir, string? group, bool isEnabled, List<SaveEntry> results, string? rootName)
    {
        foreach (var sub in Directory.GetDirectories(dir))
        {
            // A directory whose *subdirectories* hold the saves is a container (DDS2's "Cartels"),
            // not a save itself. Recursing one level here is what makes both layouts work; without
            // it, DDS2 would report a single "Cartels" entry instead of the individual saves, and
            // deleting it would wipe every save at once.
            if (Directory.GetDirectories(sub).Any(ContainsSaveFiles))
            {
                CollectFrom(sub, Path.GetFileName(sub), isEnabled, results, rootName);
                continue;
            }

            if (ContainsSaveFiles(sub))
                results.Add(Describe(sub, group, isEnabled, rootName));
        }

        // Loose save files only count at the top level. Inside a container they're shared data
        // (DDS2 keeps CartelLocalData.sav / mod data next to the save folders), not a save slot,
        // and offering to "delete" those as if they were saves would be misleading.
        if (group == null)
        {
            foreach (var file in Directory.GetFiles(dir).Where(IsSaveFile))
                results.Add(Describe(file, group: null, isEnabled, rootName));
        }
    }

    private static bool IsSaveFile(string path) =>
        SaveExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private static bool ContainsSaveFiles(string dir) =>
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Any(IsSaveFile);

    private SaveEntry Describe(string path, string? group, bool isEnabled, string? rootName = null)
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
            // Left empty for the primary root, so nothing about the ordinary single-root case changes.
            RootName = IsPrimaryRoot(rootName) ? "" : rootName!,
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
    /// A straight folder copy produces a clone the game won't load, for two separate reasons:
    ///
    ///   1. Games commonly name a save's inner files after the save itself (DDS2 writes
    ///      "&lt;SaveName&gt;_Progress.save"). Any file whose name starts with the original save name
    ///      is therefore re-prefixed to match the new one; unrelated names are copied untouched.
    ///   2. A save also records its own name *inside* itself. DDS2 keeps it in
    ///      CartelDefaults.sav and uses it to find the progress file, so a copy that still names
    ///      the original looks for a file that isn't in its folder and is silently skipped -
    ///      the clone simply never appears in the game's load list. GvasNameRewriter fixes those
    ///      references up.
    public SaveEntry? Clone(SaveEntry save, string newName)
    {
        var log = LoggingService.Instance;
        try
        {
            if (!_game.Profile.SupportsSaveCloning)
            {
                // Refused rather than attempted. The copy would be written successfully and then
                // never appear in game, because this game loads a fixed set of slots named in an
                // index rather than whatever files it finds - so "it worked" would be a lie the
                // user only discovers when they go looking for the save later.
                log.Error(
                    $"{_game.Profile.DisplayName} loads a fixed set of save slots from an index, so a copy under a " +
                    "new name would never show up in game. Use Back Up instead - it keeps a full copy outside the " +
                    "game folder that you can restore over a slot.");
                return null;
            }

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

                RenameSelfReferences(dest, save.Name, newName);
                Dds2SaveRules.OnSaveCloned(_game, dest, newName);
                VerifyClone(dest, newName);

                log.Success($"Cloned save '{save.Name}' to '{newName}'.");
                return Describe(dest, save.GroupName, isEnabled: save.IsEnabled, save.RootName);
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
                return Describe(dest, save.GroupName, isEnabled: save.IsEnabled, save.RootName);
            }
        }
        catch (Exception ex)
        {
            log.Error($"Failed to clone '{save.Name}': {ex.Message}");
            return null;
        }
    }

    /// Points a freshly-copied save's internal name references at its new name. Only files the
    /// rewriter fully understands are touched; anything else is left byte-for-byte as copied.
    private static void RenameSelfReferences(string folder, string oldName, string newName)
    {
        var log = LoggingService.Instance;
        var references = 0;
        var files = 0;

        foreach (var file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories))
        {
            if (!IsSaveFile(file)) continue;

            var updated = GvasNameRewriter.RewriteSelfReferences(file, oldName, newName);
            if (updated <= 0) continue;

            references += updated;
            files++;
        }

        if (references > 0)
            log.Info($"Renamed {references} internal reference(s) across {files} file(s) so the game recognises the copy.");
        else
            log.Warn($"No internal name references were found to update. If '{newName}' doesn't appear in game, " +
                     "the save may record its name somewhere this can't safely edit.");
    }

    /// Checks a cloned save actually satisfies the rule every working save follows: the folder
    /// name, the name recorded inside the save, and the progress file all agree. Cloning used to
    /// break this silently - the copy looked fine on disk and simply never appeared in game - so
    /// it's worth confirming rather than assuming.
    private void VerifyClone(string folder, string newName)
    {
        if (!Dds2SaveRules.Applies(_game)) return;

        var problem = Dds2SaveRules.DescribeCloneProblem(folder, newName);
        if (problem != null)
            LoggingService.Instance.Warn($"'{newName}' may not appear in game: {problem}");
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
                // Back to the root it came out of, which is only SaveGames for a single-root game.
                var root = RootPathFor(save.RootName);
                destParent = save.GroupName == null ? root : Path.Combine(root, save.GroupName);
            }
            else
            {
                destParent = Path.Combine(_disabledDir, DisabledKeyFor(save.RootName, save.GroupName));
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
