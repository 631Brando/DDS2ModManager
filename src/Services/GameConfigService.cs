namespace DDS2ModManager.Services;

/// Reads and writes the game's per-user .ini files in %LocalAppData%\&lt;Project&gt;\Saved\Config\Windows.
///
/// Edits are raw text on purpose: Unreal .ini keys are game- and plugin-specific, so any attempt to
/// present a curated list of "known settings" would be wrong for every game but the one it was
/// written against. Raw editing works everywhere, and the backup-on-save below is what makes it
/// safe to get wrong.
public class GameConfigService
{
    private const string BackupSuffix = ".dds2mm.bak";

    private readonly GameInstallation _game;

    public GameConfigService(GameInstallation game) => _game = game;

    public string ConfigPath => _game.ConfigPath;
    public bool ConfigFolderExists => Directory.Exists(_game.ConfigPath);

    /// Config files worth showing. Unreal writes a lot of empty 2-byte placeholder .ini files for
    /// plugins that have no settings; listing those buries the handful that actually matter
    /// (Engine, Game, GameUserSettings, Input) in noise, so empty ones are filtered out.
    public List<GameConfigFile> GetConfigFiles()
    {
        if (!ConfigFolderExists) return new List<GameConfigFile>();

        return Directory.GetFiles(_game.ConfigPath, "*.ini")
            .Select(f => new FileInfo(f))
            .Where(f => f.Length > 4)
            .Select(f => new GameConfigFile
            {
                Name = f.Name,
                Path = f.FullName,
                SizeBytes = f.Length,
                LastModified = f.LastWriteTime,
                HasBackup = File.Exists(f.FullName + BackupSuffix)
            })
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public string ReadText(GameConfigFile file) => File.ReadAllText(file.Path);

    /// Writes new contents, keeping a one-slot backup of what was there before. The backup is
    /// only taken the first time a file is edited, so it always represents the file as the game
    /// last wrote it rather than the previous (possibly also broken) edit.
    public bool Save(GameConfigFile file, string contents)
    {
        var log = LoggingService.Instance;
        try
        {
            var backupPath = file.Path + BackupSuffix;
            if (!File.Exists(backupPath))
            {
                File.Copy(file.Path, backupPath);
                log.Info($"Backed up the original {file.Name} before saving changes.");
            }

            File.WriteAllText(file.Path, contents);
            file.HasBackup = true;
            log.Success($"Saved {file.Name}.");
            return true;
        }
        catch (Exception ex)
        {
            log.Error($"Couldn't save {file.Name}: {ex.Message}");
            return false;
        }
    }

    public bool RestoreBackup(GameConfigFile file)
    {
        var log = LoggingService.Instance;
        var backupPath = file.Path + BackupSuffix;
        try
        {
            if (!File.Exists(backupPath))
            {
                log.Warn($"No backup exists for {file.Name}.");
                return false;
            }

            File.Copy(backupPath, file.Path, true);
            log.Success($"Restored {file.Name} from its backup.");
            return true;
        }
        catch (Exception ex)
        {
            log.Error($"Couldn't restore {file.Name}: {ex.Message}");
            return false;
        }
    }
}

public class GameConfigFile
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public long SizeBytes { get; set; }
    public DateTime LastModified { get; set; }
    public bool HasBackup { get; set; }

    public string SizeDisplay => SizeBytes < 1024 ? $"{SizeBytes} B" : $"{SizeBytes / 1024.0:F1} KB";
}
