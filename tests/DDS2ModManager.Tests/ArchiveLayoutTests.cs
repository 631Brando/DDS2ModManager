namespace DDS2ModManager.Tests;

/// Telling a two-part mod apart from two variants of one mod.
///
/// Get this wrong in one direction and the player is asked to choose between "UE4SSMods" and
/// "LogicMods" and installs half a mod. Get it wrong in the other and an x2/x5/x10 archive
/// silently installs all three multipliers at once. Both directions are tested.
public class ArchiveLayoutTests : IDisposable
{
    private readonly List<string> _temp = new();

    private string NewRoot()
    {
        var d = Path.Combine(Path.GetTempPath(), "DDS2MMLayout_" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(d);
        _temp.Add(d);
        return d;
    }

    public void Dispose()
    {
        foreach (var d in _temp) try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { }
    }

    private static void Touch(string path, string content = "x")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    /// The layout this project's own releases ship.
    [Fact]
    public void Two_destination_archive_is_two_parts()
    {
        var root = NewRoot();
        Touch(Path.Combine(root, "UE4SSMods", "MyMod", "Scripts", "main.lua"), "-- lua");
        Touch(Path.Combine(root, "LogicMods", "MyMod", "MyMod.pak"));
        Touch(Path.Combine(root, "INSTALL.txt"));

        var parts = ModArchiveLayoutService.DetectParts(root);

        Assert.Equal(2, parts.Count);
        Assert.Contains(parts, p => ModArchiveLayoutService.KindOf(p) == ModType.LuaMod);
        Assert.Contains(parts, p => ModArchiveLayoutService.KindOf(p) == ModType.LogicMod);
    }

    /// The game-root-relative form, which the older Nexus packages use.
    [Fact]
    public void Game_root_relative_layout_is_detected()
    {
        var root = NewRoot();
        Touch(Path.Combine(root, "Content", "Paks", "LogicMods", "MyMod", "MyMod.pak"));
        Touch(Path.Combine(root, "Binaries", "Win64", "ue4ss", "Mods", "MyMod_Lua", "Scripts", "main.lua"), "-- lua");

        Assert.Equal(2, ModArchiveLayoutService.DetectParts(root).Count);
    }

    /// The regression that matters most: making destination layouts work must not cost the
    /// variant picker, or players silently get every damage multiplier at once.
    [Fact]
    public void Variant_archive_is_not_multi_destination()
    {
        var root = NewRoot();
        foreach (var v in new[] { "x2", "x5", "x10" })
            Touch(Path.Combine(root, v, "Mod.pak"));

        Assert.Empty(ModArchiveLayoutService.DetectParts(root));
        Assert.Equal(3, ModVariantDetectionService.DetectCandidates(root).Count);
    }

    [Fact]
    public void A_plain_single_mod_is_untouched()
    {
        var root = NewRoot();
        Touch(Path.Combine(root, "Simple.pak"));

        Assert.Empty(ModArchiveLayoutService.DetectParts(root));
        Assert.Single(ModVariantDetectionService.DetectCandidates(root));
    }

    /// One destination folder is a normal mod that happens to sit in a named folder. Requiring
    /// two also means a single stray folder called "Mods" cannot hijack an install.
    [Fact]
    public void A_single_destination_folder_is_not_a_multi_part_archive()
    {
        var root = NewRoot();
        Touch(Path.Combine(root, "LogicMods", "MyMod", "MyMod.pak"));

        Assert.Empty(ModArchiveLayoutService.DetectParts(root));
    }

    // ---- halves named after the mod, not after their destinations ---------------------------

    // The reported bug, from the real archive. "EddieWiki" is the script half and "EddieWiki_P"
    // the pak half, with no marker folder anywhere - so the destination detector found nothing and
    // the user was asked which ONE to install. Either answer gives a script half calling into a
    // pak that was never installed, with nothing on screen saying so.
    [Fact]
    public void Same_named_pak_and_lua_siblings_are_two_halves_not_two_versions()
    {
        var root = NewRoot();
        Touch(Path.Combine(root, "EddieWiki", "Scripts", "main.lua"), "-- lua");
        Touch(Path.Combine(root, "EddieWiki_P", "EddieWiki_P.pak"));

        var parts = ModVariantDetectionService.DetectTwoPartSiblings(root);

        Assert.Equal(2, parts.Count);
        Assert.Contains(parts, p => Path.GetFileName(p) == "EddieWiki");
        Assert.Contains(parts, p => Path.GetFileName(p) == "EddieWiki_P");
    }

    // The whole point of the dialog. Must keep asking.
    [Fact]
    public void A_multiplier_set_is_still_variants()
    {
        var root = NewRoot();
        foreach (var v in new[] { "x2", "x5", "x10" })
            Touch(Path.Combine(root, v, "Mod.pak"));

        Assert.Empty(ModVariantDetectionService.DetectTwoPartSiblings(root));
        Assert.Equal(3, ModVariantDetectionService.DetectCandidates(root).Count);
    }

    // The nasty case: a multiplier set where EVERY folder carries the _P suffix, so a naive
    // name rule would collapse them. Blocked twice over - the x2/x5 discriminator survives suffix
    // stripping, AND both folders are pak-bearing.
    [Fact]
    public void A_multiplier_set_whose_folders_all_end_in_P_is_still_variants()
    {
        var root = NewRoot();
        Touch(Path.Combine(root, "Mod_x2_P", "Mod_x2_P.pak"));
        Touch(Path.Combine(root, "Mod_x5_P", "Mod_x5_P.pak"));

        Assert.Empty(ModVariantDetectionService.DetectTwoPartSiblings(root));
        Assert.Equal(2, ModVariantDetectionService.DetectCandidates(root).Count);
    }

    // Proves the DESTINATION half of the rule carries its own weight. These two names reduce to
    // the same key, so the name test passes - but both are paks bound for the same folder, which
    // is the _P load-priority convention: two alternatives, not two halves. Delete the kind check
    // and this test goes red on its own.
    [Fact]
    public void Two_same_named_paks_are_alternatives_not_halves()
    {
        var root = NewRoot();
        Touch(Path.Combine(root, "MyMod", "MyMod.pak"));
        Touch(Path.Combine(root, "MyMod_P", "MyMod_P.pak"));

        Assert.Empty(ModVariantDetectionService.DetectTwoPartSiblings(root));
    }

    // The same, one level down: an all-lua variant set. Only the kind check catches this.
    [Fact]
    public void Two_same_named_lua_folders_are_alternatives_not_halves()
    {
        var root = NewRoot();
        Touch(Path.Combine(root, "MyMod", "Scripts", "main.lua"), "-- a");
        Touch(Path.Combine(root, "MyMod_Lua", "Scripts", "main.lua"), "-- b");

        Assert.Empty(ModVariantDetectionService.DetectTwoPartSiblings(root));
    }

    // There are only two destination families, so three same-named folders cannot all differ.
    // The cap is a consequence of the kind partition, never a count rule of its own.
    [Fact]
    public void Three_same_named_folders_cannot_be_a_two_part_mod()
    {
        var root = NewRoot();
        Touch(Path.Combine(root, "MyMod", "Scripts", "main.lua"), "-- lua");
        Touch(Path.Combine(root, "MyMod_P", "MyMod_P.pak"));
        Touch(Path.Combine(root, "MyMod_Lua", "Scripts", "main.lua"), "-- lua");

        Assert.Empty(ModVariantDetectionService.DetectTwoPartSiblings(root));
    }

    // A stray readme beside the two halves is invisible to a directory scan, so it must not
    // disturb the result.
    [Fact]
    public void A_loose_file_beside_the_two_halves_changes_nothing()
    {
        var root = NewRoot();
        Touch(Path.Combine(root, "MyMod", "Scripts", "main.lua"), "-- lua");
        Touch(Path.Combine(root, "MyMod_P", "MyMod_P.pak"));
        Touch(Path.Combine(root, "README.txt"));

        Assert.Equal(2, ModVariantDetectionService.DetectTwoPartSiblings(root).Count);
    }

    [Fact]
    public void One_installable_folder_is_never_a_part_set()
    {
        var root = NewRoot();
        Touch(Path.Combine(root, "MyMod", "MyMod.pak"));

        Assert.Empty(ModVariantDetectionService.DetectTwoPartSiblings(root));
    }

    // Folder names that normalise to nothing carry no identity to match on.
    [Fact]
    public void Folders_whose_names_reduce_to_nothing_are_not_a_part_set()
    {
        var root = NewRoot();
        Touch(Path.Combine(root, "---", "Scripts", "main.lua"), "-- lua");
        Touch(Path.Combine(root, "___", "Mod.pak"));

        Assert.Empty(ModVariantDetectionService.DetectTwoPartSiblings(root));
    }

    // ---- and the whole reading of the archive, end to end ------------------------------------

    // Describe is what the installer actually calls. The halves must arrive as DestinationParts -
    // each part needs its own root - and must NOT arrive as VariantCandidates, which is what opens
    // the dialog.
    [Fact]
    public void The_reported_archive_is_read_as_parts_and_opens_no_dialog()
    {
        var root = NewRoot();
        Touch(Path.Combine(root, "EddieWiki", "Scripts", "main.lua"), "-- lua");
        Touch(Path.Combine(root, "EddieWiki_P", "EddieWiki_P.pak"));

        var prepared = ModInstallerService.Describe(root, isTemp: false);

        Assert.Equal(2, prepared.DestinationParts.Count);
        Assert.Single(prepared.VariantCandidates);
    }

    [Fact]
    public void A_multiplier_archive_still_reaches_the_dialog()
    {
        var root = NewRoot();
        foreach (var v in new[] { "x2", "x5", "x10" })
            Touch(Path.Combine(root, v, "Mod.pak"));

        var prepared = ModInstallerService.Describe(root, isTemp: false);

        Assert.Empty(prepared.DestinationParts);
        Assert.Equal(3, prepared.VariantCandidates.Count);
    }

    // ---- one name for a part set --------------------------------------------------------------

    // A manifest names the MOD, and both halves ARE that mod - but only one half ships the file.
    // Without propagating it, the reported archive installs as "DDS2 In-Game Wiki" and
    // "EddieWiki_P", whose grouping keys differ, so the two rows never link and enabling one
    // toggles half a mod.
    [Fact]
    public void A_name_declared_by_one_half_names_the_whole_set()
    {
        var root = NewRoot();
        Touch(Path.Combine(root, "EddieWiki", "Scripts", "main.lua"), "-- lua");
        Touch(Path.Combine(root, "EddieWiki", ".dds2mod.json"),
              """{"schema":1,"name":"DDS2 In-Game Wiki","updateUrl":"https://github.com/a/b"}""");
        Touch(Path.Combine(root, "EddieWiki_P", "EddieWiki_P.pak"));

        var parts = ModVariantDetectionService.DetectTwoPartSiblings(root);

        Assert.Equal("DDS2 In-Game Wiki", Installer().SharedPartName(parts));
    }

    // Nothing declared: both halves fall back to folder and pak names, which already reduce to the
    // same key, so there is nothing to propagate and nothing to force.
    [Fact]
    public void A_set_that_declares_no_name_is_left_alone()
    {
        var root = NewRoot();
        Touch(Path.Combine(root, "MyMod", "Scripts", "main.lua"), "-- lua");
        Touch(Path.Combine(root, "MyMod_P", "MyMod_P.pak"));

        var parts = ModVariantDetectionService.DetectTwoPartSiblings(root);

        Assert.Null(Installer().SharedPartName(parts));
    }

    // Two parts declaring DIFFERENT names is an archive telling us they are not one mod. Forcing a
    // shared name there would relabel somebody else's mod.
    [Fact]
    public void Two_parts_declaring_different_names_are_not_renamed()
    {
        var root = NewRoot();
        Touch(Path.Combine(root, "A", "Scripts", "main.lua"), "-- lua");
        Touch(Path.Combine(root, "A", ".dds2mod.json"), """{"schema":1,"name":"First"}""");
        Touch(Path.Combine(root, "B", "B.pak"));
        Touch(Path.Combine(root, "B", ".dds2mod.json"), """{"schema":1,"name":"Second"}""");

        Assert.Null(Installer().SharedPartName(new[]
        {
            Path.Combine(root, "A"), Path.Combine(root, "B")
        }));
    }

    /// SharedPartName reads manifests off disk and needs no game, so a throwaway install is enough.
    private ModInstallerService Installer()
    {
        var gameRoot = NewRoot();
        Directory.CreateDirectory(Path.Combine(gameRoot, "DrugDealerSimulator2", "Binaries", "Win64"));
        var game = new GameInstallation { RootPath = gameRoot };

        return new ModInstallerService(
            game,
            new ModAnalyzerService(game, "", GameProfiles.Dds2.EngineVersion),
            new ModRegistryService(Path.Combine(NewRoot(), "registry.json")));
    }
}
