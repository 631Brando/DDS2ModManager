namespace DDS2ModManager.Services;

/// Persists user preferences to %AppData%\DDS2ModManager\settings.json.
public class AppSettingsService
{
    private static readonly Lazy<AppSettingsService> _instance = new(() => new AppSettingsService());
    public static AppSettingsService Instance => _instance.Value;

    private readonly string _path;
    public AppSettings Current { get; private set; } = new();

    private AppSettingsService()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DDS2ModManager");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "settings.json");
        Load();
    }

    public void Load()
    {
        if (!File.Exists(_path)) return;
        try
        {
            Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path)) ?? new AppSettings();
        }
        catch
        {
            Current = new AppSettings();
        }
    }

    public void Save()
    {
        WriteToDisk();
        LoggingService.Instance.Info("Settings saved.");
    }

    /// Same write without the log line - for incidental state like window size, which is saved on
    /// every close and would otherwise put a "Settings saved." entry in the log every session for
    /// something the user never asked to save.
    public void SaveQuiet() => WriteToDisk();

    private void WriteToDisk() =>
        File.WriteAllText(_path, JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true }));

    public string GetLogsFolder() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DDS2ModManager", "Logs");

    public string GetDisabledModsFolder() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DDS2ModManager", "DisabledMods");

    /// Deletes all of this app's persisted state - settings.json, every per-game mod-tracking
    /// registry_*.json, and the cached mappings.usmap (auto re-extracted from the embedded
    /// resource next time it's needed) - so the next launch starts from a clean slate. This is
    /// the recovery path for when persisted state itself is the problem (corrupt JSON, a stale
    /// cache, etc). Deliberately does NOT touch DisabledModsFolder (real mod files while
    /// disabled) or anything in the game's own folders - only this app's own bookkeeping/cache.
    public static void ResetAllAppData()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DDS2ModManager");
        if (!Directory.Exists(dir)) return;

        foreach (var file in Directory.GetFiles(dir, "registry_*.json"))
            File.Delete(file);

        var settingsPath = Path.Combine(dir, "settings.json");
        if (File.Exists(settingsPath)) File.Delete(settingsPath);

        var mappingsPath = Path.Combine(dir, "mappings.usmap");
        if (File.Exists(mappingsPath)) File.Delete(mappingsPath);
    }
}
