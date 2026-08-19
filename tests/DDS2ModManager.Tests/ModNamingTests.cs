using System.Reflection;

namespace DDS2ModManager.Tests;

/// What a mod ends up CALLED, and why a name that can't be resolved stops the install.
///
/// The name is not a label. It is the duplicate-install key (ModInstallerService), the profile
/// match key (ModProfileService), the Nexus match key (MainViewModel.Nexus) and the mod-list group
/// key — and nothing in the UI can rename a mod afterwards. So the interesting cases here are the
/// refusals: the old code fell back to the working directory's own name, which for an archive
/// install is the temp folder "DDS2MM_Install_&lt;guid&gt;", and that GUID became all four keys.
public class ModNamingTests : IDisposable
{
    private readonly List<string> _temps = [];

    public void Dispose()
    {
        foreach (var t in _temps) { try { Directory.Delete(t, true); } catch { } }
    }

    private GameInstallation Install(string projectFolder = "DrugDealerSimulator")
    {
        var root = Path.Combine(Path.GetTempPath(), "dds_nm_" + Guid.NewGuid().ToString("N")[..8]);
        _temps.Add(root);
        Directory.CreateDirectory(Path.Combine(root, projectFolder, "Binaries", "Win64"));
        return new GameInstallation { RootPath = root };
    }

    /// A stand-in for the folder PrepareInstall unpacks an archive into.
    private string ExtractionRoot()
    {
        var d = Path.Combine(Path.GetTempPath(), "DDS2MM_Install_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        _temps.Add(d);
        return d;
    }

    private static void Write(string path, string text = "x")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    /// InferModName is private with one call site, so it is reached by reflection rather than made
    /// public purely for testing. Returns null when nothing in the archive names the mod.
    private static string? Name(GameInstallation game, string workingDir, ModType type, bool isTempRoot)
    {
        var installer = (ModInstallerService)Activator.CreateInstance(
            typeof(ModInstallerService),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            [game, new ModAnalyzerService(game, "", GameProfiles.Dds1.EngineVersion),
             new ModRegistryService(Path.Combine(Path.GetTempPath(), "dds_nm_reg_" + Guid.NewGuid().ToString("N")[..8] + ".json"))],
            null)!;

        var method = typeof(ModInstallerService)
            .GetMethod("InferModName", BindingFlags.Instance | BindingFlags.NonPublic)!;

        return (string?)method.Invoke(installer, [workingDir, type, isTempRoot]);
    }

    // ---- the bug that started this ------------------------------------------------------------

    // A DLL plugin IS its DLL: InstallDllPlugin copies it by filename into a flat folder the loader
    // keys on filename, so the basename is already the mod's identity on disk. Same reasoning as
    // the pak rule, and stable across versions in a way a download filename is not.
    [Fact]
    public void A_dll_plugin_is_named_after_its_dll()
    {
        var arc = ExtractionRoot();
        Write(Path.Combine(arc, "AERR.dll"));
        Write(Path.Combine(arc, "Mod Guide.html"));
        Write(Path.Combine(arc, "Custom Example Pack (CEP)", "CustomDrugs", "DRUG-MOLLY.json"));

        Assert.Equal("AERR", Name(Install(), arc, ModType.DllPlugin, isTempRoot: true));
    }

    // Two DLLs is a framework plus a dependency, or two mods in one archive. Taking the first would
    // name the mod after whichever the filesystem happened to enumerate first.
    [Fact]
    public void Two_dlls_are_not_guessed_between()
    {
        var arc = ExtractionRoot();
        Write(Path.Combine(arc, "Framework.dll"));
        Write(Path.Combine(arc, "Dependency.dll"));

        Assert.Null(Name(Install(), arc, ModType.DllPlugin, isTempRoot: true));
    }

    // ---- loose assets: the same hole, and it predates the DLL work -----------------------------

    [Fact]
    public void A_loose_asset_mod_is_named_after_the_folder_wrapping_content()
    {
        var arc = ExtractionRoot();
        Write(Path.Combine(arc, "BetterDrugs", "Content", "DataTables", "Drugs.uasset"));

        Assert.Equal("BetterDrugs", Name(Install(), arc, ModType.LooseAsset, isTempRoot: true));
    }

    // Nothing named it: Content\ sits at the archive root, so the only candidate is the temp folder.
    [Fact]
    public void A_loose_asset_mod_rooted_at_content_has_no_name()
    {
        var arc = ExtractionRoot();
        Write(Path.Combine(arc, "Content", "DataTables", "Drugs.uasset"));

        Assert.Null(Name(Install(), arc, ModType.LooseAsset, isTempRoot: true));
    }

    // Authors routinely ship the whole game-relative path. Listing the mod under the game's own
    // project folder is a wrong name, not a fallback - and every such mod would collide with every
    // other one on the duplicate-install check.
    [Fact]
    public void The_project_folder_is_never_used_as_a_mod_name()
    {
        var game = Install();
        var arc = ExtractionRoot();
        Write(Path.Combine(arc, "DrugDealerSimulator", "Content", "DataTables", "Drugs.uasset"));

        Assert.Null(Name(game, arc, ModType.LooseAsset, isTempRoot: true));
    }

    // ---- the rules that already worked, pinned against regression ------------------------------

    [Fact]
    public void A_pak_mod_is_still_named_after_its_pak()
    {
        var arc = ExtractionRoot();
        Write(Path.Combine(arc, "CoolMod_P.pak"));

        Assert.Equal("CoolMod_P", Name(Install(), arc, ModType.PatchMod, isTempRoot: true));
    }

    [Fact]
    public void A_lua_mod_is_still_named_after_its_folder()
    {
        var arc = ExtractionRoot();
        Write(Path.Combine(arc, "MyLuaMod", "Scripts", "main.lua"));

        Assert.Equal("MyLuaMod", Name(Install(), arc, ModType.LuaMod, isTempRoot: true));
    }

    // A lua archive with no folder around Scripts\ used to be named after the temp root - and for
    // lua that name is WRITTEN TO DISK as the ue4ss\Mods folder and into mods.txt, so it is the one
    // place the wrong name is load-bearing rather than cosmetic.
    [Fact]
    public void A_lua_mod_with_no_folder_around_scripts_has_no_name()
    {
        var arc = ExtractionRoot();
        Write(Path.Combine(arc, "Scripts", "main.lua"));

        Assert.Null(Name(Install(), arc, ModType.LuaMod, isTempRoot: true));
    }

    // A folder the user dragged in was named by a person. Only the extraction root is nameless, so
    // the fallback must survive for every other case.
    [Fact]
    public void A_dropped_folder_is_still_named_after_itself()
    {
        var arc = ExtractionRoot();
        var inner = Path.Combine(arc, "SomeonesMod");
        Write(Path.Combine(inner, "AERR.dll"));
        Write(Path.Combine(inner, "readme.txt"));

        // Two DLL-less rules fall through; the terminal rule applies because this is not the root.
        Assert.Equal("SomeonesMod", Name(Install(), inner, ModType.LooseAsset, isTempRoot: false));
    }

    // ---- the author's own statement outranks every deduction -----------------------------------

    // Everything else here reads a filename or a folder and reasons about what it probably means.
    // A manifest is the mod SAYING what it is called.
    [Fact]
    public void A_declared_name_beats_the_dll_filename()
    {
        var arc = ExtractionRoot();
        Write(Path.Combine(arc, "AERR.dll"));
        Write(Path.Combine(arc, ".dds2mod.json"),
            """{"schema":1,"name":"AE Revolutions Reloaded","updateUrl":"https://github.com/a/b"}""");

        Assert.Equal("AE Revolutions Reloaded", Name(Install(), arc, ModType.DllPlugin, isTempRoot: true));
    }

    // Build() refuses a manifest with no updateUrl - it has nothing to offer the updater - but a
    // manifest that names the mod and declares no updates is still telling the truth about its name.
    [Fact]
    public void A_declared_name_is_read_even_with_no_update_url()
    {
        var arc = ExtractionRoot();
        Write(Path.Combine(arc, "AERR.dll"));
        Write(Path.Combine(arc, ".ddsmod.json"), """{"schema":1,"name":"Named Without Updates"}""");

        Assert.Equal("Named Without Updates", Name(Install(), arc, ModType.DllPlugin, isTempRoot: true));
    }

    // The name becomes a folder name for some types, and a manifest is author-supplied text.
    [Fact]
    public void A_declared_name_containing_a_path_separator_is_refused()
    {
        var arc = ExtractionRoot();
        Write(Path.Combine(arc, "AERR.dll"));
        Write(Path.Combine(arc, ".dds2mod.json"), """{"schema":1,"name":"..\\..\\Windows\\System32"}""");

        // Falls through to the DLL rule rather than being used.
        Assert.Equal("AERR", Name(Install(), arc, ModType.DllPlugin, isTempRoot: true));
    }

    // A manifest refused whole must not name the mod either.
    [Fact]
    public void A_manifest_from_a_future_schema_does_not_name_the_mod()
    {
        var arc = ExtractionRoot();
        Write(Path.Combine(arc, "AERR.dll"));
        Write(Path.Combine(arc, ".dds2mod.json"), """{"schema":99,"name":"From The Future"}""");

        Assert.Equal("AERR", Name(Install(), arc, ModType.DllPlugin, isTempRoot: true));
    }

    // ---- the Nexus half of the report ----------------------------------------------------------

    // Fixing the name does NOT restore the Nexus card for this mod, and saying otherwise would be a
    // false promise. "AERR" normalises to a 4-character key, below MinimumKeyLength - and even
    // without that gate the page is titled "AE Revolutions Reloaded", which no normalisation of the
    // author's acronym reaches.
    [Fact]
    public void A_correct_name_is_still_not_enough_to_match_an_acronym_to_its_nexus_page()
    {
        var catalogue = new[]
        {
            new NexusModPost { ModId = 79, Name = "AE Revolutions Reloaded", GameDomain = "drugdealersimulator" }
        };

        var index = NexusModMatcher.BuildIndex(catalogue);

        Assert.Null(NexusModMatcher.Match("AERR", index));
        Assert.NotNull(NexusModMatcher.Match("AE Revolutions Reloaded", index));
    }
}
