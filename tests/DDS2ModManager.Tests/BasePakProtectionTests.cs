namespace DDS2ModManager.Tests;

/// Telling the shipped game apart from a mod.
///
/// This is the highest-consequence predicate in the app. Whatever it calls a "mod" is offered to the
/// user for import AND handed to Reset-to-Vanilla, which deletes it. Getting it wrong on DDS1 means
/// deleting an 11.3 GB base pak that only a full re-download brings back.
///
/// Tested in both directions on purpose: the risk when adding DDS1's rule was regressing DDS2's.
public class BasePakProtectionTests : IDisposable
{
    private readonly List<string> _temps = [];

    public void Dispose()
    {
        foreach (var t in _temps) { try { Directory.Delete(t, true); } catch { } }
    }

    private GameInstallation Install(string projectFolder)
    {
        var root = Path.Combine(Path.GetTempPath(), "dds_base_" + Guid.NewGuid().ToString("N")[..8]);
        _temps.Add(root);
        Directory.CreateDirectory(Path.Combine(root, projectFolder, "Binaries", "Win64"));
        return new GameInstallation { RootPath = root };
    }

    // UE4 names its single pak after the project, so DDS1's base game is
    // "DrugDealerSimulator-WindowsNoEditor.pak" - which matches NEITHER the "pakchunk" nor the
    // "global" rule that protects DDS2. This is the case that made the guard necessary.
    [Fact]
    public void Dds1s_base_pak_is_recognised_as_the_base_game()
    {
        var game = Install("DrugDealerSimulator");

        Assert.True(UnmanagedModScannerService.IsBaseGameArchive("DrugDealerSimulator-WindowsNoEditor", game));
    }

    // The regression direction. DDS2 was protected before this change and must stay protected.
    [Fact]
    public void Dds2s_base_archives_are_still_recognised()
    {
        var game = Install("DrugDealerSimulator2");

        Assert.True(UnmanagedModScannerService.IsBaseGameArchive("pakchunk0-Windows", game));
        Assert.True(UnmanagedModScannerService.IsBaseGameArchive("pakchunk0optional-Windows", game));
        Assert.True(UnmanagedModScannerService.IsBaseGameArchive("global", game));

        // The project-name rule must fire here too - DDS2 could ship one at any patch.
        Assert.True(UnmanagedModScannerService.IsBaseGameArchive("DrugDealerSimulator2-Windows", game));
    }

    // The other failure mode: refusing to manage a real mod because the filter is too eager. A user
    // whose mod is silently treated as base game can never uninstall it through the manager.
    [Fact]
    public void A_real_mod_is_not_mistaken_for_the_base_game()
    {
        foreach (var game in new[] { Install("DrugDealerSimulator"), Install("DrugDealerSimulator2") })
        {
            Assert.False(UnmanagedModScannerService.IsBaseGameArchive("DriveableScooter", game));
            Assert.False(UnmanagedModScannerService.IsBaseGameArchive("BetterPrices_P", game));
            Assert.False(UnmanagedModScannerService.IsBaseGameArchive("ModActor", game));
        }
    }

    // The rule is "project name followed by a hyphen", not a bare prefix: a mod legitimately named
    // after the game it is for must still be manageable.
    [Fact]
    public void A_mod_merely_starting_with_the_game_name_is_still_a_mod()
    {
        var game = Install("DrugDealerSimulator");

        Assert.False(UnmanagedModScannerService.IsBaseGameArchive("DrugDealerSimulatorTweaks", game));
    }

    // ---- container shape ----------------------------------------------------------------------

    // Drives both the install loop and the scanner. Wrong on DDS1 and every mod install warns about
    // two files that cannot exist; wrong on DDS2 and a mod installs without its data.
    [Fact]
    public void Container_extensions_follow_the_games_pak_layout()
    {
        Assert.Equal([".pak", ".ucas", ".utoc"], GameProfiles.Dds2.ContainerExtensions);
        Assert.Equal([".pak"], GameProfiles.Dds1.ContainerExtensions);
    }
}
