namespace DDS2ModManager.Tests;

/// The last two capability gaps: seeing loose assets somebody installed by hand, and noticing when a
/// loose file and a pak mod contend for the same asset.
///
/// Both live in the same awkward truth — ownership of a loose .uasset cannot be recovered from disk.
/// So the rules here are about being honest rather than clever: group what can be grouped, refuse to
/// delete what cannot be attributed, and never claim to know which of two mods the engine will serve.
public class LooseScanAndCrossConflictTests : IDisposable
{
    private readonly List<string> _temps = [];

    public void Dispose()
    {
        foreach (var t in _temps) { try { Directory.Delete(t, true); } catch { } }
    }

    private GameInstallation Install(string projectFolder)
    {
        var root = Path.Combine(Path.GetTempPath(), "dds_ls_" + Guid.NewGuid().ToString("N")[..8]);
        _temps.Add(root);
        Directory.CreateDirectory(Path.Combine(root, projectFolder, "Binaries", "Win64"));
        return new GameInstallation { RootPath = root };
    }

    private static void Write(string path, string text = "x")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private static ModInfo Mod(string name, ModType type, params string[] paths) => new()
    {
        Name = name, Type = type, IsEnabled = true, IsInstalled = true,
        ContainedAssetPaths = paths.ToList()
    };

    // ---- cross-namespace conflicts -----------------------------------------------------------------

    // The two sides describe the same asset in different notations: a pak mod's paths come from
    // CUE4Parse as "<Project>/Content/DataTables/Foo.uasset", a loose mod's are relative to Content.
    // Compared directly they never match, so this contest went unreported entirely.
    [Fact]
    public void A_loose_file_and_a_pak_mod_targeting_one_asset_conflict()
    {
        var loose = Mod("LooseTweaks", ModType.LooseAsset, "DataTables/ItemDatabase.uasset");
        var pak = Mod("PakOverhaul", ModType.PatchMod,
            "DrugDealerSimulator/Content/DataTables/ItemDatabase.uasset");

        var conflicts = new CompatibilityCheckerService().CheckConflicts([loose, pak]);

        var clash = Assert.Single(conflicts);
        Assert.Equal(ConflictKind.LooseOverridesPak, clash.Kind);
        Assert.Contains("LooseTweaks", clash.ModNames);
        Assert.Contains("PakOverhaul", clash.ModNames);
    }

    // The prefix is NOT reconstructed from ProjectName - GameInstallation resolves the project folder
    // from disk, so it can legitimately differ from the prefix baked into the cooked pak. Matching on
    // the tail after /Content/ keeps working when they diverge; rebuilding the prefix would compare
    // nothing and report a clean bill of health.
    [Fact]
    public void The_match_survives_a_prefix_that_differs_from_the_project_folder()
    {
        var loose = Mod("Loose", ModType.LooseAsset, "Drugs/Weed.uasset");
        var pak = Mod("Pak", ModType.PatchMod, "SomeOtherCookedName/Content/Drugs/Weed.uasset");

        var conflicts = new CompatibilityCheckerService().CheckConflicts([loose, pak]);

        Assert.Single(conflicts);
    }

    [Fact]
    public void Different_assets_across_the_two_namespaces_do_not_conflict()
    {
        var loose = Mod("Loose", ModType.LooseAsset, "DataTables/Drugs.uasset");
        var pak = Mod("Pak", ModType.PatchMod, "DrugDealerSimulator/Content/StringTables/Names.uasset");

        Assert.Empty(new CompatibilityCheckerService().CheckConflicts([loose, pak]));
    }

    // Which one the engine actually serves depends on a filesystem hook versus chunk priority. That
    // has to be observed in game, so the card must not print a confident guess.
    [Fact]
    public void No_winner_is_named_for_a_loose_versus_pak_contest()
    {
        var loose = Mod("Loose", ModType.LooseAsset, "DataTables/Foo.uasset");
        var pak = Mod("Pak", ModType.PatchMod, "P/Content/DataTables/Foo.uasset");

        var clash = Assert.Single(new CompatibilityCheckerService().CheckConflicts([loose, pak]));

        Assert.False(clash.ShowsWinner);
    }

    // ---- scanning for hand-installed loose assets ---------------------------------------------------

    // A vanilla install has exactly one thing under Content: Paks. Anything else was put there by a
    // person, which is what makes this scannable when per-file ownership is not recoverable.
    [Fact]
    public void Loose_assets_outside_paks_are_found_and_grouped_by_folder()
    {
        var game = Install("DrugDealerSimulator");
        Write(Path.Combine(game.ContentPath, "DataTables", "Items.uasset"));
        Write(Path.Combine(game.ContentPath, "DataTables", "Items.uexp"));
        Write(Path.Combine(game.ContentPath, "Drugs", "Weed.uasset"));

        var found = new UnmanagedModScannerService()
            .Scan(game, [], mappingsPath: "", egame: GameProfiles.Dds1.EngineVersion, aesKeyHex: null);

        var loose = found.Where(m => m.IsLooseAssetGroup).ToList();

        Assert.Equal(2, loose.Count);
        Assert.Contains(loose, m => m.Name == "DataTables" && m.Files.Count == 2);
        Assert.Contains(loose, m => m.Name == "Drugs" && m.Files.Count == 1);
        Assert.All(loose, m => Assert.Equal(ModType.LooseAsset, m.DetectedType));

        // The row must say what it cannot know, rather than presenting itself as one identified mod.
        Assert.All(loose, m => Assert.Contains(m.Issues, i => i.Contains("can't be recovered")));
    }

    // Content\Paks is the game's own and is covered by the pak scan; picking it up here would report
    // the base game as a loose-asset mod.
    [Fact]
    public void The_paks_folder_is_never_reported_as_loose_assets()
    {
        var game = Install("DrugDealerSimulator");
        Write(Path.Combine(game.PaksPath, "DrugDealerSimulator-WindowsNoEditor.pak"));
        Write(Path.Combine(game.PaksPath, "LogicMods", "Something.uasset"));

        var found = new UnmanagedModScannerService()
            .Scan(game, [], mappingsPath: "", egame: GameProfiles.Dds1.EngineVersion, aesKeyHex: null);

        Assert.DoesNotContain(found, m => m.IsLooseAssetGroup);
    }

    // IoStore leaves no loose-file path for the engine to prefer, so anything under DDS2's Content is
    // not a mod and must not be offered as one.
    [Fact]
    public void A_game_that_cannot_load_loose_assets_is_not_scanned_for_them()
    {
        var game = Install("DrugDealerSimulator2");
        Write(Path.Combine(game.ContentPath, "DataTables", "Items.uasset"));

        var found = new UnmanagedModScannerService()
            .Scan(game, [], mappingsPath: "", egame: GameProfiles.Dds2.EngineVersion, aesKeyHex: null);

        Assert.DoesNotContain(found, m => m.IsLooseAssetGroup);
    }

    // Already-tracked files belong to a mod the manager installed; re-reporting them would offer the
    // user a duplicate import of their own mod.
    [Fact]
    public void Files_already_tracked_are_not_reported_again()
    {
        var game = Install("DrugDealerSimulator");
        var tracked = Path.Combine(game.ContentPath, "DataTables", "Items.uasset");
        Write(tracked);

        var known = new HashSet<string>([tracked], StringComparer.OrdinalIgnoreCase);
        var found = new UnmanagedModScannerService()
            .Scan(game, [new ModInfo { Name = "Mine", InstallFiles = [tracked] }],
                  mappingsPath: "", egame: GameProfiles.Dds1.EngineVersion, aesKeyHex: null);

        Assert.DoesNotContain(found, m => m.IsLooseAssetGroup);
        Assert.NotEmpty(known);
    }
}
