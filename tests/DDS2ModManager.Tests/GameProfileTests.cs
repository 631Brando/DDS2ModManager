using CUE4Parse.UE4.Versions;

namespace DDS2ModManager.Tests;

/// A GameProfile decides where the manager looks for a game's config, saves and mods. Every value in
/// it is one the code used to hardcode to DDS2, so the risk is silent wrongness: point the manager at
/// DDS1 with DDS2's config folder name and it reports "no .ini files" rather than failing loudly.
public class GameProfileTests : IDisposable
{
    private readonly List<string> _temps = [];

    public void Dispose()
    {
        foreach (var t in _temps) { try { Directory.Delete(t, true); } catch { } }
    }

    /// Builds an install whose project folder is <paramref name="projectFolder"/>, the way detection
    /// finds one: a directory containing Binaries\Win64.
    private GameInstallation Install(string projectFolder, bool ue4ssModern = false, bool ue4ssLegacy = false)
    {
        var root = Path.Combine(Path.GetTempPath(), "dds_prof_" + Guid.NewGuid().ToString("N")[..8]);
        _temps.Add(root);
        var win64 = Path.Combine(root, projectFolder, "Binaries", "Win64");
        Directory.CreateDirectory(win64);

        if (ue4ssModern) Directory.CreateDirectory(Path.Combine(win64, "ue4ss", "Mods"));
        if (ue4ssLegacy)
        {
            Directory.CreateDirectory(Path.Combine(win64, "Mods"));
            File.WriteAllText(Path.Combine(win64, "Mods", "mods.txt"), "Keybinds : 1\n");
        }

        return new GameInstallation { RootPath = root };
    }

    // ---- profile inference -------------------------------------------------------------------

    [Fact]
    public void Dds2_project_folder_selects_the_dds2_profile()
    {
        var game = Install("DrugDealerSimulator2");

        Assert.Equal("dds2", game.Profile.Id);
        Assert.Equal(EGame.GAME_UE5_3, game.Profile.EngineVersion);
    }

    [Fact]
    public void Dds1_project_folder_selects_the_dds1_profile()
    {
        var game = Install("DrugDealerSimulator");

        Assert.Equal("dds1", game.Profile.Id);
        Assert.Equal(EGame.GAME_UE4_21, game.Profile.EngineVersion);
    }

    // Everyone using this tool today has DDS2, and their settings say nothing about a game. An
    // unrecognised folder must keep behaving exactly as it did before profiles existed.
    [Fact]
    public void An_unrecognised_project_folder_falls_back_to_dds2()
    {
        var game = Install("SomeOtherUnrealGame");

        Assert.Equal(GameProfiles.Default.Id, game.Profile.Id);
        Assert.Equal("dds2", game.Profile.Id);
    }

    // Detection knows which game it went looking for, so an explicit profile has to win over a guess
    // made from the folder name.
    [Fact]
    public void An_explicit_profile_overrides_inference()
    {
        var game = Install("DrugDealerSimulator2");
        game.Profile = GameProfiles.Dds1;

        Assert.Equal("dds1", game.Profile.Id);
    }

    // ProjectName falls back to Profile.ProjectFolderName while Profile is inferred from the detected
    // name. If that fallback ever routes through ProjectName instead of the detected value it
    // recurses until the stack dies - a crash on startup for anyone whose game folder is missing.
    [Fact]
    public void A_missing_install_resolves_without_recursing()
    {
        var game = new GameInstallation { RootPath = @"Z:\definitely\not\here" };

        Assert.Null(game.DetectedProjectName);
        Assert.Equal("dds2", game.Profile.Id);
        Assert.Equal("DrugDealerSimulator2", game.ProjectName);
        Assert.False(game.IsValid);
    }

    [Fact]
    public void A_missing_install_with_an_explicit_profile_uses_that_profiles_folder_name()
    {
        var game = new GameInstallation { RootPath = @"Z:\definitely\not\here", Profile = GameProfiles.Dds1 };

        Assert.Equal("DrugDealerSimulator", game.ProjectName);
    }

    // ---- config path -------------------------------------------------------------------------

    // UE4 writes WindowsNoEditor, UE5 writes Windows. Wrong value = the Saves & Config window shows
    // an empty list instead of an error, which reads as "this game has no config".
    [Fact]
    public void Config_path_uses_the_platform_folder_for_the_engine()
    {
        Assert.EndsWith(Path.Combine("Config", "Windows"),
            Install("DrugDealerSimulator2").ConfigPath);

        Assert.EndsWith(Path.Combine("Config", "WindowsNoEditor"),
            Install("DrugDealerSimulator").ConfigPath);
    }

    // ---- save roots --------------------------------------------------------------------------

    // DDS1's Saved\SaveGames holds only a slot index and the graphics settings; the playable saves
    // are RamaSave containers in Saved\Serialized. Miss that folder and the manager tells a DDS1
    // player they have no saves at all.
    [Fact]
    public void Dds1_exposes_both_of_its_save_roots()
    {
        var roots = Install("DrugDealerSimulator").SaveRootPaths.ToList();

        Assert.Equal(2, roots.Count);
        Assert.EndsWith(Path.Combine("Saved", "SaveGames"), roots[0]);
        Assert.EndsWith(Path.Combine("Saved", "Serialized"), roots[1]);
    }

    [Fact]
    public void Dds2_has_a_single_save_root()
    {
        var roots = Install("DrugDealerSimulator2").SaveRootPaths.ToList();

        Assert.Single(roots);
        Assert.EndsWith(Path.Combine("Saved", "SaveGames"), roots[0]);
    }

    // ---- UE4SS layout ------------------------------------------------------------------------

    // THE load-bearing invariant. GameResetService does Directory.Delete(UE4SSRootPath, recursive)
    // to remove the mod loader. Under UE4SS's legacy layout "UE4SS's folder" is Binaries\Win64
    // itself - which contains the game executable. If UE4SSRootPath is ever made layout-aware,
    // "remove UE4SS" silently becomes "delete the game".
    [Fact]
    public void Ue4ss_root_is_never_the_win64_folder_itself()
    {
        foreach (var game in new[]
                 {
                     Install("DrugDealerSimulator",  ue4ssLegacy: true),
                     Install("DrugDealerSimulator2", ue4ssModern: true),
                     Install("DrugDealerSimulator"),
                 })
        {
            Assert.NotEqual(
                Path.TrimEndingDirectorySeparator(game.Win64Path),
                Path.TrimEndingDirectorySeparator(game.UE4SSRootPath));

            Assert.StartsWith(game.Win64Path, game.UE4SSRootPath);
        }
    }

    // Reading an existing install's mod list has to work against whichever layout is actually
    // present - DDS1's scene still runs the 3.0.x layout.
    [Fact]
    public void Legacy_ue4ss_mods_are_found_in_win64_mods()
    {
        var game = Install("DrugDealerSimulator", ue4ssLegacy: true);

        Assert.True(game.HasLegacyUE4SSLayout);
        Assert.Equal(Path.Combine(game.Win64Path, "Mods"), game.UE4SSModsPath);
        Assert.Equal(Path.Combine(game.Win64Path, "Mods", "mods.txt"), game.ModsTxtPath);
    }

    [Fact]
    public void Modern_ue4ss_mods_are_found_under_the_ue4ss_folder()
    {
        var game = Install("DrugDealerSimulator2", ue4ssModern: true);

        Assert.False(game.HasLegacyUE4SSLayout);
        Assert.Equal(Path.Combine(game.Win64Path, "ue4ss", "Mods"), game.UE4SSModsPath);
    }

    // A ue4ss\ folder wins even if a stale Mods\ is sitting next to it - which is exactly the state
    // an install left behind by the 3.0.1 -> 3.1 migration is in.
    [Fact]
    public void A_modern_layout_wins_over_a_leftover_legacy_mods_folder()
    {
        var game = Install("DrugDealerSimulator2", ue4ssModern: true, ue4ssLegacy: true);

        Assert.False(game.HasLegacyUE4SSLayout);
        Assert.Equal(Path.Combine(game.Win64Path, "ue4ss", "Mods"), game.UE4SSModsPath);
    }

    // Before UE4SS is installed there is neither folder. Installing is what we would do next, and we
    // only ever install the modern layout, so that is what the path must point at.
    [Fact]
    public void With_no_ue4ss_installed_the_modern_layout_is_assumed()
    {
        var game = Install("DrugDealerSimulator");

        Assert.False(game.HasLegacyUE4SSLayout);
        Assert.Equal(Path.Combine(game.Win64Path, "ue4ss", "Mods"), game.UE4SSModsPath);
    }

    // ---- the registry ------------------------------------------------------------------------

    // Ids are written into settings.json as the key for per-game state. A duplicate would make two
    // games share one set of tracked mods.
    [Fact]
    public void Profile_ids_are_unique()
    {
        var ids = GameProfiles.All.Select(p => p.Id).ToList();

        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Profiles_resolve_by_id_and_by_project_folder()
    {
        foreach (var p in GameProfiles.All)
        {
            Assert.Same(p, GameProfiles.ById(p.Id));
            Assert.Same(p, GameProfiles.ByProjectFolder(p.ProjectFolderName));
        }

        Assert.Null(GameProfiles.ById("nope"));
        Assert.Null(GameProfiles.ByProjectFolder(null));
    }

    // Only DDS2 is IoStore, and only DDS1 can load loose .uasset files - the two facts that decide
    // how a mod gets installed. Pinning them here so a future profile edit can't quietly swap them.
    [Fact]
    public void Pak_layout_and_loose_asset_support_match_the_engine()
    {
        Assert.Equal(PakLayout.IoStoreTriple, GameProfiles.Dds2.PakLayout);
        Assert.False(GameProfiles.Dds2.SupportsLooseAssets);
        Assert.True(GameProfiles.Dds2.NeedsMappings);

        Assert.Equal(PakLayout.SinglePak, GameProfiles.Dds1.PakLayout);
        Assert.True(GameProfiles.Dds1.SupportsLooseAssets);
        Assert.False(GameProfiles.Dds1.NeedsMappings);
    }
}
