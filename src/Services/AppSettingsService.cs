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
        AppPaths.EnsureRoot();
        _path = AppPaths.Settings;
        Load();
    }

    public void Load()
    {
        if (!File.Exists(_path)) return;
        try
        {
            var json = File.ReadAllText(_path);
            Current = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            MigrateFlatGameSettings(json);
        }
        catch
        {
            Current = new AppSettings();
        }
    }

    /// A settings.json with no Games section was written before this app could manage a second game,
    /// so everything game-shaped in it describes DDS2 and nothing else.
    ///
    /// Reads the SAME text a second time as a GameSettings. That works only because GameSettings
    /// keeps the old property names exactly, and it is the point: no hand-written field-by-field
    /// mapping means no field can be quietly forgotten. Losing one would present to the user as
    /// "the app forgot my game path / AES key / update history", with no error anywhere.
    private void MigrateFlatGameSettings(string json)
    {
        if (Current.Games.Count > 0) return;

        var legacy = JsonSerializer.Deserialize<GameSettings>(json);
        if (legacy == null || !legacy.HasAnything) return;

        // The old Settings window wrote EGameVersion on EVERY save, so practically every existing
        // settings.json contains "GAME_UE5_3". Carried across as an explicit override it would pin
        // that user to UE 5.3 permanently, surviving any future profile bump - and the failure is
        // silent, because a wrong EGame still lists every path in a pak and only fails on
        // deserialize. Keep it only when it says something the profile does not already say.
        if (string.Equals(legacy.EGameVersion, GameProfiles.Dds2.EngineVersion.ToString(),
                StringComparison.OrdinalIgnoreCase))
            legacy.EGameVersion = null;

        Current.Games[GameProfiles.Dds2.Id] = legacy;
        Current.ActiveGameId ??= GameProfiles.Dds2.Id;

        WriteToDisk();
        LoggingService.Instance.Info(
            "Moved your existing settings into a per-game section so a second game can be managed (one time).");
    }

    /// This game's settings, created on first ask. Never returns null, so callers can read and write
    /// through it without a null dance.
    public GameSettings ForGame(GameProfile profile)
    {
        if (!Current.Games.TryGetValue(profile.Id, out var settings))
            Current.Games[profile.Id] = settings = new GameSettings();
        return settings;
    }

    public GameSettings ForGame(GameInstallation game) => ForGame(game.Profile);

    /// Records which game to reopen next launch. Deliberately the only writer, so ActiveGameId can
    /// never disagree with the game that is actually open.
    public void SetActiveGame(GameProfile profile) => Current.ActiveGameId = profile.Id;

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

    public string GetLogsFolder() => AppPaths.Logs;

    public string GetDisabledModsFolder() => AppPaths.DisabledMods;

    /// Deletes all of this app's persisted state - settings.json, every per-game mod-tracking
    /// registry_*.json, and the cached mappings.usmap (auto re-extracted from the embedded
    /// resource next time it's needed) - so the next launch starts from a clean slate. This is
    /// the recovery path for when persisted state itself is the problem (corrupt JSON, a stale
    /// cache, etc). Deliberately does NOT touch DisabledModsFolder (real mod files while
    /// disabled) or anything in the game's own folders - only this app's own bookkeeping/cache.
    public static void ResetAllAppData()
    {
        var dir = AppPaths.Root;
        if (!Directory.Exists(dir)) return;

        foreach (var file in Directory.GetFiles(dir, "registry_*.json"))
            File.Delete(file);

        if (File.Exists(AppPaths.Settings)) File.Delete(AppPaths.Settings);
        if (File.Exists(AppPaths.Mappings)) File.Delete(AppPaths.Mappings);
    }
}
