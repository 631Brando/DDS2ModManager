namespace DDS2ModManager.Tests;

/// Behaviour pinned to what the loaders' own releases actually do, checked against the published
/// archives (UE4SS v3.0.1, experimental-latest on the v4.0.0 line, UnrealModLoader v2.2.1).
public class UpstreamLoaderBehaviourTests : IDisposable
{
    private readonly List<string> _temps = [];

    public void Dispose()
    {
        foreach (var t in _temps) { try { Directory.Delete(t, true); } catch { } }
    }

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

    private GameInstallation Dds2()
    {
        var root = Temp("dds_up_");
        Write(Path.Combine(root, "DrugDealerSimulator2", "Binaries", "Win64", "DrugDealerSimulator2-Win64-Shipping.exe"));
        Write(Path.Combine(root, "DrugDealerSimulator2", "Content", "Paks", "pakchunk0-Windows.utoc"));
        _temps.Add(AppPaths.PreviousUE4SSFor(root));
        return new GameInstallation { RootPath = root, Profile = GameProfiles.Dds2 };
    }

    private static GitHubReleaseInfo Volume(string tag, params string[] assets) => new()
    {
        TagName = tag,
        Assets = assets.Select(a => new GitHubAsset { Name = a, BrowserDownloadUrl = "https://x/" + a, Size = 1 }).ToList()
    };

    // ---- the build archive spans several releases ------------------------------------------------

    /// "experimental" hit GitHub's 1,000-asset cap on 5 Nov 2026 and the archive continued in
    /// "experimental2". Every 4.0.0 build is only in the second volume.
    [Fact]
    public void Builds_from_every_archive_volume_are_listed_newest_first()
    {
        var builds = UE4SSManagerService.BuildsFromArchive(
        [
            Volume("experimental", "UE4SS_v3.0.1-1093-gba2efd55.zip", "UE4SS_v3.0.1-1211-gaaaaaaaa.zip", "zMapGenBP.zip"),
            Volume("experimental2", "UE4SS_v3.0.1-1221-gaea3ed6e.zip", "UE4SS_v4.0.0-1-gf54b7332.zip",
                "UE4SS_v4.0.0-14-g682a75f1.zip", "zDEV-UE4SS_v4.0.0-9-gb2d936b9.zip")
        ]);

        Assert.Equal(
        [
            "UE4SS_v4.0.0-14-g682a75f1.zip", "zDEV-UE4SS_v4.0.0-9-gb2d936b9.zip", "UE4SS_v4.0.0-1-gf54b7332.zip",
            "UE4SS_v3.0.1-1221-gaea3ed6e.zip", "UE4SS_v3.0.1-1211-gaaaaaaaa.zip", "UE4SS_v3.0.1-1093-gba2efd55.zip"
        ], builds.Select(b => b.AssetName));

        // The volume it came from is what an install records as its tag.
        Assert.Equal("experimental2", builds[0].ReleaseTag);
        Assert.Equal("experimental", builds[^1].ReleaseTag);
    }

    [Fact]
    public void A_build_published_in_two_volumes_is_listed_once()
    {
        var builds = UE4SSManagerService.BuildsFromArchive(
        [
            Volume("experimental", "UE4SS_v3.0.1-1211-gaaaaaaaa.zip"),
            Volume("experimental2", "UE4SS_v3.0.1-1211-gaaaaaaaa.zip")
        ]);

        Assert.Single(builds);
    }

    // ---- Undo update leaves the player's mods alone ----------------------------------------------

    /// The kept copy is the whole ue4ss\ folder on the day of the update, mods included. Undo used to
    /// put all of it back: every lua mod installed since was deleted, every one removed since came
    /// back, and mods.txt reverted - while the log said mod folders "were not touched".
    [Fact]
    public void Undoing_an_update_restores_ue4ss_but_not_the_players_mods_as_they_were()
    {
        var game = Dds2();
        var kept = AppPaths.PreviousUE4SSFor(game.RootPath);

        Write(Path.Combine(kept, "ue4ss", "UE4SS.dll"), "old build");
        Write(Path.Combine(kept, "ue4ss", "UE4SS-settings.ini"), "old settings");
        Write(Path.Combine(kept, "ue4ss", "Mods", "mods.txt"), "OldUserMod : 1\r\nBPModLoaderMod : 1\r\n");
        Write(Path.Combine(kept, "ue4ss", "Mods", "BPModLoaderMod", "Scripts", "main.lua"), "old bp");
        Write(Path.Combine(kept, "ue4ss", "Mods", "BPModLoaderMod", "load_order.txt"), "old order");
        Write(Path.Combine(kept, "ue4ss", "Mods", "OldUserMod", "Scripts", "main.lua"), "removed since");
        Write(Path.Combine(kept, "dwmapi.dll"), "old proxy");
        Write(Path.Combine(kept, "asset.txt"), "UE4SS_v3.0.1-1221-gaea3ed6e.zip");

        Write(Path.Combine(game.UE4SSRootPath, "UE4SS.dll"), "new build");
        Write(Path.Combine(game.UE4SSRootPath, "NewOnly.dll"), "new");
        Write(Path.Combine(game.UE4SSRootPath, "Mods", "mods.txt"), "NewUserMod : 1\r\nBPModLoaderMod : 1\r\n");
        Write(Path.Combine(game.UE4SSRootPath, "Mods", "BPModLoaderMod", "Scripts", "main.lua"), "new bp");
        Write(Path.Combine(game.UE4SSRootPath, "Mods", "BPModLoaderMod", "load_order.txt"), "my order");
        Write(Path.Combine(game.UE4SSRootPath, "Mods", "NewUserMod", "Scripts", "main.lua"), "installed since");
        Write(Path.Combine(game.Win64Path, "dwmapi.dll"), "new proxy");

        // shared\ holds UE4SS's helpers AND libraries lua mods bring with them.
        Write(Path.Combine(kept, "ue4ss", "Mods", "shared", "UEHelpers", "UEHelpers.lua"), "old helpers");
        Write(Path.Combine(game.UE4SSRootPath, "Mods", "shared", "UEHelpers", "UEHelpers.lua"), "new helpers");
        Write(Path.Combine(game.UE4SSRootPath, "Mods", "shared", "SomeModLib", "lib.lua"), "a mod's library");

        Assert.True(UE4SSManagerService.RestorePreviousBuild(game));

        var mods = Path.Combine(game.UE4SSRootPath, "Mods");

        // UE4SS itself is the old build, with what it shipped.
        Assert.Equal("old build", File.ReadAllText(Path.Combine(game.UE4SSRootPath, "UE4SS.dll")));
        Assert.Equal("old settings", File.ReadAllText(Path.Combine(game.UE4SSRootPath, "UE4SS-settings.ini")));
        Assert.False(File.Exists(Path.Combine(game.UE4SSRootPath, "NewOnly.dll")));
        Assert.Equal("old bp", File.ReadAllText(Path.Combine(mods, "BPModLoaderMod", "Scripts", "main.lua")));
        Assert.Equal("old proxy", File.ReadAllText(Path.Combine(game.Win64Path, "dwmapi.dll")));

        // The player's mods and lists are as they are today.
        Assert.Equal("installed since", File.ReadAllText(Path.Combine(mods, "NewUserMod", "Scripts", "main.lua")));
        Assert.False(Directory.Exists(Path.Combine(mods, "OldUserMod")));
        Assert.StartsWith("NewUserMod : 1", File.ReadAllText(Path.Combine(mods, "mods.txt")));
        Assert.Equal("my order", File.ReadAllText(Path.Combine(mods, "BPModLoaderMod", "load_order.txt")));

        Assert.Equal("old helpers", File.ReadAllText(Path.Combine(mods, "shared", "UEHelpers", "UEHelpers.lua")));
        Assert.Equal("a mod's library", File.ReadAllText(Path.Combine(mods, "shared", "SomeModLib", "lib.lua")));
    }

    // ---- where a logic mod goes depends on what the installed loader reads -----------------------

    /// The pak loop of BPModLoaderMod in UE4SS_v3.0.1.zip, verbatim in its essentials: subfolders are
    /// read for config.lua only, and paks come from LogicMods' own top level.
    private const string StableBlueprintLoader = """
        for ModDirectoryName,ModDirectory in pairs(LogicModsDir) do
            for _,ModFile in pairs(ModDirectory.__files) do
                if ModFile.__name == "config.lua" then dofile(ModFile.__absolute_path) end
            end
        end
        for _, ModFile in pairs(LogicModsDir.__files) do
            local ModName = ModFile.__name
        end
        """;

    /// The same loop in the experimental line (v4.0.0-14): each subfolder's files are gathered first.
    private const string ExperimentalBlueprintLoader = """
        local Files = LogicModsDir.__files
        for _, ModDirectory in pairs(LogicModsDir) do
            for _, ModFile in pairs(ModDirectory.__files) do
                table.insert(Files, ModFile)
            end
        end
        for _, ModFile in pairs(Files) do
            local ModName = ModFile.__name
        end
        """;

    [Fact]
    public void Stable_ue4ss_blueprint_loader_reads_only_the_top_of_LogicMods()
    {
        var game = Dds2();
        Write(Path.Combine(game.Win64Path, "UE4SS.dll"));
        Write(Path.Combine(game.Win64Path, "Mods", "mods.txt"), "BPModLoaderMod : 1\r\n");
        Write(Path.Combine(game.Win64Path, "Mods", "BPModLoaderMod", "Scripts", "main.lua"), StableBlueprintLoader);

        Assert.False(ModInstallerService.BlueprintLoaderReadsSubfolders(game));
        Assert.False(LogicModsNested(game));
    }

    [Fact]
    public void Experimental_ue4ss_blueprint_loader_reads_one_folder_down()
    {
        var game = Dds2();
        Write(Path.Combine(game.UE4SSRootPath, "UE4SS.dll"));
        Write(Path.Combine(game.UE4SSRootPath, "Mods", "BPModLoaderMod", "Scripts", "main.lua"), ExperimentalBlueprintLoader);

        Assert.True(ModInstallerService.BlueprintLoaderReadsSubfolders(game));
        Assert.True(LogicModsNested(game));
    }

    /// Before any UE4SS is installed there is nothing to read, so the profile's own default stands.
    [Fact]
    public void With_no_ue4ss_yet_the_profile_decides()
    {
        var game = Dds2();
        Assert.True(ModInstallerService.BlueprintLoaderReadsSubfolders(game));
        Assert.True(LogicModsNested(game));
    }

    // ---- uninstalling a lua mod takes its mods.txt line too --------------------------------------

    /// mods.txt keys on the folder. The display name of half of a two-part mod carries a suffix, and
    /// uninstall used to read the key only after deleting the folder - so it fell back to that name
    /// and left the real line behind.
    [Fact]
    public void Uninstalling_a_lua_mod_removes_its_line_even_when_the_display_name_differs()
    {
        var game = Dds2();
        Write(Path.Combine(game.UE4SSRootPath, "UE4SS.dll"));
        var folder = Path.Combine(game.UE4SSModsPath, "SpecialClientMarker");
        Write(Path.Combine(folder, "Scripts", "main.lua"));
        Write(game.ModsTxtPath, "SpecialClientMarker : 1\r\nKeybinds : 1\r\n");

        var registry = new ModRegistryService(Path.Combine(Temp("dds_reg_"), "registry.json"));
        var installer = new ModInstallerService(game, new ModAnalyzerService(game, "", game.Profile.EngineVersion), registry);
        var mod = new ModInfo
        {
            Name = "SpecialClientMarker (LuaMod)",
            Type = ModType.LuaMod,
            InstallPath = folder,
            InstallFiles = [folder],
            IsEnabled = true
        };
        registry.Upsert(mod);

        installer.Uninstall(mod);

        Assert.False(Directory.Exists(folder));
        Assert.False(new LuaModConfigService().ReadEntries(game).ContainsKey("SpecialClientMarker"));
        Assert.True(new LuaModConfigService().ReadEntries(game).ContainsKey("Keybinds"));
    }

    // ---- UnrealModLoader is ANSI end to end ------------------------------------------------------

    private GameInstallation Dds1At(string rootName)
    {
        var root = Path.Combine(Temp("dds_ansi_"), rootName);
        Write(Path.Combine(root, "DrugDealerSimulator", "Binaries", "Win64", "DrugDealerSimulator-Win64-Shipping.exe"));
        Write(Path.Combine(root, "DrugDealerSimulator", "Content", "Paks", "DrugDealerSimulator-WindowsNoEditor.pak"));
        return new GameInstallation { RootPath = root, Profile = GameProfiles.Dds1 };
    }

    private string UmlPayload()
    {
        var dir = Temp("dds_uml_");
        Write(Path.Combine(dir, "UnrealEngineModLoader.dll"), "loader");
        Write(Path.Combine(dir, "Tools", "AutoInjector", "xinput1_3.dll"), "proxy");
        return dir;
    }

    /// The AutoInjector reads LoaderPath with a narrow std::ifstream and passes the bytes to
    /// LoadLibraryA, so the file has to be in the ANSI code page - UTF-8 breaks any accented path.
    [Fact]
    public void The_injector_ini_is_written_in_the_ansi_code_page()
    {
        var game = Dds1At("Bibliothèque");
        if (!UnrealModLoaderService.FitsAnsiCodePage(game.Win64Path)) return; // an ACP without è

        Assert.True(UnrealModLoaderService.ApplyExtracted(game, UmlPayload(), "v2.2.1", "UnrealModLoader_V2.2.1.rar"));

        var bytes = File.ReadAllBytes(Path.Combine(game.Win64Path, UnrealModLoaderService.LoaderInfoFileName));
        var expected = UnrealModLoaderService.AnsiCodePage().GetBytes(
            $"LoaderPath={Path.Combine(game.Win64Path, UnrealModLoaderService.LoaderDllName)}");

        Assert.True(bytes.AsSpan().IndexOf(expected) >= 0);
    }

    /// A path the ANSI code page can't spell comes back from GetModuleFileNameA with '?' in it, and
    /// every folder UML derives from the exe points nowhere - refused up front, not installed broken.
    [Fact]
    public void Uml_is_refused_where_the_ansi_code_page_cannot_spell_the_game_path()
    {
        var game = Dds1At("Games \U0001F3AE");
        if (UnrealModLoaderService.FitsAnsiCodePage(game.Win64Path)) return; // a UTF-8 ACP spells anything

        Assert.Contains("ANSI", UnrealModLoaderService.WhyNot(game));
        Assert.False(UnrealModLoaderService.ApplyExtracted(game, UmlPayload(), "v2.2.1", "UnrealModLoader_V2.2.1.rar"));
        Assert.False(File.Exists(Path.Combine(game.Win64Path, UnrealModLoaderService.LoaderDllName)));
    }

    private bool LogicModsNested(GameInstallation game)
    {
        var method = typeof(ModInstallerService).GetMethod("LogicModsNested",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var registry = Path.Combine(Temp("dds_reg_"), "registry.json");
        var installer = new ModInstallerService(game, new ModAnalyzerService(game, "", game.Profile.EngineVersion),
            new ModRegistryService(registry));
        return (bool)method.Invoke(installer, null)!;
    }
}
