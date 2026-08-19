namespace DDS2ModManager.Tests;

/// DDS1's fifth mod shape: a native DLL loaded by a mod loader, usually with a data folder beside it.
///
/// Found the hard way — a real mod (AE Revolutions Reloaded) was refused with "couldn't determine a
/// mod type", because it is not a pak, not a lua script and not a cooked asset. The refusal was
/// correct behaviour for an unknown shape; the gap was that the shape was unknown at all.
public class DllPluginTests : IDisposable
{
    private readonly List<string> _temps = [];

    public void Dispose()
    {
        foreach (var t in _temps) { try { Directory.Delete(t, true); } catch { } }
    }

    private GameInstallation Install(string projectFolder)
    {
        var root = Path.Combine(Path.GetTempPath(), "dds_dll_" + Guid.NewGuid().ToString("N")[..8]);
        _temps.Add(root);
        Directory.CreateDirectory(Path.Combine(root, projectFolder, "Binaries", "Win64"));
        return new GameInstallation { RootPath = root };
    }

    private string ArchiveDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "dds_arc_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        _temps.Add(d);
        return d;
    }

    private static void Write(string path, string text = "x")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    // ---- the numbering contract -------------------------------------------------------------------

    // ModProfileService and ModBackupService serialise ModType as an integer, so members may only be
    // appended. Inserting one silently remaps every saved profile and backup on disk.
    [Fact]
    public void The_new_type_was_appended_not_inserted()
    {
        Assert.Equal(5, (int)ModType.DllPlugin);

        // And the existing numbers are untouched.
        Assert.Equal(0, (int)ModType.Unknown);
        Assert.Equal(1, (int)ModType.PatchMod);
        Assert.Equal(2, (int)ModType.LogicMod);
        Assert.Equal(3, (int)ModType.LuaMod);
        Assert.Equal(4, (int)ModType.LooseAsset);
    }

    // ---- capability gating --------------------------------------------------------------------------

    // DDS2's extension mechanism is UE4SS. A loose native DLL there has nothing to load it, so the
    // shape must not be recognised - otherwise a DDS2 user gets offered an install that cannot work.
    [Fact]
    public void Only_a_game_whose_scene_uses_dll_plugins_supports_them()
    {
        Assert.True(GameProfiles.Dds1.SupportsDllPlugins);
        Assert.False(GameProfiles.Dds2.SupportsDllPlugins);
    }

    // ---- which loader takes the DLL, and where ------------------------------------------------------

    // There is no shared convention: UnrealModUnlocker reads UnrealModPlugins, UnrealModLoader reads
    // coremods. The destination has to come from what is installed, never from a constant.
    [Fact]
    public void Each_loader_declares_its_own_plugin_folder()
    {
        var game = Install("DrugDealerSimulator");
        Write(Path.Combine(game.Win64Path, "dxgi.dll"), "MZ...UnrealModUnlocker.dll...");
        Write(Path.Combine(game.Win64Path, "ModLoaderInfo.ini"));

        var loaders = new ModLoaderService().DetectAll(game);

        var unlocker = loaders.Single(l => l.Loader == ModLoaders.UnrealModUnlocker);
        Assert.Equal(Path.Combine(game.Win64Path, "UnrealModPlugins"), unlocker.PluginFolder);

        var uml = loaders.Single(l => l.Loader == ModLoaders.UnrealModLoader);
        Assert.Equal(Path.Combine(game.Win64Path, "coremods"), uml.PluginFolder);
    }

    // UE4SS loads lua mods, not arbitrary native DLLs. Reporting a folder for it would send a plugin
    // somewhere nothing reads.
    [Fact]
    public void Ue4ss_offers_no_plugin_folder()
    {
        var game = Install("DrugDealerSimulator");
        Write(Path.Combine(game.Win64Path, "UE4SS.dll"));

        var ue4ss = new ModLoaderService().Detect(game, ModLoaders.UE4SS)!;

        Assert.True(ue4ss.IsInstalled);
        Assert.Null(ue4ss.PluginFolder);
    }

    // ---- detection ------------------------------------------------------------------------------------

    // The real archive's shape: a DLL, an HTML guide, and a folder of JSON content. No pak, no lua,
    // no cooked assets - which is exactly why it used to be refused.
    [Fact]
    public void An_archive_of_a_dll_and_data_is_recognised()
    {
        var game = Install("DrugDealerSimulator");
        var arc = ArchiveDir();
        Write(Path.Combine(arc, "AERR.dll"));
        Write(Path.Combine(arc, "Mod Guide.html"));
        Write(Path.Combine(arc, "Custom Example Pack (CEP)", "CustomDrugs", "DRUG-MOLLY.json"));

        var result = new ModAnalyzerService(game, "", GameProfiles.Dds1.EngineVersion).Analyze(arc);

        Assert.Equal(ModType.DllPlugin, result.Type);
        Assert.False(result.ParseFailed);
        Assert.Equal(3, result.AssetPaths.Count);
    }

    // The same archive on DDS2 must still be refused: nothing there can load it.
    [Fact]
    public void The_same_archive_is_not_recognised_on_a_game_without_dll_plugins()
    {
        var game = Install("DrugDealerSimulator2");
        var arc = ArchiveDir();
        Write(Path.Combine(arc, "AERR.dll"));

        var result = new ModAnalyzerService(game, "", GameProfiles.Dds2.EngineVersion).Analyze(arc);

        Assert.NotEqual(ModType.DllPlugin, result.Type);
    }

    // A pak mod that happens to ship a helper DLL is still a pak mod - the pak decides.
    [Fact]
    public void A_pak_beside_a_dll_is_still_a_pak_mod()
    {
        var game = Install("DrugDealerSimulator");
        var arc = ArchiveDir();
        Write(Path.Combine(arc, "Helper.dll"));
        Write(Path.Combine(arc, "CoolMod.pak"));

        var result = new ModAnalyzerService(game, "", GameProfiles.Dds1.EngineVersion).Analyze(arc);

        Assert.NotEqual(ModType.DllPlugin, result.Type);
    }

    // ---- the refusal precondition -------------------------------------------------------------------

    // InstallDllPlugin refuses on exactly this: no detected loader offers a PluginFolder. Pinning the
    // condition rather than the install keeps the check honest without an installer that writes to
    // %AppData%. A bare install must produce no candidate at all - a fallback here would put a native
    // DLL somewhere nothing reads, which looks like success and presents to the user as a broken mod.
    [Fact]
    public void A_bare_install_offers_no_destination_for_a_dll()
    {
        var game = Install("DrugDealerSimulator");

        var candidates = new ModLoaderService().DetectAll(game)
            .Where(l => l.IsInstalled && l.PluginFolder != null);

        Assert.Empty(candidates);
    }

    // A lua mod shipping a DLL is still a lua mod, for the same reason.
    [Fact]
    public void A_lua_script_beside_a_dll_is_still_a_lua_mod()
    {
        var game = Install("DrugDealerSimulator");
        var arc = ArchiveDir();
        Write(Path.Combine(arc, "Helper.dll"));
        Write(Path.Combine(arc, "MyMod", "Scripts", "main.lua"));

        var result = new ModAnalyzerService(game, "", GameProfiles.Dds1.EngineVersion).Analyze(arc);

        Assert.Equal(ModType.LuaMod, result.Type);
    }
}
