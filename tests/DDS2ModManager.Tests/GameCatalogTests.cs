using System.Reflection;
using DDS2ModManager.Services;

namespace DDS2ModManager.Tests;

/// The game picker's catalog: what gets listed, in what order, what opens on startup, and how an
/// install keeps its identity - and its data - however its folder is spelled.
public class GameCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dds2mm_cat_" + Guid.NewGuid().ToString("N")[..8]);

    public GameCatalogTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private string MakeGame(string folder, string project, bool installed = true)
    {
        var root = Path.Combine(_root, folder);
        var win64 = Path.Combine(root, project, "Binaries", "Win64");
        var paks = Path.Combine(root, project, "Content", "Paks");
        Directory.CreateDirectory(win64);
        Directory.CreateDirectory(paks);
        if (installed)
        {
            File.WriteAllBytes(Path.Combine(win64, $"{project}-Win64-Shipping.exe"), new byte[16]);
            File.WriteAllBytes(Path.Combine(paks, "pakchunk0-WindowsNoEditor.pak"), new byte[16]);
        }
        return root;
    }

    private static DetectedGame Entry(GameProfile profile, CatalogState state, string root = "") =>
        new() { RootPath = root, Profile = profile, State = state };

    // ---- what a folder is -------------------------------------------------------------------

    [Fact]
    public void An_installed_game_is_listed_as_installed()
    {
        var entry = new GameCatalogService().Inspect(MakeGame("Mordhau", "Mordhau"));

        Assert.NotNull(entry);
        Assert.Equal(CatalogState.Installed, entry!.State);
        Assert.Equal("Mordhau", entry.ProjectName);
        Assert.False(entry.Profile.IsBuiltIn);
    }

    [Fact]
    public void A_husk_is_listed_as_a_leftover_not_an_install()
    {
        var entry = new GameCatalogService().Inspect(MakeGame("Gone", "GoneGame", installed: false));

        Assert.Equal(CatalogState.Leftover, entry!.State);
    }

    [Fact]
    public void A_folder_with_no_unreal_game_is_not_listed()
    {
        var folder = Path.Combine(_root, "Valheim");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "valheim.exe"), new byte[16]);

        Assert.Null(new GameCatalogService().Inspect(folder));
    }

    // Per-install files are named by a hash of RootPath exactly as stored. Normalising it here
    // would orphan a remembered install's registry, backups and disabled mods.
    [Fact]
    public void The_folder_is_kept_exactly_as_it_was_spelled()
    {
        var root = MakeGame("Spelled", "SpelledGame") + "\\";

        var entry = new GameCatalogService().Inspect(root);

        Assert.Equal(root, entry!.RootPath);
    }

    // ...while matching treats two spellings of one folder as one install.
    [Fact]
    public void Two_spellings_of_one_folder_are_one_install()
    {
        var root = MakeGame("Twice", "TwiceGame");
        var catalog = new GameCatalogService();

        var a = catalog.Inspect(root)!;
        var b = catalog.Inspect(root.ToUpperInvariant() + "\\")!;

        Assert.Equal(a.Key, b.Key);
    }

    // ---- order and startup ------------------------------------------------------------------

    [Fact]
    public void A_remembered_folder_a_scan_also_finds_is_not_added_by_hand()
    {
        // An uninstalled Steam game: no manifest any more, but its leftover folder is still in
        // steamapps\common, so the library walk lists it whatever the setting says.
        const string leftover = @"C:\Program Files (x86)\Steam\steamapps\common\Drug Dealer Simulator 2";
        const string remembered = @"c:\program files (x86)\steam\steamapps\common\Drug Dealer Simulator 2";

        var merged = GameCatalogService.MergeRemembered([leftover], [remembered]);

        var entry = Assert.Single(merged).Value;
        Assert.False(entry.ByHand);
        Assert.Equal(remembered, entry.Spelling);   // the spelling its per-install files were named from
    }

    [Fact]
    public void A_folder_only_the_settings_know_is_added_by_hand()
    {
        var custom = Path.Combine(_root, "Games", "Somewhere");

        // Two settings sections remembering one folder must not talk each other out of it.
        var merged = GameCatalogService.MergeRemembered(
            [Path.Combine(_root, "Library", "Other")], [custom, custom + Path.DirectorySeparatorChar]);

        Assert.True(merged[GameStoreIndex.NormalizeFolder(custom)].ByHand);
        Assert.False(merged[GameStoreIndex.NormalizeFolder(Path.Combine(_root, "Library", "Other"))].ByHand);
        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void Built_in_games_come_first_then_everything_else_by_name()
    {
        var zed = GenericGameProfiles.Create(MakeGame("Zed", "Zed"), "Zed", null, readExecutable: false);
        var alpha = GenericGameProfiles.Create(MakeGame("Alpha", "Alpha"), "Alpha", null, readExecutable: false);

        var ordered = GameCatalogService.Order(new[]
        {
            Entry(zed, CatalogState.Installed, "z"),
            Entry(GameProfiles.Dds2, CatalogState.Installed, "d2"),
            Entry(alpha, CatalogState.Installed, "a"),
            Entry(GameProfiles.Dds1, CatalogState.NotFound)
        }).Select(g => g.Profile.DisplayName).ToList();

        Assert.Equal(new[] { GameProfiles.Dds1.DisplayName, GameProfiles.Dds2.DisplayName, "Alpha", "Zed" }, ordered);
    }

    // DDS2 first, as it has always been for anyone with both games.
    [Fact]
    public void Startup_prefers_dds2_then_dds1()
    {
        var both = new[]
        {
            Entry(GameProfiles.Dds1, CatalogState.Installed, "d1"),
            Entry(GameProfiles.Dds2, CatalogState.Installed, "d2")
        };

        Assert.Equal("dds2", GameCatalogService.DefaultStartupGame(both)!.Profile.Id);
    }

    // Opening a game mounts every pak it has. Nothing about one being installed says the user wants
    // to mod it, so a game with no profile of its own is never picked for them.
    [Fact]
    public void Startup_never_opens_a_game_the_user_did_not_pick()
    {
        var mordhau = GenericGameProfiles.Create(MakeGame("M", "Mordhau"), "Mordhau", null, readExecutable: false);

        Assert.Null(GameCatalogService.DefaultStartupGame(new[] { Entry(mordhau, CatalogState.Installed, "m") }));
    }

    [Fact]
    public void Startup_never_opens_a_leftover()
    {
        Assert.Null(GameCatalogService.DefaultStartupGame(new[] { Entry(GameProfiles.Dds2, CatalogState.Leftover, "d2") }));
    }

    // ---- identity across sessions -----------------------------------------------------------

    // A game added by hand before its Steam manifest existed was "path:..."; once Steam writes the
    // manifest it would derive as "steam:...". Switching keys would orphan its settings.
    [Fact]
    public void An_existing_settings_section_for_the_folder_keeps_its_id()
    {
        var root = MakeGame("Keyed", "KeyedGame");
        var games = AppSettingsService.Instance.Current.Games;
        var oldKey = "path:" + Guid.NewGuid().ToString("N")[..12];

        games[oldKey] = new GameSettings { GamePathOverride = root };
        try
        {
            Assert.Equal(oldKey, GenericGameProfiles.StableId("steam:999999", root));
        }
        finally
        {
            games.Remove(oldKey);
        }
    }

    [Fact]
    public void Without_a_section_the_derived_id_is_used()
    {
        var root = MakeGame("Fresh", "FreshGame");

        Assert.Equal("steam:424242", GenericGameProfiles.StableId("steam:424242", root));
    }

    // ---- mappings ---------------------------------------------------------------------------

    // UE4SS names a dump after the project it came from, and copying a ue4ss folder over from
    // another game is a common way to install it. Another game's usmap fails silently.
    [Fact]
    public void Another_games_dumped_usmap_is_never_used()
    {
        var root = MakeGame("Copied", "CopiedGame");
        var game = new GameInstallation
        {
            RootPath = root,
            Profile = GenericGameProfiles.Create(root, "CopiedGame", null, readExecutable: false)
        };
        Directory.CreateDirectory(game.UE4SSRootPath);
        File.WriteAllBytes(Path.Combine(game.UE4SSRootPath, "DrugDealerSimulator2-5.3.2-0+UE5-0c574377.usmap"), new byte[64]);

        Assert.Null(GameMountService.FindDumpedMappings(game));
    }

    [Fact]
    public void This_games_dumped_usmap_is_used()
    {
        var root = MakeGame("Own", "OwnGame");
        var game = new GameInstallation
        {
            RootPath = root,
            Profile = GenericGameProfiles.Create(root, "OwnGame", null, readExecutable: false)
        };
        Directory.CreateDirectory(game.UE4SSRootPath);
        var own = Path.Combine(game.UE4SSRootPath, "OwnGame-5.1.1-0+UE5.usmap");
        File.WriteAllBytes(own, new byte[64]);

        Assert.Equal(own, GameMountService.FindDumpedMappings(game));
    }

    // ---- where a generic patch mod goes -----------------------------------------------------

    private static void InstallPak(GameInstallation game, string workingDir, ModInfo mod)
    {
        var installer = new ModInstallerService(
            game,
            new ModAnalyzerService(game, "", game.Profile.EngineVersion),
            new ModRegistryService(Path.Combine(Path.GetTempPath(), "dds2mm_reg_" + Guid.NewGuid().ToString("N") + ".json")));

        var method = typeof(ModInstallerService).GetMethod("InstallPakTriple", BindingFlags.Instance | BindingFlags.NonPublic)
                     ?? throw new InvalidOperationException("InstallPakTriple not found - was it renamed?");
        method.Invoke(installer, [workingDir, game.PaksPath, mod]);
    }

    // At the top of Content\Paks a copy that overwrites can replace one of the game's own paks
    // whose name this manager doesn't know - and uninstalling the mod would delete it.
    [Fact]
    public void A_generic_games_patch_mod_goes_into_its_own_tilde_mods_folder()
    {
        var root = MakeGame("Gen", "GenGame");
        var game = new GameInstallation
        {
            RootPath = root,
            Profile = GenericGameProfiles.Create(root, "GenGame", null, readExecutable: false)
        };

        var src = Path.Combine(_root, "src_mod");
        Directory.CreateDirectory(src);
        File.WriteAllBytes(Path.Combine(src, "pakchunk0-WindowsNoEditor.pak"), new byte[] { 1, 2, 3 });   // collides with a base name

        var mod = new ModInfo { Name = "Collider", Type = ModType.PatchMod };
        InstallPak(game, src, mod);

        var expected = Path.Combine(game.PaksPath, "~mods", "pakchunk0-WindowsNoEditor");
        Assert.Equal(expected, mod.InstallPath);
        Assert.Equal(16, new FileInfo(Path.Combine(game.PaksPath, "pakchunk0-WindowsNoEditor.pak")).Length);   // base pak untouched
    }

    // DDS1/DDS2's base names ARE known, so their patch mods keep going where they always have.
    [Fact]
    public void A_built_in_games_patch_mod_still_goes_to_the_top_of_paks()
    {
        var root = MakeGame("DrugDealerSimulator", "DrugDealerSimulator");
        var game = new GameInstallation { RootPath = root, Profile = GameProfiles.Dds1 };

        var src = Path.Combine(_root, "src_dds1");
        Directory.CreateDirectory(src);
        File.WriteAllBytes(Path.Combine(src, "BetterPrices_P.pak"), new byte[] { 1 });

        var mod = new ModInfo { Name = "BetterPrices", Type = ModType.PatchMod };
        InstallPak(game, src, mod);

        Assert.Equal(game.PaksPath, mod.InstallPath);
    }
}
