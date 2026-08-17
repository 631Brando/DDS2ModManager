namespace DDS2ModManager.Services;

/// Reads and writes the game's per-user .ini files in %LocalAppData%\&lt;Project&gt;\Saved\Config\Windows.
///
/// Edits are raw text on purpose: Unreal .ini keys are game- and plugin-specific, so any attempt to
/// present a curated list of "known settings" would be wrong for every game but the one it was
/// written against. Raw editing works everywhere, and the backup-on-save below is what makes it
/// safe to get wrong.
public class GameConfigService
{
    /// Public because the UE4SS installer needs it: whether a backup exists is how it tells a
    /// settings file the user has edited from one they've never touched.
    public const string BackupSuffix = ".dds2mm.bak";

    private readonly GameInstallation _game;

    public GameConfigService(GameInstallation game) => _game = game;

    public string ConfigPath => _game.ConfigPath;
    public bool ConfigFolderExists => Directory.Exists(_game.ConfigPath);

    /// Config files worth showing. Unreal writes a lot of empty 2-byte placeholder .ini files for
    /// plugins that have no settings; listing those buries the handful that actually matter
    /// (Engine, Game, GameUserSettings, Input) in noise, so empty ones are filtered out.
    public List<GameConfigFile> GetConfigFiles()
    {
        var files = new List<GameConfigFile>();

        if (ConfigFolderExists)
        {
            files.AddRange(Directory.GetFiles(_game.ConfigPath, "*.ini")
                .Select(f => new FileInfo(f))
                .Where(f => f.Length > 4)
                .Select(f => Describe(f, isGameConfig: true))
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase));
        }

        files.AddRange(GetModLoaderConfigFiles());
        return files;
    }

    /// UE4SS's own settings, which live in the mod loader's folder rather than the game's config
    /// folder. Listed alongside the game's because it is the other file people are told to edit -
    /// to turn on the debug console, or change a keybind - and hunting through Binaries\Win64 for
    /// it is exactly the kind of thing this window exists to save.
    ///
    /// Found by enumerating the folder rather than looking up "UE4SS-settings.ini" by name, so a
    /// build that ships it under a different name still appears instead of silently going missing.
    /// The backup-on-save protection is the same as for the game's files.
    private IEnumerable<GameConfigFile> GetModLoaderConfigFiles()
    {
        if (!Directory.Exists(_game.UE4SSRootPath)) return Enumerable.Empty<GameConfigFile>();

        try
        {
            return Directory.GetFiles(_game.UE4SSRootPath, "*.ini", SearchOption.TopDirectoryOnly)
                .Select(f => new FileInfo(f))
                .Select(f => Describe(f, isGameConfig: false))
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            // The game's own config files are the main event here; failing to list UE4SS's must not
            // take the whole list down with it.
            LoggingService.Instance.Warn($"Couldn't list UE4SS's settings files: {ex.Message}");
            return Enumerable.Empty<GameConfigFile>();
        }
    }

    private static GameConfigFile Describe(FileInfo f, bool isGameConfig) => new()
    {
        Name = f.Name,
        Path = f.FullName,
        SizeBytes = f.Length,
        LastModified = f.LastWriteTime,
        HasBackup = File.Exists(f.FullName + BackupSuffix),
        IsGameConfig = isGameConfig
    };

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

    /// False for UE4SS's own settings file, which sits in the mod loader's folder rather than the
    /// game's config folder and changes the loader's behaviour, not the game's.
    ///
    /// The distinction is not pedantry: the two are edited for completely different reasons, they
    /// live on opposite sides of the install, and "I changed a setting and the game didn't change"
    /// is a confusing thing to work out on your own. Everything user-facing keys off this.
    public bool IsGameConfig { get; set; } = true;

    /// Group heading in the file list.
    public string Category => IsGameConfig ? "Game config" : "Mod loader (UE4SS)";

    public string SizeDisplay => SizeBytes < 1024 ? $"{SizeBytes} B" : $"{SizeBytes / 1024.0:F1} KB";

    /// The folder this file lives in - the two kinds are in different places, so "Open Folder" has
    /// to follow the selection rather than always opening the game's config folder.
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";
}
