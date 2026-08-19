using System.Text.Json;

namespace DDS2ModManager.Tests;

/// Loose cooked assets - DDS1's third mod type, and how most of its mods ship.
///
/// The thing that makes these different from every other mod type is that the DIRECTORY TREE is
/// load-bearing. A loose asset only overrides the packed original when it sits at exactly the same
/// relative path, so anything that flattens or re-roots the files produces an install where nothing
/// loads and nothing says why.
public class LooseAssetTests : IDisposable
{
    private readonly List<string> _temps = [];

    public void Dispose()
    {
        foreach (var t in _temps) { try { Directory.Delete(t, true); } catch { } }
    }

    private string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "dds_loose_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        _temps.Add(d);
        return d;
    }

    private static void Touch(string path, string content = "x")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    // ---- the enum numbering ---------------------------------------------------------------------

    /// ModProfileService and ModBackupService serialise ModType as an INTEGER - neither passes a
    /// JsonStringEnumConverter. Inserting a member anywhere but the end silently remaps every saved
    /// profile and backup entry already on disk, turning a user's LogicMod into something else with
    /// nothing reporting a problem. These numbers are a compatibility contract, not an ordering.
    [Fact]
    public void Mod_type_numbers_are_pinned()
    {
        Assert.Equal(0, (int)ModType.Unknown);
        Assert.Equal(1, (int)ModType.PatchMod);
        Assert.Equal(2, (int)ModType.LogicMod);
        Assert.Equal(3, (int)ModType.LuaMod);
        Assert.Equal(4, (int)ModType.LooseAsset);
    }

    /// The concrete failure the pinning prevents: a profile written before LooseAsset existed still
    /// has to read back as the same type.
    [Fact]
    public void A_profile_written_before_loose_assets_existed_still_reads_correctly()
    {
        var json = """{"Schema":1,"Name":"old","Mods":[{"Name":"Scooter","Type":2,"Enabled":true}]}""";

        var profile = JsonSerializer.Deserialize<ModProfile>(json)!;

        Assert.Equal(ModType.LogicMod, profile.Mods.Single().Type);
    }

    // ---- finding the Content root ----------------------------------------------------------------

    // Published both ways: rooted at Content\ so the archive mirrors the game folder, or already
    // inside it. Getting this wrong writes the files one level off, where nothing loads them.
    [Fact]
    public void An_archive_rooted_at_content_is_detected()
    {
        var root = TempDir();
        Touch(Path.Combine(root, "Content", "DataTables", "ItemDatabase.uasset"));

        Assert.Equal(Path.Combine(root, "Content"), ModAnalyzerService.FindLooseAssetRoot(root));
    }

    [Fact]
    public void An_archive_that_starts_inside_content_uses_its_own_root()
    {
        var root = TempDir();
        Touch(Path.Combine(root, "DataTables", "ItemDatabase.uasset"));

        Assert.Equal(root, ModAnalyzerService.FindLooseAssetRoot(root));
    }

    // No cooked assets means this is not a loose-asset mod at all, and must not be treated as one.
    [Fact]
    public void An_archive_with_no_cooked_assets_is_not_a_loose_asset_mod()
    {
        var root = TempDir();
        Touch(Path.Combine(root, "readme.txt"));
        Touch(Path.Combine(root, "Cool.pak"));

        Assert.Null(ModAnalyzerService.FindLooseAssetRoot(root));
    }

    [Fact]
    public void A_umap_counts_as_a_cooked_asset()
    {
        var root = TempDir();
        Touch(Path.Combine(root, "Maps", "Level.umap"));

        Assert.NotNull(ModAnalyzerService.FindLooseAssetRoot(root));
    }

    // ---- capability gating -----------------------------------------------------------------------

    // IoStore leaves no loose-file path for the engine to prefer, so this can only ever be a UE4
    // thing. Gating it on the profile is what keeps DDS2's behaviour identical.
    [Fact]
    public void Only_a_game_that_can_load_loose_assets_declares_support()
    {
        Assert.True(GameProfiles.Dds1.SupportsLooseAssets);
        Assert.False(GameProfiles.Dds2.SupportsLooseAssets);
    }

    // ---- conflicts --------------------------------------------------------------------------------

    // The common DDS1 clash: two mods both shipping the same overriding asset. Whichever was copied
    // last wins outright and the other's edits are simply gone.
    [Fact]
    public void Two_loose_mods_shipping_the_same_asset_conflict()
    {
        var a = new ModInfo
        {
            Name = "BalanceTweaks", Type = ModType.LooseAsset, IsEnabled = true, IsInstalled = true,
            ContainedAssetPaths = ["DataTables/ItemDatabase.uasset", "DataTables/ItemDatabase.uexp"]
        };
        var b = new ModInfo
        {
            Name = "PriceOverhaul", Type = ModType.LooseAsset, IsEnabled = true, IsInstalled = true,
            ContainedAssetPaths = ["DataTables/ItemDatabase.uasset", "DataTables/Drugs.uasset"]
        };

        var conflicts = new CompatibilityCheckerService().CheckConflicts([a, b]);

        var clash = Assert.Single(conflicts);
        Assert.Equal(ConflictKind.FullFileReplacement, clash.Kind);
        Assert.Contains("DataTables/ItemDatabase.uasset", clash.AssetPaths);
    }

    // Two loose mods touching different files are not a conflict, and reporting them as one would
    // make the panel useless on DDS1, where nearly every mod ships loose assets.
    [Fact]
    public void Loose_mods_touching_different_assets_do_not_conflict()
    {
        var a = new ModInfo
        {
            Name = "A", Type = ModType.LooseAsset, IsEnabled = true, IsInstalled = true,
            ContainedAssetPaths = ["DataTables/Drugs.uasset"]
        };
        var b = new ModInfo
        {
            Name = "B", Type = ModType.LooseAsset, IsEnabled = true, IsInstalled = true,
            ContainedAssetPaths = ["StringTables/Names.uasset"]
        };

        Assert.Empty(new CompatibilityCheckerService().CheckConflicts([a, b]));
    }

    // A loose mod and a lua mod share no namespace at all. EVERY lua mod contains Scripts/main.lua,
    // so mixing the two path sets would turn unrelated mods into critical conflicts.
    [Fact]
    public void A_loose_mod_is_not_compared_against_a_lua_mod()
    {
        var loose = new ModInfo
        {
            Name = "Loose", Type = ModType.LooseAsset, IsEnabled = true, IsInstalled = true,
            ContainedAssetPaths = ["Scripts/main.lua"]
        };
        var lua = new ModInfo
        {
            Name = "Lua", Type = ModType.LuaMod, IsEnabled = true, IsInstalled = true,
            ContainedAssetPaths = ["Scripts/main.lua"]
        };

        Assert.Empty(new CompatibilityCheckerService().CheckConflicts([loose, lua]));
    }
}
