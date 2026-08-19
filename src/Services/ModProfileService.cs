using System.IO;

namespace DDS2ModManager.Services;

/// One mod inside a saved profile or an exported list.
public class ProfileMod
{
    public string Name { get; set; } = "";
    public ModType Type { get; set; }
    public bool Enabled { get; set; }
    public string Version { get; set; } = "";

    /// Where the mod says its updates come from, so an exported list is a list somebody can
    /// actually act on rather than a list of names to go hunting for.
    public string UpdateUrl { get; set; } = "";

    public int NexusModId { get; set; }
}

/// A named set of which mods are on and which are off.
public class ModProfile
{
    public int Schema { get; set; } = SupportedSchema;
    public string Name { get; set; } = "";
    public DateTime SavedUtc { get; set; } = DateTime.UtcNow;
    public string ManagerVersion { get; set; } = "";
    public string GameVersion { get; set; } = "";

    /// Which game this profile describes (a GameProfile.Id). Makes an exported list self-describing
    /// once more than one game is manageable.
    ///
    /// Additive on purpose, and Schema deliberately stays 1: Read() rejects any profile whose Schema
    /// exceeds SupportedSchema, so bumping it for a new optional field would make every profile
    /// written from now on unreadable by the previous build, for no gain. An older profile has no
    /// GameId and renders exactly as it always did.
    public string GameId { get; set; } = "";

    public List<ProfileMod> Mods { get; set; } = new();

    public const int SupportedSchema = 1;

    public string SavedDisplay => SavedUtc.ToLocalTime().ToString("d MMM yyyy, HH:mm");
    public string Summary => $"{Mods.Count(m => m.Enabled)} enabled of {Mods.Count}";
}

/// Saves and restores named sets of enabled mods, and exports a readable list to share.
///
/// The case this exists for is the one people describe unprompted: reinstalling a game after a
/// year and having no idea which mods were on. A profile answers that, and an exported list
/// answers the other half - "here is my setup" in a bug report.
///
/// IMPORTANT: applying a profile only toggles mods that are ALREADY INSTALLED. It never
/// downloads, installs or deletes anything. A profile naming mods the user doesn't have reports
/// them as missing and leaves them alone, because silently fetching mods from a file is exactly
/// the kind of surprise this app avoids everywhere else.
public class ModProfileService
{
    private readonly string _dir;
    private readonly string _gameId;

    /// Profiles are per game install: a DDS2 load order means nothing applied to DDS1, and without
    /// scoping two games both holding a profile called "Main" would overwrite each other's file.
    public ModProfileService(GameInstallation game)
        : this(AppPaths.ProfilesFor(game.RootPath), game.Profile.Id) { }

    /// Explicit folder, for tests. Without this a test run writes into the user's real profile
    /// folder, which is both a surprise and a way for a test to delete something they wanted.
    public ModProfileService(string directory, string gameId = "")
    {
        _dir = directory;
        _gameId = gameId;
        Directory.CreateDirectory(_dir);
    }

    public string Folder => _dir;

    /// Profiles on disk, newest first. Never throws - a corrupt one is skipped with a warning
    /// rather than taking the list down with it.
    public List<ModProfile> All()
    {
        var found = new List<ModProfile>();

        try
        {
            foreach (var file in Directory.EnumerateFiles(_dir, "*.json"))
            {
                var profile = Read(file);
                if (profile != null) found.Add(profile);
            }
        }
        catch (Exception ex) { LoggingService.Instance.Warn($"Couldn't list saved profiles: {ex.Message}"); }

        return found.OrderByDescending(p => p.SavedUtc).ToList();
    }

    private ModProfile? Read(string path)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<ModProfile>(File.ReadAllText(path));
            if (parsed == null) return null;

            if (parsed.Schema > ModProfile.SupportedSchema)
            {
                LoggingService.Instance.Warn(
                    $"'{Path.GetFileNameWithoutExtension(path)}' was saved by a newer version of this manager " +
                    "and is being ignored rather than misread.");
                return null;
            }

            return parsed;
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Warn($"Couldn't read the profile '{Path.GetFileName(path)}': {ex.Message}");
            return null;
        }
    }

    public ModProfile Capture(string name, IEnumerable<ModInfo> mods, string managerVersion, string gameVersion) =>
        new()
        {
            Name = name,
            ManagerVersion = managerVersion,
            GameVersion = gameVersion,
            GameId = _gameId,
            Mods = mods.Select(m => new ProfileMod
            {
                Name = m.Name,
                Type = m.Type,
                Enabled = m.IsEnabled,
                Version = m.InstalledVersion,
                UpdateUrl = m.ModUpdateUrl ?? "",
                // The user's declaration first, the name match second - same order as the resolver.
                // Any non-null link ENDS the chain: a "NoPage" link carries ModId 0, and falling
                // through to the matched id there would export the very guess the user rejected.
                //
                // Write-only, and no domain of its own: ModProfile.GameId qualifies it. Nothing in
                // this repo reads it back, and nothing may ever restore it as a link.
                NexusModId = m.NexusLink is { } link ? link.ModId : (m.NexusInfo?.ModId ?? 0)
            }).ToList()
        };

    public bool Save(ModProfile profile)
    {
        try
        {
            File.WriteAllText(PathFor(profile.Name),
                JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Error($"Couldn't save the profile '{profile.Name}': {ex.Message}");
            return false;
        }
    }

    public bool Delete(string name)
    {
        try
        {
            var path = PathFor(name);
            if (File.Exists(path)) File.Delete(path);
            return true;
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Error($"Couldn't delete the profile '{name}': {ex.Message}");
            return false;
        }
    }

    /// What applying a profile WOULD do, worked out before anything is touched.
    public record ApplyPlan(
        List<ModInfo> ToEnable,
        List<ModInfo> ToDisable,
        List<string> Missing,
        List<string> Extra)
    {
        public bool ChangesAnything => ToEnable.Count > 0 || ToDisable.Count > 0;
    }

    /// Matches a profile against what is installed.
    ///
    /// Matched on name and type together, because a two-part mod ships two rows sharing a name -
    /// matching on name alone would apply one entry's state to both halves.
    public ApplyPlan Plan(ModProfile profile, IEnumerable<ModInfo> installed)
    {
        var mods = installed.ToList();
        var toEnable = new List<ModInfo>();
        var toDisable = new List<ModInfo>();
        var missing = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var wanted in profile.Mods)
        {
            var match = mods.FirstOrDefault(m =>
                m.Type == wanted.Type && string.Equals(m.Name, wanted.Name, StringComparison.OrdinalIgnoreCase));

            if (match == null)
            {
                missing.Add($"{wanted.Name} ({wanted.Type})");
                continue;
            }

            seen.Add(match.Id);
            if (wanted.Enabled && !match.IsEnabled) toEnable.Add(match);
            else if (!wanted.Enabled && match.IsEnabled) toDisable.Add(match);
        }

        // Installed but absent from the profile. Reported, never touched: the profile says what
        // it knew about, not that everything else should be switched off.
        var extra = mods.Where(m => !seen.Contains(m.Id)).Select(m => $"{m.Name} ({m.Type})").ToList();

        return new ApplyPlan(toEnable, toDisable, missing, extra);
    }

    /// Filenames are user-supplied, so anything that isn't safe in one is replaced rather than
    /// allowed to escape the profiles folder.
    private string PathFor(string name)
    {
        var safe = new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim();
        if (safe.Length == 0) safe = "profile";
        return Path.Combine(_dir, safe + ".json");
    }

    /// A profile as plain text, for pasting into a bug report or a Discord message.
    public static string ToShareableText(ModProfile profile)
    {
        var lines = new List<string>
        {
            // Named from the profile so a shared list says which game it is for. A profile written
            // before GameId existed has none, and falls back to the original wording exactly.
            $"{GameProfiles.ById(profile.GameId)?.ShortName ?? GameProfiles.Default.ShortName} mod list - {profile.Name}",
            $"Saved {profile.SavedDisplay}   Manager {profile.ManagerVersion}   Game {profile.GameVersion}",
            $"{profile.Summary}",
            ""
        };

        foreach (var m in profile.Mods.OrderByDescending(m => m.Enabled).ThenBy(m => m.Name))
        {
            var state = m.Enabled ? "[on] " : "[off]";
            var version = string.IsNullOrWhiteSpace(m.Version) ? "" : $"  v{m.Version}";
            var source = string.IsNullOrWhiteSpace(m.UpdateUrl) ? "" : $"  {m.UpdateUrl}";
            lines.Add($"{state} {m.Name} ({m.Type}){version}{source}");
        }

        return string.Join(Environment.NewLine, lines);
    }
}
