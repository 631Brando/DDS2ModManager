using System.IO;

namespace DDS2ModManager.Services;

/// One thing that happened to a mod, kept so the user can answer "what changed, and when?".
public class ModHistoryEntry
{
    public DateTime AtUtc { get; set; } = DateTime.UtcNow;
    public string ModName { get; set; } = "";
    public string Action { get; set; } = "";
    public string FromVersion { get; set; } = "";
    public string ToVersion { get; set; } = "";

    /// The release notes as published. This is the part worth keeping: the manager already shows
    /// them once, in the prompt, and then throws them away - so a month later there is no way to
    /// find out what an update actually changed short of going back to the repository.
    public string Notes { get; set; } = "";

    public string Source { get; set; } = "";

    public string AtLocalDisplay => AtUtc.ToLocalTime().ToString("d MMM yyyy, HH:mm");

    public string Headline => string.IsNullOrWhiteSpace(FromVersion)
        ? $"{ModName} {ToVersion}".TrimEnd()
        : $"{ModName} {FromVersion} → {ToVersion}";
}

/// A log of mod installs, updates and removals.
///
/// Separate from LoggingService on purpose: that is a diagnostic trace of one session, rotated
/// away after twenty runs. This is a small permanent record of what happened to the user's
/// mods, which is a different question and outlives any single log file.
public class ModHistoryService
{
    private static readonly Lazy<ModHistoryService> _instance = new(() => new ModHistoryService());
    public static ModHistoryService Instance => _instance.Value;

    /// Enough to answer "what changed recently" without the file growing without limit.
    private const int MaxEntries = 400;

    private readonly string _path;
    private List<ModHistoryEntry> _entries = new();

    private ModHistoryService()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DDS2ModManager");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "mod-history.json");
        Load();
    }

    public IReadOnlyList<ModHistoryEntry> Entries => _entries;

    public void Record(string modName, string action, string from, string to, string? notes = null, string? source = null)
    {
        try
        {
            _entries.Insert(0, new ModHistoryEntry
            {
                ModName = modName,
                Action = action,
                FromVersion = from ?? "",
                ToVersion = to ?? "",
                Notes = notes ?? "",
                Source = source ?? ""
            });

            if (_entries.Count > MaxEntries) _entries = _entries.Take(MaxEntries).ToList();

            Save();
        }
        catch (Exception ex)
        {
            // History is a convenience. Failing to write it must never interrupt the install that
            // was the actual point of the operation.
            LoggingService.Instance.Warn($"Couldn't record mod history: {ex.Message}");
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            _entries = JsonSerializer.Deserialize<List<ModHistoryEntry>>(File.ReadAllText(_path)) ?? new();
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Warn($"Couldn't read the mod history: {ex.Message}");
            _entries = new();
        }
    }

    private void Save() =>
        File.WriteAllText(_path, JsonSerializer.Serialize(_entries, new JsonSerializerOptions { WriteIndented = true }));

    public void Clear()
    {
        _entries = new();
        try { if (File.Exists(_path)) File.Delete(_path); } catch { }
    }
}
