using CUE4Parse.UE4.Versions;

namespace DDS2ModManager.Tests;

/// Installing a mod loader on any game whose engine supports it: UE4SS on either release line, and
/// UnrealModLoader on UE4. Everything here runs against payloads built on disk - the download is the
/// only step not exercised.
public class LoaderInstallTests : IDisposable
{
    private readonly List<string> _temps = [];

    public void Dispose()
    {
        foreach (var t in _temps) { try { Directory.Delete(t, true); } catch { } }
    }

    private static EGame Ue(int major, int minor) => (EGame)((major << 24) | (minor << 16));

    private string Temp(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N")[..8]);
        _temps.Add(dir);
        return dir;
    }

    private static void Write(string path, string text = "x")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    /// A real install - an executable and the game's own paks - so IsInstalled holds.
    private GameInstallation Game(string project, GameProfile profile)
    {
        var root = Temp("dds_ld_");
        Write(Path.Combine(root, project, "Binaries", "Win64", $"{project}-Win64-Shipping.exe"));
        Write(Path.Combine(root, project, "Content", "Paks", $"{project}-WindowsNoEditor.pak"));
        var game = new GameInstallation { RootPath = root, Profile = profile };

        // Undo copies live in the developer's real AppData; never leave one behind.
        _temps.Add(AppPaths.PreviousUE4SSFor(root));
        return game;
    }

    private GameInstallation Dds1() => Game("DrugDealerSimulator", GameProfiles.Dds1);
    private GameInstallation Dds2() => Game("DrugDealerSimulator2", GameProfiles.Dds2);

    // ---- what each loader supports ---------------------------------------------------------------

    [Fact]
    public void Engine_versions_decode_from_cue4parse_values()
    {
        Assert.Equal((4, 21), LoaderCompatibility.VersionOf(EGame.GAME_UE4_21));
        Assert.Equal((5, 3), LoaderCompatibility.VersionOf(EGame.GAME_UE5_3));
        Assert.Equal((4, 21), LoaderCompatibility.VersionOf(UnrealEngineProbe.ToEGame(4, 21)));
    }

    [Theory]
    [InlineData(4, 5, ModLoaders.UnrealModLoader)]                        // below UE4SS's 4.7 floor
    [InlineData(4, 21, ModLoaders.UE4SS | ModLoaders.UnrealModLoader)]
    [InlineData(4, 27, ModLoaders.UE4SS | ModLoaders.UnrealModLoader)]
    [InlineData(5, 3, ModLoaders.UE4SS)]                                  // UML is UE4 only
    [InlineData(5, 9, ModLoaders.None)]                                   // past experimental's 5.8
    public void A_generic_game_may_install_what_its_engine_supports(int major, int minor, ModLoaders expected)
    {
        Assert.Equal(expected, LoaderCompatibility.InstallableFor(Ue(major, minor)));
    }

    [Fact]
    public void Stable_is_offered_on_dds2_only_with_its_crash_named()
    {
        var stable = LoaderCompatibility.ForUE4SS(GameProfiles.Dds2, UE4SSChannel.Stable);
        Assert.True(stable.Available);
        Assert.Contains("crashes Drug Dealer Simulator 2", stable.Warning);

        var experimental = LoaderCompatibility.ForUE4SS(GameProfiles.Dds2, UE4SSChannel.Experimental);
        Assert.True(experimental.Available);
        Assert.Null(experimental.Warning);
    }

    [Fact]
    public void Stable_warns_about_blueprint_mods_on_ue5_and_stops_at_5_3()
    {
        var generic = GameProfiles.Dds2 with
        {
            Id = "steam:1", IsBuiltIn = false, UE4SSStableCaveat = null, EngineLabel = "UE 5.1",
            EngineVersion = Ue(5, 1), InstallableLoaders = LoaderCompatibility.InstallableFor(Ue(5, 1))
        };

        var onUe51 = LoaderCompatibility.ForUE4SS(generic, UE4SSChannel.Stable);
        Assert.True(onUe51.Available);
        Assert.Contains("Blueprint", onUe51.Warning);

        var onUe54 = LoaderCompatibility.ForUE4SS(generic with { EngineVersion = Ue(5, 4), EngineLabel = "UE 5.4" },
            UE4SSChannel.Stable);
        Assert.False(onUe54.Available);
        Assert.Contains("UE 4.12 to 5.3", onUe54.BlockedReason);
    }

    [Fact]
    public void Unreal_mod_loader_is_ue4_only()
    {
        Assert.True(LoaderCompatibility.ForUnrealModLoader(GameProfiles.Dds1).Available);
        Assert.False(LoaderCompatibility.ForUnrealModLoader(GameProfiles.Dds2).Available);
    }

    [Theory]
    [InlineData(17, 0, 0, 0)]
    [InlineData(21, 0, 1, 0)]   // DDS1
    [InlineData(22, 0, 1, 1)]
    [InlineData(25, 1, 1, 0)]
    public void The_written_uml_profile_follows_the_engine_version(int minor, int namePool, int chunked, int is422)
    {
        var profile = LoaderCompatibility.BuildUnrealModLoaderProfile(4, minor);

        Assert.Contains("[GameInfo]", profile);
        Assert.Contains($"UsesFNamePool={namePool}", profile);
        Assert.Contains($"IsUsingFChunkedFixedUObjectArray={chunked}", profile);
        Assert.Contains($"IsUsing4_22={is422}", profile);
        Assert.Contains("IsUsingDeferedSpawn=0", profile);
    }

    // ---- UnrealModLoader ---------------------------------------------------------------------------

    [Theory]
    [InlineData("https://github.com/RussellJerome/UnrealModLoader/releases/download/v2.2.1/UnrealModLoader_V2.2.1.rar", true)]
    [InlineData("http://github.com/RussellJerome/UnrealModLoader/releases/download/v2.2.1/UnrealModLoader_V2.2.1.rar", false)]
    [InlineData("https://github.com/SomeoneElse/UnrealModLoader/releases/download/v2.2.1/UnrealModLoader_V2.2.1.rar", false)]
    [InlineData("https://github.com.evil.example/RussellJerome/UnrealModLoader/releases/download/v2.2.1/x.rar", false)]
    [InlineData("https://user@github.com/RussellJerome/UnrealModLoader/releases/download/v2.2.1/x.rar", false)]
    [InlineData("https://github.com:8443/RussellJerome/UnrealModLoader/releases/download/v2.2.1/x.rar", false)]
    [InlineData("https://github.com/RussellJerome/UnrealModLoader/archive/refs/heads/main.zip", false)]
    [InlineData(null, false)]
    public void Uml_downloads_only_from_its_official_release_page(string? url, bool official)
    {
        Assert.Equal(official, UnrealModLoaderService.IsOfficialDownload(url));
    }

    [Fact]
    public void Uml_ignores_an_asset_not_served_from_its_release_page()
    {
        var release = new GitHubReleaseInfo { TagName = "v2.2.1" };
        release.Assets.Add(new GitHubAsset { Name = "UnrealModLoader_V2.2.1.rar", BrowserDownloadUrl = "https://example.com/UnrealModLoader_V2.2.1.rar" });
        Assert.Null(UnrealModLoaderService.FindAsset(release));

        release.Assets.Add(new GitHubAsset
        {
            Name = "UnrealModLoader_V2.2.1.rar",
            BrowserDownloadUrl = "https://github.com/RussellJerome/UnrealModLoader/releases/download/v2.2.1/UnrealModLoader_V2.2.1.rar"
        });
        Assert.NotNull(UnrealModLoaderService.FindAsset(release));
    }

    /// The shape of UnrealModLoader_V2.2.1.rar.
    private string UmlPayload(params string[] profiles)
    {
        var dir = Temp("dds_uml_");
        Write(Path.Combine(dir, "UnrealEngineModLoader.dll"), "loader");
        Write(Path.Combine(dir, "UnrealEngineModLauncher.exe"), "launcher");
        Write(Path.Combine(dir, "ModLoaderInfo.ini"), "[DEBUG]\r\nUseConsole=1\r\n");
        Write(Path.Combine(dir, "Tools", "AutoInjector", "xinput1_3.dll"), "proxy");
        Write(Path.Combine(dir, "Tools", "AutoInjector", "ModLoaderInfo.ini"), "[INFO]\r\nLoaderPath=C:\\Users\\Russell\\x.dll\r\n");
        Write(Path.Combine(dir, "Profiles", "BasicExampleGame.profile"), "[GameInfo]");
        foreach (var p in profiles) Write(Path.Combine(dir, "Profiles", p), "[GameInfo]\r\nshipped=1");
        return dir;
    }

    [Fact]
    public void Uml_installs_its_autoinjector_flat_in_win64_with_a_profile_for_the_game()
    {
        var game = Dds1();

        Assert.True(UnrealModLoaderService.ApplyExtracted(game, UmlPayload(), "v2.2.1", "UnrealModLoader_V2.2.1.rar"));

        var win64 = game.Win64Path;
        Assert.True(File.Exists(Path.Combine(win64, "UnrealEngineModLoader.dll")));
        Assert.True(File.Exists(Path.Combine(win64, "xinput1_3.dll")));
        Assert.False(File.Exists(Path.Combine(win64, "UnrealEngineModLauncher.exe")));   // never a second exe

        var info = File.ReadAllText(Path.Combine(win64, "ModLoaderInfo.ini"));
        Assert.Contains($"LoaderPath={Path.Combine(win64, "UnrealEngineModLoader.dll")}", info);
        Assert.Contains("[DEBUG]", info);
        Assert.Contains("UseConsole=0", info);

        // DDS1 is UE 4.21: no FNamePool, chunked object array.
        var profile = File.ReadAllText(Path.Combine(win64, "Profiles", "DrugDealerSimulator-Win64-Shipping.profile"));
        Assert.Contains("UsesFNamePool=0", profile);
        Assert.Contains("IsUsingFChunkedFixedUObjectArray=1", profile);

        var status = UnrealModLoaderService.GetStatus(game);
        Assert.True(status.IsManagedByUs);
        Assert.Equal("v2.2.1", status.InstalledVersion);

        var detected = new ModLoaderService().Detect(game, ModLoaders.UnrealModLoader)!;
        Assert.True(detected.IsManagedByUs);
        Assert.Equal(Path.Combine(game.ContentPath, "CoreMods"), detected.PluginFolder);
    }

    [Fact]
    public void Uml_uses_its_own_profile_when_it_ships_one()
    {
        var game = Dds1();

        UnrealModLoaderService.ApplyExtracted(game,
            UmlPayload("DrugDealerSimulator-Win64-Shipping.profile"), "v2.2.1", "a.rar");

        Assert.Contains("shipped=1",
            File.ReadAllText(Path.Combine(game.Win64Path, "Profiles", "DrugDealerSimulator-Win64-Shipping.profile")));
        Assert.Null(UnrealModLoaderService.ReadManifest(game)!.GeneratedProfile);
    }

    [Fact]
    public void Reinstalling_uml_keeps_a_tuned_profile_and_the_console_choice()
    {
        var game = Dds1();
        UnrealModLoaderService.ApplyExtracted(game, UmlPayload(), "v2.2.1", "a.rar");

        var profilePath = Path.Combine(game.Win64Path, "Profiles", "DrugDealerSimulator-Win64-Shipping.profile");
        File.WriteAllText(profilePath, "[GameInfo]\r\ntuned=1");
        var infoPath = Path.Combine(game.Win64Path, "ModLoaderInfo.ini");
        File.WriteAllText(infoPath, File.ReadAllText(infoPath).Replace("UseConsole=0", "UseConsole=1"));

        Assert.True(UnrealModLoaderService.ApplyExtracted(game, UmlPayload(), "v2.2.1", "a.rar"));

        Assert.Contains("tuned=1", File.ReadAllText(profilePath));
        Assert.Contains("UseConsole=1", File.ReadAllText(infoPath));

        // Still ours, so a later Remove still takes it.
        Assert.Contains(Path.Combine("Profiles", "DrugDealerSimulator-Win64-Shipping.profile"),
            UnrealModLoaderService.ReadManifest(game)!.Files);
    }

    [Fact]
    public void Reinstalling_uml_never_overwrites_a_tuned_profile_it_ships()
    {
        var game = Dds1();
        var shipped = UmlPayload("DrugDealerSimulator-Win64-Shipping.profile");
        UnrealModLoaderService.ApplyExtracted(game, shipped, "v2.2.1", "a.rar");

        var profile = Path.Combine(game.Win64Path, "Profiles", "DrugDealerSimulator-Win64-Shipping.profile");
        File.WriteAllText(profile, "[GameInfo]\r\nIsUsingDeferedSpawn=1");

        Assert.True(UnrealModLoaderService.ApplyExtracted(game, shipped, "v2.2.1", "a.rar"));
        Assert.Contains("IsUsingDeferedSpawn=1", File.ReadAllText(profile));

        // And Remove leaves the game's own profile for the next install.
        Assert.True(UnrealModLoaderService.Remove(game));
        Assert.Contains("IsUsingDeferedSpawn=1", File.ReadAllText(profile));
    }

    [Fact]
    public void Uml_is_refused_over_a_launcher_setup_it_did_not_make()
    {
        var game = Dds1();
        Write(Path.Combine(game.Win64Path, "UnrealEngineModLoader.dll"), "theirs");

        Assert.False(UnrealModLoaderService.ApplyExtracted(game, UmlPayload(), "v2.2.1", "a.rar"));
        Assert.Equal("theirs", File.ReadAllText(Path.Combine(game.Win64Path, "UnrealEngineModLoader.dll")));
        Assert.Null(UnrealModLoaderService.ReadManifest(game));
    }

    [Fact]
    public void With_both_dll_loaders_present_plugins_still_go_to_unreal_mod_unlocker()
    {
        var game = Dds1();
        Write(Path.Combine(game.Win64Path, "dxgi.dll"), "MZ...UnrealModUnlocker.dll...");
        UnrealModLoaderService.ApplyExtracted(game, UmlPayload(), "v2.2.1", "a.rar");

        var arc = Temp("dds_dll_");
        Write(Path.Combine(arc, "AERR.dll"));

        var installer = new ModInstallerService(game, new ModAnalyzerService(game, "", game.Profile.EngineVersion),
            new ModRegistryService(Path.Combine(Temp("dds_reg_"), "registry.json")));
        var method = typeof(ModInstallerService).GetMethod("InstallDllPlugin",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var mod = new ModInfo { Name = "AERR", Type = ModType.DllPlugin };

        Assert.True((bool)method.Invoke(installer, [arc, mod])!);
        Assert.True(File.Exists(Path.Combine(game.Win64Path, "UnrealModPlugins", "AERR.dll")));
        Assert.False(File.Exists(Path.Combine(game.ContentPath, "CoreMods", "AERR.dll")));
    }

    [Fact]
    public void Uml_is_refused_over_a_setup_it_did_not_make()
    {
        var game = Dds1();
        Write(Path.Combine(game.Win64Path, "ModLoaderInfo.ini"), "[INFO]\r\nLoaderPath=D:\\Mine\\UnrealEngineModLoader.dll");

        Assert.False(UnrealModLoaderService.ApplyExtracted(game, UmlPayload(), "v2.2.1", "a.rar"));
        Assert.Contains("D:\\Mine", File.ReadAllText(Path.Combine(game.Win64Path, "ModLoaderInfo.ini")));
    }

    [Fact]
    public void Uml_is_refused_over_another_tools_xinput_proxy()
    {
        var game = Dds1();
        Write(Path.Combine(game.Win64Path, "xinput1_3.dll"), "someone else");

        Assert.False(UnrealModLoaderService.ApplyExtracted(game, UmlPayload(), "v2.2.1", "a.rar"));
        Assert.Equal("someone else", File.ReadAllText(Path.Combine(game.Win64Path, "xinput1_3.dll")));
    }

    [Fact]
    public void Uml_is_refused_on_ue5()
    {
        Assert.False(UnrealModLoaderService.ApplyExtracted(Dds2(), UmlPayload(), "v2.2.1", "a.rar"));
    }

    [Fact]
    public void Removing_uml_takes_only_its_own_files()
    {
        var game = Dds1();
        UnrealModLoaderService.ApplyExtracted(game, UmlPayload(), "v2.2.1", "a.rar");

        var logicMod = Path.Combine(game.LogicModsPath, "MyMod.pak");
        var coreMod = Path.Combine(game.ContentPath, "CoreMods", "Framework.dll");
        var playersProfile = Path.Combine(game.Win64Path, "Profiles", "SomethingElse.profile");
        Write(logicMod);
        Write(coreMod);
        Write(playersProfile);

        Assert.True(UnrealModLoaderService.Remove(game));

        Assert.False(File.Exists(Path.Combine(game.Win64Path, "UnrealEngineModLoader.dll")));
        Assert.False(File.Exists(Path.Combine(game.Win64Path, "xinput1_3.dll")));
        Assert.False(File.Exists(Path.Combine(game.Win64Path, "ModLoaderInfo.ini")));
        Assert.False(File.Exists(UnrealModLoaderService.ManifestPath(game)));
        Assert.True(File.Exists(Path.Combine(game.Win64Path, "DrugDealerSimulator-Win64-Shipping.exe")));
        Assert.True(File.Exists(logicMod));
        Assert.True(File.Exists(coreMod));
        Assert.True(File.Exists(playersProfile));
    }

    // ---- UE4SS on both release lines -------------------------------------------------------------

    /// The shape of UE4SS_v3.0.1.zip: everything at the root.
    private string StablePayload()
    {
        var dir = Temp("dds_st_");
        Write(Path.Combine(dir, "dwmapi.dll"), "stable proxy");
        Write(Path.Combine(dir, "UE4SS.dll"), "stable");
        Write(Path.Combine(dir, "UE4SS-settings.ini"), "[General]\r\nSecondsToScanBeforeGivingUp = 30\r\n");
        Write(Path.Combine(dir, "README.md"));
        Write(Path.Combine(dir, "Mods", "mods.txt"), "BPModLoaderMod : 1\r\n");
        Write(Path.Combine(dir, "Mods", "BPModLoaderMod", "Scripts", "main.lua"), "stable bp");
        return dir;
    }

    /// The shape of an experimental build: dwmapi.dll beside ue4ss\.
    private string ExperimentalPayload(string tag = "exp")
    {
        var dir = Temp("dds_ex_");
        Write(Path.Combine(dir, "dwmapi.dll"), $"{tag} proxy");
        Write(Path.Combine(dir, "ue4ss", "UE4SS.dll"), tag);
        Write(Path.Combine(dir, "ue4ss", "UE4SS-settings.ini"), "[General]\r\nSecondsToScanBeforeGivingUp = 120\r\n");
        Write(Path.Combine(dir, "ue4ss", "Mods", "mods.txt"), "BPModLoaderMod : 1\r\n");
        Write(Path.Combine(dir, "ue4ss", "Mods", "BPModLoaderMod", "Scripts", "main.lua"), $"{tag} bp");
        return dir;
    }

    [Fact]
    public void Stable_installs_in_the_older_layout_and_is_recorded_as_ours()
    {
        var game = Dds2();

        Assert.True(UE4SSManagerService.ApplyExtracted(game, StablePayload(), "v3.0.1", "UE4SS_v3.0.1.zip", UE4SSChannel.Stable));

        Assert.True(File.Exists(Path.Combine(game.Win64Path, "UE4SS.dll")));
        Assert.True(File.Exists(Path.Combine(game.Win64Path, "Mods", "mods.txt")));
        Assert.False(Directory.Exists(game.UE4SSRootPath));
        Assert.True(game.HasLegacyUE4SSLayout);

        var status = new UE4SSManagerService().GetCurrentStatus(game);
        Assert.True(status.IsManagedByUs);
        Assert.Equal(UE4SSChannel.Stable, status.Channel);
        Assert.Equal(LoaderLayout.Legacy, status.Layout);
        Assert.Equal("UE4SS stable v3.0.1", status.StatusLabel);

        // Removable file by file - and never the game, never the player's mod list.
        var removable = new ModLoaderService().Detect(game, ModLoaders.UE4SS)!.RemovableFiles;
        Assert.Contains(Path.Combine(game.Win64Path, "UE4SS.dll"), removable);
        Assert.DoesNotContain(removable, f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(Path.Combine(game.Win64Path, "Mods", "mods.txt"), removable);
    }

    [Fact]
    public void A_settings_merge_never_touches_another_tools_ini_beside_the_game()
    {
        var game = Dds2();
        var reshade = Path.Combine(game.Win64Path, "ReShade.ini");
        Write(reshade, "[GENERAL]\r\nEffectSearchPaths=.\\\r\n");

        UE4SSManagerService.ApplyExtracted(game, StablePayload(), "v3.0.1", "UE4SS_v3.0.1.zip", UE4SSChannel.Stable);
        UE4SSManagerService.ApplyExtracted(game, StablePayload(), "v3.0.1", "UE4SS_v3.0.1.zip", UE4SSChannel.Stable);

        Assert.False(File.Exists(reshade + UE4SSManagerService.DefaultSnapshotSuffix));
        Assert.Equal("[GENERAL]\r\nEffectSearchPaths=.\\\r\n", File.ReadAllText(reshade));
    }

    [Fact]
    public void Moving_from_stable_to_experimental_carries_the_players_mods_into_ue4ss()
    {
        var game = Dds2();
        UE4SSManagerService.ApplyExtracted(game, StablePayload(), "v3.0.1", "UE4SS_v3.0.1.zip", UE4SSChannel.Stable);

        Write(Path.Combine(game.Win64Path, "Mods", "MyMod", "Scripts", "main.lua"), "mine");
        File.WriteAllText(Path.Combine(game.Win64Path, "Mods", "mods.txt"), "BPModLoaderMod : 1\r\nMyMod : 1\r\n");

        Assert.True(UE4SSManagerService.ApplyExtracted(game, ExperimentalPayload(), "experimental-latest",
            "UE4SS_v3.0.1-1161.zip", UE4SSChannel.Experimental));

        Assert.Equal("mine", File.ReadAllText(Path.Combine(game.UE4SSRootPath, "Mods", "MyMod", "Scripts", "main.lua")));
        Assert.Contains("MyMod : 1", File.ReadAllText(Path.Combine(game.UE4SSRootPath, "Mods", "mods.txt")));
        Assert.Equal("exp bp", File.ReadAllText(Path.Combine(game.UE4SSRootPath, "Mods", "BPModLoaderMod", "Scripts", "main.lua")));

        Assert.False(File.Exists(Path.Combine(game.Win64Path, "UE4SS.dll")));
        Assert.False(Directory.Exists(Path.Combine(game.Win64Path, "Mods")));
        Assert.Null(UE4SSManagerService.ReadLegacyManifest(game));
        Assert.True(File.Exists(Path.Combine(game.Win64Path, "DrugDealerSimulator2-Win64-Shipping.exe")));

        var status = new UE4SSManagerService().GetCurrentStatus(game);
        Assert.Equal(UE4SSChannel.Experimental, status.Channel);
        Assert.Equal(LoaderLayout.Modern, status.Layout);
        Assert.Equal(Path.Combine(game.UE4SSRootPath, "Mods"), game.UE4SSModsPath);
    }

    [Fact]
    public void A_lua_mods_row_follows_its_folder_when_ue4ss_changes_layout()
    {
        var game = Dds2();
        UE4SSManagerService.ApplyExtracted(game, StablePayload(), "v3.0.1", "a.zip", UE4SSChannel.Stable);
        var oldDir = Path.Combine(game.Win64Path, "Mods", "MyMod");
        Write(Path.Combine(oldDir, "Scripts", "main.lua"));

        var registry = new ModRegistryService(Path.Combine(Temp("dds_reg_"), "registry.json"));
        registry.Upsert(new ModInfo { Name = "MyMod", Type = ModType.LuaMod, InstallPath = oldDir, InstallFiles = [oldDir] });

        UE4SSManagerService.ApplyExtracted(game, ExperimentalPayload(), "e", "b.zip", UE4SSChannel.Experimental);
        Assert.Equal(1, registry.RelinkMovedLuaMods(game.UE4SSModsPath));

        var moved = Path.Combine(game.UE4SSRootPath, "Mods", "MyMod");
        var row = registry.Mods.Single();
        Assert.Equal(moved, row.InstallPath);
        Assert.Equal([moved], row.InstallFiles);
        Assert.Equal(0, registry.RelinkMovedLuaMods(game.UE4SSModsPath));   // idempotent
    }

    [Fact]
    public void Uninstalling_a_lua_mod_never_deletes_outside_one_mod_folder()
    {
        var game = Dds2();
        UE4SSManagerService.ApplyExtracted(game, ExperimentalPayload(), "e", "a.zip", UE4SSChannel.Experimental);
        Write(Path.Combine(game.UE4SSModsPath, "Other", "Scripts", "main.lua"));

        var registry = new ModRegistryService(Path.Combine(Temp("dds_reg_"), "registry.json"));
        var installer = new ModInstallerService(game, new ModAnalyzerService(game, "", game.Profile.EngineVersion), registry);

        // A damaged row naming the Mods folder itself.
        var bad = new ModInfo { Name = "Broken", Type = ModType.LuaMod, InstallPath = game.UE4SSModsPath, InstallFiles = [game.UE4SSModsPath] };
        registry.Upsert(bad);
        installer.Uninstall(bad);

        Assert.True(File.Exists(Path.Combine(game.UE4SSModsPath, "Other", "Scripts", "main.lua")));
    }

    [Fact]
    public void Going_back_from_experimental_to_stable_is_refused()
    {
        var game = Dds2();
        UE4SSManagerService.ApplyExtracted(game, ExperimentalPayload(), "experimental-latest", "a.zip", UE4SSChannel.Experimental);

        Assert.False(UE4SSManagerService.ApplyExtracted(game, StablePayload(), "v3.0.1", "b.zip", UE4SSChannel.Stable));
        Assert.False(File.Exists(Path.Combine(game.Win64Path, "UE4SS.dll")));
    }

    [Fact]
    public void A_hand_made_older_layout_offers_no_install_or_update()
    {
        var game = Dds2();
        Write(Path.Combine(game.Win64Path, "UE4SS.dll"), "hand made");
        Write(Path.Combine(game.Win64Path, "Mods", "mods.txt"));

        var status = new UE4SSManagerService().GetCurrentStatus(game);
        Assert.True(status.IsInstalled);
        Assert.False(status.CanInstall);
        Assert.Contains("by hand", status.InstallBlockedReason);
    }

    [Fact]
    public void A_ue4ss_folder_holding_only_mods_is_not_an_install()
    {
        var game = Dds2();
        Write(Path.Combine(game.UE4SSRootPath, "Mods", "Early", "Scripts", "main.lua"));

        var status = new UE4SSManagerService().GetCurrentStatus(game);
        Assert.False(status.IsInstalled);
        Assert.True(status.CanInstall);   // so the Install button shows
    }

    [Fact]
    public void Ue4ss_on_an_engine_4_21_or_older_warns_on_both_lines()
    {
        var old = GameProfiles.Dds2 with
        {
            Id = "steam:2", IsBuiltIn = false, UE4SSStableCaveat = null, EngineLabel = "UE 4.20",
            EngineVersion = Ue(4, 20), InstallableLoaders = LoaderCompatibility.InstallableFor(Ue(4, 20))
        };

        foreach (var channel in new[] { UE4SSChannel.Experimental, UE4SSChannel.Stable })
        {
            var choice = LoaderCompatibility.ForUE4SS(old, channel);
            Assert.True(choice.Available);
            Assert.Contains("LessEqual421", choice.Warning);
        }

        Assert.Null(LoaderCompatibility.ForUE4SS(old with { EngineVersion = Ue(4, 22) }, UE4SSChannel.Experimental).Warning);
    }

    [Fact]
    public void Nothing_is_installed_over_a_hand_made_older_layout()
    {
        var game = Dds2();
        Write(Path.Combine(game.Win64Path, "UE4SS.dll"), "hand made");
        Write(Path.Combine(game.Win64Path, "Mods", "mods.txt"));

        Assert.False(UE4SSManagerService.ApplyExtracted(game, ExperimentalPayload(), "e", "a.zip", UE4SSChannel.Experimental));
        Assert.False(UE4SSManagerService.ApplyExtracted(game, StablePayload(), "s", "b.zip", UE4SSChannel.Stable));
        Assert.Equal("hand made", File.ReadAllText(Path.Combine(game.Win64Path, "UE4SS.dll")));
        Assert.False(Directory.Exists(game.UE4SSRootPath));
    }

    [Fact]
    public void Stable_adopts_the_mods_a_lua_install_left_in_an_empty_ue4ss_folder()
    {
        var game = Dds2();
        Write(Path.Combine(game.UE4SSRootPath, "Mods", "Early", "Scripts", "main.lua"), "early");

        Assert.True(UE4SSManagerService.ApplyExtracted(game, StablePayload(), "v3.0.1", "a.zip", UE4SSChannel.Stable));

        Assert.False(Directory.Exists(game.UE4SSRootPath));
        Assert.Equal("early", File.ReadAllText(Path.Combine(game.Win64Path, "Mods", "Early", "Scripts", "main.lua")));
    }

    // The bug this replaced: the new dwmapi.dll was copied BEFORE the undo copy was taken, so Undo
    // put the new proxy back beside the old ue4ss\ folder.
    [Fact]
    public void The_undo_copy_keeps_the_proxy_dll_that_was_actually_installed()
    {
        var game = Dds2();
        UE4SSManagerService.ApplyExtracted(game, ExperimentalPayload("old"), "e", "UE4SS_old.zip", UE4SSChannel.Experimental);
        UE4SSManagerService.ApplyExtracted(game, ExperimentalPayload("new"), "e", "UE4SS_new.zip", UE4SSChannel.Experimental);

        var kept = UE4SSManagerService.FindPreviousBuild(game)!;
        Assert.Equal("UE4SS_old.zip", kept.AssetName);
        Assert.Equal("old proxy", File.ReadAllText(Path.Combine(kept.RootPath, "dwmapi.dll")));
        Assert.Equal("new proxy", File.ReadAllText(Path.Combine(game.Win64Path, "dwmapi.dll")));

        Assert.True(UE4SSManagerService.RestorePreviousBuild(game));
        Assert.Equal("old proxy", File.ReadAllText(Path.Combine(game.Win64Path, "dwmapi.dll")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(game.UE4SSRootPath, "UE4SS.dll")));
    }

    [Fact]
    public void Logic_mods_go_flat_once_unreal_mod_loader_is_present()
    {
        // A generic UE4 game's profile nests logic mods (UE4SS's convention)...
        var root = Temp("dds_lm_");
        Write(Path.Combine(root, "SomeGame", "Binaries", "Win64", "SomeGame-Win64-Shipping.exe"));
        Write(Path.Combine(root, "SomeGame", "Content", "Paks", "SomeGame-WindowsNoEditor.pak"));
        var profile = GenericGameProfiles.Create(root, "SomeGame", null, readExecutable: false);
        Assert.True(profile.LogicModsUseSubfolders);
        Assert.True(profile.InstallableLoaders.HasFlag(ModLoaders.UnrealModLoader));

        var game = new GameInstallation { RootPath = root, Profile = profile };
        Assert.True(UnrealModLoaderService.ApplyExtracted(game, UmlPayload(), "v2.2.1", "a.rar"));

        // ...but UML only scans LogicMods' own files, so with UML present a logic mod installs flat.
        var nested = typeof(ModInstallerService).GetMethod("LogicModsNested",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var registry = Path.Combine(Temp("dds_reg_"), "registry.json");
        var installer = new ModInstallerService(game, new ModAnalyzerService(game, "", game.Profile.EngineVersion),
            new ModRegistryService(registry));
        Assert.False((bool)nested.Invoke(installer, null)!);
    }
}
