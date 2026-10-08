using DDS2ModManager.Services;

namespace DDS2ModManager.Tests;

/// Profiles for Unreal games this manager has no specific knowledge of. The rules here are about
/// identity (a game must be the same game however it was found) and caution (anything that can't be
/// read from the install is answered "no").
public class GenericGameTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dds2mm_gen_" + Guid.NewGuid().ToString("N")[..8]);

    public GenericGameTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private string MakeGame(string folder, string project, bool ioStore = false)
    {
        var root = Path.Combine(_root, folder);
        var win64 = Path.Combine(root, project, "Binaries", "Win64");
        var paks = Path.Combine(root, project, "Content", "Paks");
        Directory.CreateDirectory(win64);
        Directory.CreateDirectory(paks);
        File.WriteAllBytes(Path.Combine(win64, $"{project}-Win64-Shipping.exe"), new byte[16]);
        File.WriteAllBytes(Path.Combine(paks, $"pakchunk0-WindowsClient.pak"), new byte[16]);
        if (ioStore) File.WriteAllBytes(Path.Combine(paks, "global.utoc"), new byte[16]);
        return root;
    }

    // ---- identity ---------------------------------------------------------------------------

    [Fact]
    public void A_steam_game_is_keyed_by_its_app_id()
    {
        var id = GenericGameProfiles.IdFor(new StoreIdentity(GameStore.Steam, "629760", "MORDHAU", 629760), @"E:\x\Mordhau");

        Assert.Equal("steam:629760", id);
    }

    [Fact]
    public void Epic_and_gog_games_are_keyed_by_their_store_ids()
    {
        Assert.Equal("epic:fortnite", GenericGameProfiles.IdFor(new StoreIdentity(GameStore.Epic, "Fortnite", "Fortnite", 0), @"C:\a"));
        Assert.Equal("gog:1234", GenericGameProfiles.IdFor(new StoreIdentity(GameStore.Gog, "1234", "Game", 0), @"C:\b"));
    }

    // A folder no launcher knows about uses the per-install key every per-install file already
    // uses - one hashing scheme, not two.
    [Fact]
    public void An_unknown_folder_is_keyed_by_the_existing_install_key()
    {
        var root = MakeGame("Loose", "LooseGame");

        var id = GenericGameProfiles.IdFor(null, root);

        Assert.Equal("path:" + AppPaths.GameKey(GameStoreIndex.NormalizeFolder(root)), id);
    }

    // The same folder reached two ways - a picker's trailing slash, different case - is one game.
    [Fact]
    public void The_same_folder_spelled_differently_gets_the_same_id()
    {
        var root = MakeGame("Spelled", "SpelledGame");

        Assert.Equal(GenericGameProfiles.IdFor(null, root),
                     GenericGameProfiles.IdFor(null, root.ToUpperInvariant() + "\\"));
    }

    // Prefixed, so no generic id can ever collide with a built-in one.
    [Theory]
    [InlineData("steam:1")]
    [InlineData("epic:x")]
    [InlineData("gog:1")]
    [InlineData("path:abcdef012345")]
    public void Generic_ids_are_recognisable_and_never_built_in(string id)
    {
        Assert.True(GenericGameProfiles.IsGenericId(id));
        Assert.Null(GameProfiles.ById(id));
    }

    [Fact]
    public void Built_in_ids_are_not_generic()
    {
        Assert.False(GenericGameProfiles.IsGenericId("dds1"));
        Assert.False(GenericGameProfiles.IsGenericId("dds2"));
    }

    // ---- resolving a folder to a profile ----------------------------------------------------

    // DDS1 found through its Steam manifest must be "dds1", not a generic "steam:682990" whose
    // settings would be split from the section every earlier version wrote.
    [Fact]
    public void A_built_in_game_found_by_app_id_resolves_to_its_built_in_profile()
    {
        var root = MakeGame("RenamedFolder", "SomethingElse");
        var dds1 = new StoreIdentity(GameStore.Steam, "682990", "Drug Dealer Simulator", 682990);

        Assert.Same(GameProfiles.Dds1, GenericGameProfiles.Resolve(root, "SomethingElse", dds1, readExecutable: false));
    }

    [Fact]
    public void A_built_in_game_resolves_by_project_folder()
    {
        var root = MakeGame("Drug Dealer Simulator 2", "DrugDealerSimulator2");

        Assert.Same(GameProfiles.Dds2, GenericGameProfiles.Resolve(root, "DrugDealerSimulator2", null, readExecutable: false));
    }

    [Fact]
    public void Any_other_unreal_game_resolves_to_a_generic_profile()
    {
        var root = MakeGame("Mordhau", "Mordhau");
        var steam = new StoreIdentity(GameStore.Steam, "629760", "MORDHAU", 629760);

        var p = GenericGameProfiles.Resolve(root, "Mordhau", steam, readExecutable: false);

        Assert.False(p.IsBuiltIn);
        Assert.Equal("steam:629760", p.Id);
        Assert.Equal("MORDHAU", p.DisplayName);
        Assert.Equal(629760u, p.SteamAppId);
    }

    [Fact]
    public void BySteamAppId_finds_built_ins_and_nothing_else()
    {
        Assert.Same(GameProfiles.Dds1, GameProfiles.BySteamAppId(682990));
        Assert.Same(GameProfiles.Dds2, GameProfiles.BySteamAppId(1708850));
        Assert.Null(GameProfiles.BySteamAppId(629760));
        Assert.Null(GameProfiles.BySteamAppId(0));
    }

    // A persisted generic id has to come back as a profile once the game has been seen this session,
    // without ById - which every caller relies on to mean "built-in" - ever returning it.
    [Fact]
    public void A_registered_generic_profile_resolves_by_id_but_never_through_ById()
    {
        var root = MakeGame("Registered", "RegisteredGame");
        var p = GenericGameProfiles.Create(root, "RegisteredGame", null, readExecutable: false);

        Assert.Same(p, GameProfiles.Resolve(p.Id));
        Assert.Null(GameProfiles.ById(p.Id));
        Assert.DoesNotContain(GameProfiles.All, x => x.Id == p.Id);
    }

    // ---- what a generic profile claims ------------------------------------------------------

    [Fact]
    public void A_generic_profile_permits_only_the_loaders_its_engine_supports()
    {
        var p = GenericGameProfiles.Create(MakeGame("NoLoader", "NoLoader"), "NoLoader", null, readExecutable: false);

        Assert.Equal(LoaderCompatibility.InstallableFor(p.EngineVersion), p.InstallableLoaders);
        Assert.True(p.SupportedLoaders.HasFlag(ModLoaders.UE4SS));
        Assert.False(p.InstallableLoaders.HasFlag(ModLoaders.UnrealModUnlocker));   // never fetchable
    }

    [Fact]
    public void Pak_layout_follows_what_is_on_disk()
    {
        var ue4 = GenericGameProfiles.Create(MakeGame("Ue4", "Ue4Game"), "Ue4Game", null, readExecutable: false);
        var ue5 = GenericGameProfiles.Create(MakeGame("Ue5", "Ue5Game", ioStore: true), "Ue5Game", null, readExecutable: false);

        Assert.Equal(PakLayout.SinglePak, ue4.PakLayout);
        Assert.Equal(PakLayout.IoStoreTriple, ue5.PakLayout);
        Assert.True(ue5.NeedsMappings);
    }

    // The cook platform in the pak name decides the config folder before the game has ever run -
    // Mordhau's paks are *-WindowsClient, and so is its config.
    [Fact]
    public void The_config_folder_follows_the_pak_cook_platform()
    {
        var p = GenericGameProfiles.Create(MakeGame("Client", "ClientGame"), "ClientGame", null, readExecutable: false);

        Assert.Equal("WindowsClient", p.ConfigPlatformDir);
    }

    [Theory]
    [InlineData(@"C:\x\pakchunk0-WindowsClient.pak", "WindowsClient")]
    [InlineData(@"C:\x\DrugDealerSimulator-WindowsNoEditor.pak", "WindowsNoEditor")]
    [InlineData(@"C:\x\pakchunk0-Windows.pak", "Windows")]
    [InlineData(@"C:\x\pakchunk0-Windows_0_P.pak", "Windows")]
    [InlineData(@"C:\x\custom.pak", null)]
    [InlineData(@"C:\x\Game-Linux.pak", null)]
    public void Cook_platform_is_read_from_a_pak_name(string pak, string? expected) =>
        Assert.Equal(expected, UnrealInstallInspector.CookPlatformFromPakName(pak));

    [Fact]
    public void The_launcher_name_is_used_when_there_is_one()
    {
        var p = GenericGameProfiles.Create(MakeGame("Named", "PrjX"), "PrjX",
            new StoreIdentity(GameStore.Epic, "PrjX", "Project X: Remastered", 0), readExecutable: false);

        Assert.Equal("Project X: Remastered", p.DisplayName);
    }

    [Theory]
    [InlineData("DrugDealerSimulator", "Drug Dealer Simulator")]
    [InlineData("ABInfinite", "AB Infinite")]
    [InlineData("Subnautica2", "Subnautica 2")]
    [InlineData("Mordhau", "Mordhau")]
    [InlineData("Ready_Or_Not", "Ready Or Not")]
    public void A_project_folder_name_is_made_readable(string project, string expected) =>
        Assert.Equal(expected, GenericGameProfiles.Prettify(project));

    // ---- mappings ---------------------------------------------------------------------------

    private GameInstallation GenericInstall(string folder, string project)
    {
        var root = MakeGame(folder, project, ioStore: true);
        return new GameInstallation
        {
            RootPath = root,
            Profile = GenericGameProfiles.Create(root, project, null, readExecutable: false)
        };
    }

    // The bug this replaced: NeedsMappings meant "extract the embedded file", and the embedded file
    // is DDS2's. A wrong usmap fails silently - paths still list, values come back as garbage.
    [Fact]
    public void A_generic_game_without_a_usmap_gets_none_rather_than_dds2s()
    {
        var game = GenericInstall("NoMappings", "NoMappingsGame");

        Assert.Equal("", GameMountService.ResolveMappings(game, overridePath: null));
    }

    [Fact]
    public void A_usmap_ue4ss_dumped_for_the_game_is_used()
    {
        var game = GenericInstall("Dumped", "DumpedGame");
        Directory.CreateDirectory(game.UE4SSRootPath);
        var usmap = Path.Combine(game.UE4SSRootPath, "DumpedGame-5.3.2-0+UE5.usmap");
        File.WriteAllBytes(usmap, new byte[64]);

        Assert.Equal(usmap, GameMountService.ResolveMappings(game, overridePath: null));
    }

    [Fact]
    public void The_users_override_wins()
    {
        var game = GenericInstall("Override", "OverrideGame");
        Directory.CreateDirectory(game.UE4SSRootPath);
        File.WriteAllBytes(Path.Combine(game.UE4SSRootPath, "Mappings.usmap"), new byte[64]);
        var mine = Path.Combine(_root, "mine.usmap");
        File.WriteAllBytes(mine, new byte[64]);

        Assert.Equal(mine, GameMountService.ResolveMappings(game, mine));
    }

    // DDS1's real install holds a 4.27.2 usmap left by a manual engine override. DDS1 needs no
    // mappings at all, and reading it with that file would be wrong - built-ins never auto-discover.
    [Fact]
    public void A_built_in_game_never_picks_up_a_stray_usmap()
    {
        var root = MakeGame("DrugDealerSimulator", "DrugDealerSimulator");
        var game = new GameInstallation { RootPath = root, Profile = GameProfiles.Dds1 };
        Directory.CreateDirectory(game.UE4SSRootPath);
        File.WriteAllBytes(Path.Combine(game.UE4SSRootPath, "DrugDealerSimulator-4.27.2.usmap"), new byte[64]);

        Assert.Equal("", GameMountService.ResolveMappings(game, overridePath: null));
    }

    [Fact]
    public void Only_dds2_has_embedded_mappings()
    {
        Assert.True(GameProfiles.Dds2.HasEmbeddedMappings);
        Assert.False(GameProfiles.Dds1.HasEmbeddedMappings);
    }

    // ---- launcher manifests -----------------------------------------------------------------

    [Fact]
    public void A_steam_manifest_is_parsed()
    {
        const string acf = """
            "AppState"
            {
            	"appid"		"629760"
            	"Universe"		"1"
            	"name"		"MORDHAU"
            	"StateFlags"		"4"
            	"installdir"		"Mordhau"
            }
            """;

        var parsed = GameStoreIndex.ParseSteamManifest(acf);

        Assert.Equal((629760u, "MORDHAU", "Mordhau"), parsed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\"AppState\" { \"name\" \"x\" }")]
    [InlineData("\"appid\" \"0\" \"installdir\" \"x\"")]
    public void An_unusable_steam_manifest_is_ignored(string acf) =>
        Assert.Null(GameStoreIndex.ParseSteamManifest(acf));

    [Fact]
    public void An_epic_manifest_is_parsed()
    {
        const string json = """
            { "DisplayName": "Some Game", "InstallLocation": "C:\\Program Files\\Epic Games\\SomeGame", "AppName": "abc123", }
            """;

        var parsed = GameStoreIndex.ParseEpicManifest(json);

        Assert.Equal(("abc123", "Some Game", @"C:\Program Files\Epic Games\SomeGame"), parsed);
    }

    [Fact]
    public void A_broken_epic_manifest_is_ignored() =>
        Assert.Null(GameStoreIndex.ParseEpicManifest("{ not json"));
}
