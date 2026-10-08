using CUE4Parse.UE4.Versions;

namespace DDS2ModManager.Tests;

/// The engine version from an executable's version resource (Explorer's Properties > Details > File
/// version), which Unreal stamps with the engine version - and the checks that stop a studio's own
/// version number being read as an engine.
public class EngineVersionResourceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dds_ver_" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    [Theory]
    [InlineData(4, 21, 7, true)]     // DDS1's 4.21 branch: pak v7
    [InlineData(4, 26, 11, true)]    // MORDHAU: 4.26.2 writes v11, which the pak table alone called 4.27
    [InlineData(4, 27, 11, true)]
    [InlineData(5, 1, 11, true)]
    [InlineData(5, 3, 12, true)]
    [InlineData(4, 1, 8, false)]     // a studio's "4.1" on a 4.22+ pak is not the engine
    [InlineData(5, 3, 7, false)]     // UE5 never wrote pak v7
    [InlineData(4, 27, null, true)]  // no pak to check against: the resource stands
    public void A_version_resource_is_vetoed_only_when_the_pak_format_contradicts_it(int major, int minor, int? pak, bool consistent)
    {
        Assert.Equal(consistent, UnrealEngineProbe.IsConsistentWithPak((major, minor), pak));
    }

    [Fact]
    public void A_version_resource_is_exact()
    {
        var guess = UnrealEngineProbe.FromFileVersion(4, 26);

        Assert.True(guess.IsExact);
        Assert.Equal("UE 4.26", guess.Label);
        Assert.Equal((4, 26), LoaderCompatibility.VersionOf(guess.Game));
    }

    [Fact]
    public void An_executable_without_an_engine_version_resource_reads_as_unknown()
    {
        Directory.CreateDirectory(_root);
        var fake = Path.Combine(_root, "Game-Win64-Shipping.exe");
        File.WriteAllBytes(fake, new byte[64]);

        Assert.Null(UnrealEngineProbe.ReadExeFileVersion(fake));
        Assert.Null(UnrealEngineProbe.ReadExeFileVersion(Path.Combine(_root, "missing.exe")));
    }

    // Windows' own executables carry versions like 10.0.x - plausible as a version resource, not as
    // an engine, so they must never come back as one.
    [Fact]
    public void A_non_engine_version_resource_is_ignored()
    {
        var notepad = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "notepad.exe");
        if (!File.Exists(notepad)) return;

        Assert.Null(UnrealEngineProbe.ReadExeFileVersion(notepad));
    }

    // With no version resource to go on, a built-in keeps its own profile - the same object, so
    // nothing downstream sees a difference it didn't ask for.
    [Fact]
    public void A_built_in_keeps_its_profile_when_the_install_says_nothing_different()
    {
        var win64 = Path.Combine(_root, "DrugDealerSimulator", "Binaries", "Win64");
        Directory.CreateDirectory(win64);
        File.WriteAllBytes(Path.Combine(win64, "DrugDealerSimulator-Win64-Shipping.exe"), new byte[64]);

        Assert.Same(GameProfiles.Dds1, GenericGameProfiles.ForInstall(GameProfiles.Dds1, _root, "DrugDealerSimulator"));
        Assert.Equal(EGame.GAME_UE4_21, GameProfiles.Dds1.EngineVersion);
    }
}
