using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace DDS2ModManager.Services;

/// Persists the list of mods we've installed (name, type, current file locations, enabled
/// state) to %AppData%\DDS2ModManager\registry_<gameHash>.json. This is our own bookkeeping -
/// the game folder itself has no concept of "which mod owns which files."
public class ModRegistryService
{
    // Write ModType as its name ("LogicMod") rather than an int, and read case-insensitively,
    // so the file is human-readable and won't break if enum ordering ever changes.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        // ModUpdateSourceJsonConverter is a migration: UpdateSource used to be an enum and is now
        // an object, and without it every registry written by an older build fails to load.
        Converters = { new JsonStringEnumConverter(), new ModUpdateSourceJsonConverter() }
    };

    private readonly string _registryPath;
    public List<ModInfo> Mods { get; private set; } = new();

    public ModRegistryService(GameInstallation game)
        : this(AppPaths.RegistryForKey(AppPaths.GameKey(game.RootPath))) { }

    /// Explicit file, for the one-time state migration (which recovers a key from a filename and
    /// has no GameInstallation to hand) and for tests.
    public ModRegistryService(string registryPath)
    {
        AppPaths.EnsureRoot();
        _registryPath = registryPath;
        Load();
    }

    public string RegistryPath => _registryPath;

    public void Load()
    {
        if (!File.Exists(_registryPath)) return;
        try
        {
            Mods = JsonSerializer.Deserialize<List<ModInfo>>(File.ReadAllText(_registryPath), JsonOptions) ?? new();
        }
        catch (Exception ex)
        {
            // Starting empty is the only safe option - but doing it silently is not. An empty list
            // is indistinguishable from "you have no mods", and the next Save() overwrites the file
            // that still held them, so the failure has to be both reported and preserved.
            Mods = new();

            var salvage = _registryPath + ".unreadable";
            try
            {
                File.Copy(_registryPath, salvage, overwrite: true);
                LoggingService.Instance.Error(
                    $"Couldn't read the mod registry ({ex.Message}). Your installed mods are untouched on disk, but " +
                    $"the manager has lost track of them - use \"Find Existing Mods\" to pick them back up. The " +
                    $"unreadable file was kept at {salvage}.");
            }
            catch
            {
                LoggingService.Instance.Error(
                    $"Couldn't read the mod registry ({ex.Message}), and couldn't back it up either. Your installed " +
                    "mods are untouched on disk - use \"Find Existing Mods\" to pick them back up.");
            }
        }
    }

    public void Save() =>
        AtomicFile.WriteAllText(_registryPath, JsonSerializer.Serialize(Mods, JsonOptions));

    /// Points lua mods whose folder moved at where it is now: UE4SS moving between its older layout
    /// (Binaries\Win64\Mods) and its current one (ue4ss\Mods) carries the folders across, and a row
    /// left naming the old place can't be enabled, disabled or removed. Only a folder that is gone from
    /// its recorded place AND present, by the same name, in the current Mods folder is re-linked.
    public int RelinkMovedLuaMods(string currentModsFolder)
    {
        var relinked = 0;

        foreach (var mod in Mods.Where(m => m.Type == ModType.LuaMod && !string.IsNullOrWhiteSpace(m.InstallPath)))
        {
            if (Directory.Exists(mod.InstallPath)) continue;

            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(mod.InstallPath));
            var moved = Path.Combine(currentModsFolder, name);
            if (string.IsNullOrWhiteSpace(name) || !Directory.Exists(moved)) continue;

            var old = mod.InstallPath;
            mod.InstallPath = moved;
            mod.InstallFiles = mod.InstallFiles
                .Select(f => string.Equals(Path.TrimEndingDirectorySeparator(f), Path.TrimEndingDirectorySeparator(old),
                    StringComparison.OrdinalIgnoreCase) ? moved : f)
                .ToList();
            relinked++;
        }

        if (relinked > 0) Save();
        return relinked;
    }

    public void Upsert(ModInfo mod)
    {
        Mods.RemoveAll(m => m.Id == mod.Id);
        Mods.Add(mod);
        Save();
    }

    public void Remove(string id)
    {
        Mods.RemoveAll(m => m.Id == id);
        Save();
    }
}
