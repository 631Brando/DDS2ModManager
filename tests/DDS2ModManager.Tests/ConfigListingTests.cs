using DDS2ModManager.Services;

namespace DDS2ModManager.Tests;

/// The config list shows two kinds of file that live in different places and do different jobs.
/// Confusing them is the whole risk: UE4SS's settings sit in the mod loader's folder and change
/// the loader, not the game, and a user who edits one expecting the other gets no feedback at all.
public class ConfigListingTests : IDisposable
{
    private readonly string _root;
    private readonly GameInstallation _game;

    public ConfigListingTests()
    {
        // A project folder is identified by containing Binaries\Win64, which is also what makes
        // UE4SSRootPath resolve underneath this temp directory instead of a real install.
        _root = Path.Combine(Path.GetTempPath(), "dds2mm_cfg_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(_root, "DDS2", "Binaries", "Win64", "ue4ss"));
        _game = new GameInstallation { RootPath = _root };
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private string WriteUe4ssIni(string name, string contents = "[General]\nEnableDebugConsole = 1\n")
    {
        var path = Path.Combine(_game.UE4SSRootPath, name);
        File.WriteAllText(path, contents);
        return path;
    }

    // The point of the feature: it appears at all. It lives nowhere near the game's config folder,
    // so nothing would have listed it before.
    [Fact]
    public void Ue4ss_settings_are_listed()
    {
        WriteUe4ssIni("UE4SS-settings.ini");

        var listed = new GameConfigService(_game).GetConfigFiles();

        Assert.Contains(listed, f => f.Name == "UE4SS-settings.ini");
    }

    // The point of the wording: it must never read as a game config. Everything the window says
    // about it hangs off this flag, so if it ever defaults the wrong way the warning silently
    // stops appearing while the file still shows up.
    [Fact]
    public void Ue4ss_settings_are_not_marked_as_game_config()
    {
        WriteUe4ssIni("UE4SS-settings.ini");

        var file = new GameConfigService(_game).GetConfigFiles()
            .Single(f => f.Name == "UE4SS-settings.ini");

        Assert.False(file.IsGameConfig);
        Assert.Equal("Mod loader (UE4SS)", file.Category);
        Assert.Equal(_game.UE4SSRootPath, file.Folder);
    }

    // Found by enumerating the folder rather than by hardcoded name, so a UE4SS build that renames
    // its settings file still shows up instead of quietly vanishing from the list.
    [Fact]
    public void A_differently_named_ini_is_still_found()
    {
        WriteUe4ssIni("UE4SS-settings-v2.ini");

        var listed = new GameConfigService(_game).GetConfigFiles();

        Assert.Contains(listed, f => f.Name == "UE4SS-settings-v2.ini" && !f.IsGameConfig);
    }

    // Only the loader's own settings, not every .ini a mod happens to ship in a subfolder.
    [Fact]
    public void Only_top_level_ue4ss_ini_files_are_listed()
    {
        WriteUe4ssIni("UE4SS-settings.ini");
        var modDir = Path.Combine(_game.UE4SSModsPath, "SomeMod");
        Directory.CreateDirectory(modDir);
        File.WriteAllText(Path.Combine(modDir, "config.ini"), "x=1");

        var listed = new GameConfigService(_game).GetConfigFiles();

        Assert.DoesNotContain(listed, f => f.Name == "config.ini");
    }

    // No UE4SS installed is the normal state before the user installs it, and must not throw or
    // invent an entry.
    [Fact]
    public void Nothing_is_listed_when_ue4ss_is_absent()
    {
        Directory.Delete(_game.UE4SSRootPath, true);

        var listed = new GameConfigService(_game).GetConfigFiles();

        Assert.DoesNotContain(listed, f => !f.IsGameConfig);
    }

    // Backups are what the UE4SS installer keys "the user edited this" off, so the flag has to be
    // reported for the loader's files exactly as it is for the game's.
    [Fact]
    public void An_edited_settings_file_reports_having_a_backup()
    {
        var path = WriteUe4ssIni("UE4SS-settings.ini");
        File.WriteAllText(path + GameConfigService.BackupSuffix, "original");

        var file = new GameConfigService(_game).GetConfigFiles()
            .Single(f => f.Name == "UE4SS-settings.ini");

        Assert.True(file.HasBackup);
    }

    // The .bak this manager writes is bookkeeping, not something to offer for editing.
    [Fact]
    public void Backup_files_are_not_listed_as_editable()
    {
        var path = WriteUe4ssIni("UE4SS-settings.ini");
        File.WriteAllText(path + GameConfigService.BackupSuffix, "original");

        var listed = new GameConfigService(_game).GetConfigFiles();

        Assert.DoesNotContain(listed, f => f.Name.EndsWith(GameConfigService.BackupSuffix));
    }
}
