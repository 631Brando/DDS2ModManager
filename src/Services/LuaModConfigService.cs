namespace DDS2ModManager.Services;

/// Reads/writes ue4ss\Mods\mods.txt. Always appends/edits in place - never rewrites
/// the whole file from a template - so pre-existing entries (and the "Keybinds" block
/// that must stay last) are preserved exactly as UE4SS left them.
public class LuaModConfigService
{
    private static readonly Regex EntryRegex =
        new(@"^(?<indent>\s*)(?<name>[A-Za-z0-9_\-]+)\s*:\s*(?<val>[01])\s*$", RegexOptions.Compiled);

    public Dictionary<string, bool> ReadEntries(GameInstallation game)
    {
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(game.ModsTxtPath)) return result;

        foreach (var line in File.ReadAllLines(game.ModsTxtPath))
        {
            var m = EntryRegex.Match(line);
            if (m.Success)
                result[m.Groups["name"].Value] = m.Groups["val"].Value == "1";
        }
        return result;
    }

    public void SetEnabled(GameInstallation game, string modFolderName, bool enabled)
    {
        EnsureFileExists(game);
        var lines = File.ReadAllLines(game.ModsTxtPath).ToList();
        var found = false;

        for (var i = 0; i < lines.Count; i++)
        {
            var m = EntryRegex.Match(lines[i]);
            if (m.Success && string.Equals(m.Groups["name"].Value, modFolderName, StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = $"{modFolderName} : {(enabled ? 1 : 0)}";
                found = true;
                break;
            }
        }

        if (!found)
        {
            // Insert before the "Built-in keybinds, do not move up!" comment so Keybinds stays last.
            var insertAt = lines.FindIndex(l =>
                l.TrimStart().StartsWith(";") && l.Contains("Keybinds", StringComparison.OrdinalIgnoreCase));
            if (insertAt < 0) insertAt = lines.Count;
            lines.Insert(insertAt, $"{modFolderName} : {(enabled ? 1 : 0)}");
        }

        File.WriteAllLines(game.ModsTxtPath, lines);
        LoggingService.Instance.Info($"mods.txt: '{modFolderName}' -> {(enabled ? "enabled (1)" : "disabled (0)")}.");
    }

    public void RemoveEntry(GameInstallation game, string modFolderName)
    {
        if (!File.Exists(game.ModsTxtPath)) return;

        var lines = File.ReadAllLines(game.ModsTxtPath)
            .Where(l =>
            {
                var m = EntryRegex.Match(l);
                return !(m.Success && string.Equals(m.Groups["name"].Value, modFolderName, StringComparison.OrdinalIgnoreCase));
            })
            .ToList();

        File.WriteAllLines(game.ModsTxtPath, lines);
        LoggingService.Instance.Info($"mods.txt: removed entry for '{modFolderName}'.");
    }

    private void EnsureFileExists(GameInstallation game)
    {
        Directory.CreateDirectory(game.UE4SSModsPath);
        if (!File.Exists(game.ModsTxtPath))
            File.WriteAllText(game.ModsTxtPath, "; Built-in keybinds, do not move up!\r\nKeybinds : 1\r\n");
    }
}
