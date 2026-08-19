namespace DDS2ModManager.Services;

/// One-time move of this app's own %AppData% state from the original flat layout into per-game
/// folders, so a second game can be managed without the two sharing one pile.
///
/// Everything here is written to be safe when interrupted. Nothing is ever deleted, each disabled
/// mod is moved and then immediately re-recorded, and if the state cannot be confidently attributed
/// to a game the whole migration is skipped and retried next launch rather than guessed at.
///
/// Why guessing would be bad: a disabled mod's registry entry holds ABSOLUTE paths into the flat
/// DisabledMods folder. Move the files without rewriting those paths and the mod's file list
/// resolves to nothing - at which point Enable() would have recorded an empty list and reported
/// success, permanently losing the only record of what that mod owns. (ModInstallerService now
/// refuses to write an empty result, which is the net underneath this.)
public static class LegacyStateMigrationService
{
    /// 0 = the original flat layout. Bump when the layout changes again.
    public const int CurrentLayout = 1;

    public static void RunOnce()
    {
        var settings = AppSettingsService.Instance.Current;
        if (settings.StateLayoutVersion >= CurrentLayout) return;

        var log = LoggingService.Instance;

        try
        {
            // A fresh install has nothing to move; stamp it so this never runs again.
            if (!Directory.Exists(AppPaths.Root) || !HasLegacyState())
            {
                Stamp(settings);
                return;
            }

            var key = ResolveLegacyGameKey(settings);
            if (key == null)
            {
                // Deliberately NOT stamped: this is recoverable. Once the user opens a game we can
                // attribute the state, and until then everything keeps working, because the paths
                // recorded for disabled mods are absolute and still point at the untouched folder.
                log.Warn(
                    "Found mod history/profiles/backups from an older layout but couldn't tell which game install " +
                    "they belong to, so they've been left exactly where they are. They'll be picked up automatically " +
                    "once a game is open. Nothing has been lost.");
                return;
            }

            log.Info("Moving mod history, profiles, backups and disabled mods into per-game folders (one time)...");

            MigrateHistory(key, log);
            MoveChildrenInto(AppPaths.Profiles, AppPaths.ProfilesForKey(key), "profiles", log);
            MoveChildrenInto(AppPaths.Backups, AppPaths.BackupsForKey(key), "mod backups", log);

            var registry = new ModRegistryService(AppPaths.RegistryForKey(key));
            var (moved, skipped) = MigrateDisabledMods(
                AppPaths.DisabledMods, AppPaths.DisabledModsForKey(key), registry, log);

            if (moved > 0) log.Info($"Moved {moved} disabled mod(s) into this game's folder.");
            if (skipped > 0)
                log.Info($"Left {skipped} disabled mod folder(s) alone - nothing in this game's registry claims them.");

            Stamp(settings);
            log.Success("Per-game state migration complete.");
        }
        catch (Exception ex)
        {
            // Not stamped, so it retries. Everything is a move or a rewrite of a file we just moved,
            // so a partial run leaves both halves on disk rather than losing either.
            log.Error(
                $"Couldn't finish moving state into per-game folders ({ex.Message}). Nothing was deleted, and this " +
                "will be retried next launch.");
        }
    }

    private static void Stamp(AppSettings settings)
    {
        settings.StateLayoutVersion = CurrentLayout;
        AppSettingsService.Instance.SaveQuiet();
    }

    private static bool HasLegacyState() =>
        File.Exists(Path.Combine(AppPaths.Root, "mod-history.json"))
        || HasMovableChildren(AppPaths.Profiles)
        || HasMovableChildren(AppPaths.Backups)
        || HasMovableChildren(AppPaths.DisabledMods);

    /// Which game install the flat state belongs to.
    ///
    /// It can only ever be one game - the flat layout predates multi-game support entirely - so the
    /// job is just to name it. A registry filename is the most reliable source because it carries
    /// the key directly and needs no install path to still exist on disk.
    private static string? ResolveLegacyGameKey(AppSettings settings)
    {
        var keys = Directory.Exists(AppPaths.Root)
            ? Directory.GetFiles(AppPaths.Root, "registry_*.json")
                .Select(AppPaths.KeyFromRegistryPath)
                .Where(k => !string.IsNullOrEmpty(k))
                .Select(k => k!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [];

        // The flat layout can only ever have described DDS2, and AppSettingsService.Load() has
        // already folded a pre-multi-game settings file into that section by the time this runs.
        var rememberedPath = settings.Games.TryGetValue(GameProfiles.Dds2.Id, out var dds2)
            ? dds2.GamePathOverride
            : null;

        var remembered = string.IsNullOrWhiteSpace(rememberedPath) ? null : AppPaths.GameKey(rememberedPath);

        return ResolveLegacyGameKey(keys, remembered);
    }

    /// The attribution rule, separated from where the inputs come from so it can be tested without
    /// a real %AppData%. Returning null means "don't migrate" - never "pick one and hope".
    public static string? ResolveLegacyGameKey(IReadOnlyList<string> registryKeys, string? rememberedKey)
    {
        // One tracked install: unambiguous.
        if (registryKeys.Count == 1) return registryKeys[0];

        // Several tracked installs - only the remembered one can be said to own the shared state.
        if (registryKeys.Count > 1)
            return rememberedKey != null && registryKeys.Contains(rememberedKey, StringComparer.OrdinalIgnoreCase)
                ? rememberedKey
                : null;

        // No mods ever tracked, but there may still be profiles or history worth keeping.
        return rememberedKey;
    }

    private static void MigrateHistory(string key, LoggingService log)
    {
        var legacy = Path.Combine(AppPaths.Root, "mod-history.json");
        var target = AppPaths.ModHistoryForKey(key);
        if (!File.Exists(legacy) || File.Exists(target)) return;

        try { File.Move(legacy, target); }
        catch (Exception ex) { log.Warn($"Couldn't move the mod history: {ex.Message}. It's still at {legacy}."); }
    }

    private static bool HasMovableChildren(string root) =>
        Directory.Exists(root) && (Directory.EnumerateFiles(root).Any() || MovableDirs(root).Any());

    /// Direct subfolders that are content rather than one of the new per-game folders. The per-game
    /// folders live INSIDE the legacy root, so without this the migration would try to move a
    /// folder into itself.
    private static IEnumerable<string> MovableDirs(string root) =>
        Directory.EnumerateDirectories(root).Where(d => !LooksLikeGameKey(Path.GetFileName(d)));

    /// A key is the first 12 hex characters of a SHA-256. Mod and backup ids are 32-character GUIDs,
    /// so length alone separates them cleanly.
    public static bool LooksLikeGameKey(string name) =>
        name.Length == 12 && name.All(Uri.IsHexDigit);

    public static void MoveChildrenInto(string legacyRoot, string target, string what, LoggingService log)
    {
        if (!Directory.Exists(legacyRoot)) return;

        var files = Directory.GetFiles(legacyRoot);
        var dirs = MovableDirs(legacyRoot).ToList();
        if (files.Length == 0 && dirs.Count == 0) return;

        Directory.CreateDirectory(target);

        foreach (var file in files)
            TryMove(() => File.Move(file, Path.Combine(target, Path.GetFileName(file))), file, what, log);

        foreach (var dir in dirs)
            TryMove(() => Directory.Move(dir, Path.Combine(target, Path.GetFileName(dir))), dir, what, log);
    }

    private static void TryMove(Action move, string source, string what, LoggingService log)
    {
        try { move(); }
        catch (Exception ex)
        {
            log.Warn($"Couldn't move {what} entry '{Path.GetFileName(source)}': {ex.Message}. It's still at {source}.");
        }
    }

    /// The only part that has to rewrite anything, because a disabled mod's recorded file paths
    /// point straight into the folder being moved.
    ///
    /// Only folders the registry actually claims are moved. An unclaimed folder is left exactly
    /// where it is - it may belong to a second install whose own registry still resolves it through
    /// the absolute paths it recorded, and moving it under this game's key would strand it.
    public static (int Moved, int Skipped) MigrateDisabledMods(
        string legacyRoot, string target, ModRegistryService registry, LoggingService log)
    {
        if (!Directory.Exists(legacyRoot)) return (0, 0);

        var candidates = MovableDirs(legacyRoot).ToList();
        if (candidates.Count == 0) return (0, 0);

        var moved = 0;
        var skipped = 0;

        foreach (var dir in candidates)
        {
            var modId = Path.GetFileName(dir);
            var mod = registry.Mods.FirstOrDefault(m => string.Equals(m.Id, modId, StringComparison.OrdinalIgnoreCase));
            if (mod == null) { skipped++; continue; }

            var dest = Path.Combine(target, modId);
            if (Directory.Exists(dest)) continue;

            try
            {
                Directory.CreateDirectory(target);
                Directory.Move(dir, dest);

                // Re-record immediately, one mod at a time. If the process dies here the next run
                // sees the folder already at its destination and the entry already rewritten - or
                // neither - but never a rewritten entry pointing at a folder that was not moved.
                mod.InstallPath = Rebase(mod.InstallPath, dir, dest);
                mod.InstallFiles = mod.InstallFiles.Select(f => Rebase(f, dir, dest)).ToList();
                registry.Save();
                moved++;
            }
            catch (Exception ex)
            {
                log.Warn($"Couldn't move disabled mod '{mod.Name}': {ex.Message}. Its files are still at {dir}.");
            }
        }

        return (moved, skipped);
    }

    public static string Rebase(string? path, string oldPrefix, string newPrefix)
    {
        if (string.IsNullOrEmpty(path)) return path ?? "";
        return path.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase)
            ? newPrefix + path[oldPrefix.Length..]
            : path;
    }
}
