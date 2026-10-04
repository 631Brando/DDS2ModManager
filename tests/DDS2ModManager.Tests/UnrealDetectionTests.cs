using System.Text;
using CUE4Parse.UE4.Versions;
using DDS2ModManager.Services;

namespace DDS2ModManager.Tests;

/// Deciding what a folder IS - a game, a husk an uninstall left behind, or nothing - and which
/// engine built it. Both used to be wrong in ways that were invisible until something read the
/// wrong path: an uninstalled DDS2 still counted as installed because UE4SS had left Binaries\Win64
/// behind, and Mordhau resolved its project folder to Engine.
public class UnrealDetectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dds2mm_ue_" + Guid.NewGuid().ToString("N")[..8]);

    public UnrealDetectionTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private string Game(string name) { var p = Path.Combine(_root, name); Directory.CreateDirectory(p); return p; }

    private static void Touch(string path, int bytes = 16)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
    }

    private static void MakeProject(string root, string project, bool exe = true, bool pak = true)
    {
        Directory.CreateDirectory(Path.Combine(root, project, "Binaries", "Win64"));
        Directory.CreateDirectory(Path.Combine(root, project, "Content", "Paks"));
        if (exe) Touch(Path.Combine(root, project, "Binaries", "Win64", $"{project}-Win64-Shipping.exe"));
        if (pak) Touch(Path.Combine(root, project, "Content", "Paks", $"{project}-WindowsNoEditor.pak"));
    }

    // ---- project folder ---------------------------------------------------------------------

    // The Mordhau bug. Engine has its own Binaries\Win64 and sorts first, so "first folder with
    // Binaries\Win64" pointed every path into the engine.
    [Fact]
    public void Engine_is_never_taken_for_the_project()
    {
        var root = Game("Mordhau");
        Directory.CreateDirectory(Path.Combine(root, "Engine", "Binaries", "Win64"));
        MakeProject(root, "Mordhau");

        Assert.Equal("Mordhau", UnrealInstallInspector.FindProjectFolder(root));
    }

    // A stray folder that happens to have Binaries\Win64 must lose to the one whose executable is
    // named after it, which is how Unreal names a packaged game's binary.
    [Fact]
    public void The_folder_with_an_executable_named_after_it_wins()
    {
        var root = Game("Thing");
        Directory.CreateDirectory(Path.Combine(root, "AAA_Tools", "Binaries", "Win64"));
        MakeProject(root, "RealGame");

        Assert.Equal("RealGame", UnrealInstallInspector.FindProjectFolder(root));
    }

    [Fact]
    public void A_folder_with_no_unreal_project_is_not_unreal()
    {
        var root = Game("Valheim");
        Touch(Path.Combine(root, "valheim.exe"));

        Assert.Equal(UnrealInstallState.NotUnreal, UnrealInstallInspector.Inspect(root, out var project));
        Assert.Null(project);
    }

    // Through GameInstallation, because that is where every install, save and config path comes
    // from - the inspector being right is no use if the paths built on it aren't.
    [Fact]
    public void Install_paths_point_into_the_project_not_the_engine()
    {
        var root = Game("Mordhau");
        Directory.CreateDirectory(Path.Combine(root, "Engine", "Binaries", "Win64"));
        MakeProject(root, "Mordhau");

        var install = new GameInstallation { RootPath = root };

        Assert.Equal("Mordhau", install.ProjectName);
        Assert.Equal(Path.Combine(root, "Mordhau", "Content", "Paks"), install.PaksPath);
    }

    // The deleted-DDS2 case end to end: the folder still has the SHAPE of a project, which is what
    // IsValid checks and why it can't be trusted to mean "a game is here".
    [Fact]
    public void A_leftover_has_the_shape_of_an_install_but_is_not_installed()
    {
        var root = Game("Drug Dealer Simulator 2");
        MakeProject(root, "DrugDealerSimulator2", exe: false, pak: false);

        var install = new GameInstallation { RootPath = root };

        Assert.True(install.IsValid);
        Assert.False(install.IsInstalled);
        Assert.Equal(UnrealInstallState.Leftover, install.InstallState);
        Assert.Null(install.ExecutablePath);
    }

    // ---- installed vs leftover --------------------------------------------------------------

    [Fact]
    public void An_executable_and_base_paks_make_an_install()
    {
        var root = Game("DrugDealerSimulator");
        MakeProject(root, "DrugDealerSimulator");

        Assert.Equal(UnrealInstallState.Installed, UnrealInstallInspector.Inspect(root, out var project));
        Assert.Equal("DrugDealerSimulator", project);
    }

    // What an uninstalled DDS2 really looked like: Steam removed the game, UE4SS's dumps stayed.
    [Fact]
    public void Binaries_left_behind_by_ue4ss_are_a_leftover_not_a_game()
    {
        var root = Game("Drug Dealer Simulator 2");
        MakeProject(root, "DrugDealerSimulator2", exe: false, pak: false);
        Touch(Path.Combine(root, "DrugDealerSimulator2", "Binaries", "Win64", "ue4ss", "UE4SS.dll"));
        Touch(Path.Combine(root, "DrugDealerSimulator2", "Binaries", "Win64", "dwmapi.dll"));

        Assert.Equal(UnrealInstallState.Leftover, UnrealInstallInspector.Inspect(root, out _));
    }

    // Mods survive an uninstall just like UE4SS does. A few left in a subfolder - or a _P pak at
    // the top level, the override convention - are not the game's own content.
    [Fact]
    public void Mod_paks_alone_do_not_count_as_the_game()
    {
        var root = Game("Husk");
        MakeProject(root, "Husk", exe: true, pak: false);
        Touch(Path.Combine(root, "Husk", "Content", "Paks", "LogicMods", "SomeMod.pak"));
        Touch(Path.Combine(root, "Husk", "Content", "Paks", "MyOverride_P.pak"));

        Assert.Equal(UnrealInstallState.Leftover, UnrealInstallInspector.Inspect(root, out _));
    }

    [Fact]
    public void Paks_without_an_executable_are_not_an_install()
    {
        var root = Game("Partial");
        MakeProject(root, "Partial", exe: false, pak: true);

        Assert.Equal(UnrealInstallState.Leftover, UnrealInstallInspector.Inspect(root, out _));
    }

    [Fact]
    public void Iostore_containers_count_as_base_content()
    {
        var root = Game("Ue5Game");
        MakeProject(root, "Ue5Game", exe: true, pak: false);
        Touch(Path.Combine(root, "Ue5Game", "Content", "Paks", "global.utoc"));

        Assert.Equal(UnrealInstallState.Installed, UnrealInstallInspector.Inspect(root, out _));
    }

    [Fact]
    public void The_shipping_build_is_preferred_over_helper_executables()
    {
        var root = Game("Shippy");
        MakeProject(root, "Shippy", exe: false, pak: true);
        var win64 = Path.Combine(root, "Shippy", "Binaries", "Win64");
        Touch(Path.Combine(win64, "CrashReportClient.exe"));
        Touch(Path.Combine(win64, "Shippy-Win64-Shipping.exe"));

        Assert.EndsWith("Shippy-Win64-Shipping.exe", UnrealInstallInspector.FindGameExecutable(Path.Combine(root, "Shippy")));
    }

    [Fact]
    public void Anti_cheat_beside_the_game_is_detected()
    {
        var root = Game("Shooter");
        MakeProject(root, "Shooter");
        Directory.CreateDirectory(Path.Combine(root, "EasyAntiCheat"));
        Touch(Path.Combine(root, "Shooter", "Binaries", "Win64", "BEService_x64.exe"));

        var found = UnrealInstallInspector.DetectAntiCheat(root, "Shooter");

        Assert.True(found.HasFlag(AntiCheat.EasyAntiCheat));
        Assert.True(found.HasFlag(AntiCheat.BattlEye));
    }

    [Fact]
    public void No_anti_cheat_is_reported_as_none()
    {
        var root = Game("Peaceful");
        MakeProject(root, "Peaceful");

        Assert.Equal(AntiCheat.None, UnrealInstallInspector.DetectAntiCheat(root, "Peaceful"));
    }

    // ---- engine version ---------------------------------------------------------------------

    [Theory]
    [InlineData("++UE4+Release-4.21-CL-4753647", 4, 21)]
    [InlineData("++UE5+Release-5.3-CL-27405482", 5, 3)]
    [InlineData("junk before ++UE4+Release-4.27 and after", 4, 27)]
    public void Build_strings_are_parsed(string text, int major, int minor) =>
        Assert.Equal((major, minor), UnrealEngineProbe.ParseBuildString(text));

    // "++UE5" followed by a 4.x release is not a build string - both halves must agree.
    [Theory]
    [InlineData("++UE5+Release-4.21")]
    [InlineData("no version here")]
    [InlineData("++UE4+Debug-4.21")]
    public void Things_that_are_not_build_strings_are_rejected(string text) =>
        Assert.Null(UnrealEngineProbe.ParseBuildString(text));

    [Fact]
    public void A_version_maps_to_the_matching_egame() =>
        Assert.Equal(EGame.GAME_UE4_21, UnrealEngineProbe.ToEGame(4, 21));

    // An engine newer than CUE4Parse knows is read with the newest rules it has - rounding down,
    // never up, since rounding up would claim features the game doesn't have.
    [Fact]
    public void An_unknown_newer_version_rounds_down_to_the_newest_known()
    {
        var game = UnrealEngineProbe.ToEGame(5, 99);

        Assert.True((int)game >= (int)EGame.GAME_UE5_0);
        Assert.True((int)game <= (5 << 24 | 99 << 16));
        Assert.DoesNotContain("LATEST", game.ToString());
    }

    [Theory]
    [InlineData(7, EGame.GAME_UE4_21)]    // DDS1's real pak
    [InlineData(8, EGame.GAME_UE4_24)]
    [InlineData(10, EGame.GAME_UE4_26)]
    [InlineData(11, EGame.GAME_UE4_27)]   // Mordhau's real pak, no IoStore
    [InlineData(12, EGame.GAME_UE5_3)]
    public void Pak_versions_map_to_engine_ranges(int pak, EGame expected)
    {
        var guess = UnrealEngineProbe.FromContainers(pak, tocVersion: null, usesIoStore: false);

        Assert.Equal(expected, guess.Game);
        Assert.False(guess.IsExact);
    }

    // Pak v11 is shared by 4.27 and UE5.0-5.2; IoStore's TOC version is what tells them apart.
    [Fact]
    public void The_iostore_toc_splits_what_the_pak_version_cannot()
    {
        var ue427 = UnrealEngineProbe.FromContainers(11, tocVersion: 3, usesIoStore: true);
        var ue5 = UnrealEngineProbe.FromContainers(11, tocVersion: 5, usesIoStore: true);

        Assert.False(ue427.IsUe5);
        Assert.True(ue5.IsUe5);
    }

    [Fact]
    public void No_evidence_is_labelled_as_unknown_rather_than_dressed_up()
    {
        var guess = UnrealEngineProbe.FromContainers(null, null, usesIoStore: false);

        Assert.False(guess.IsExact);
        Assert.Contains("Unknown", guess.Label);
    }

    [Fact]
    public void The_pak_version_is_read_from_the_footer()
    {
        var pak = Path.Combine(_root, "test.pak");
        var bytes = new byte[2048];
        // Footer: magic then version, followed by the index offset/size/hash the real format has.
        BitConverter.GetBytes(UnrealEngineProbe.PakMagic).CopyTo(bytes, 2048 - 44);
        BitConverter.GetBytes(7).CopyTo(bytes, 2048 - 40);
        File.WriteAllBytes(pak, bytes);

        Assert.Equal(7, UnrealEngineProbe.ReadPakVersion(pak));
    }

    [Fact]
    public void A_file_without_the_pak_magic_has_no_version()
    {
        var pak = Path.Combine(_root, "notapak.pak");
        File.WriteAllBytes(pak, new byte[2048]);

        Assert.Null(UnrealEngineProbe.ReadPakVersion(pak));
    }

    [Fact]
    public void The_toc_version_is_read_from_the_header()
    {
        var utoc = Path.Combine(_root, "global.utoc");
        var bytes = Encoding.ASCII.GetBytes("-==--==--==--==-").Concat(new byte[] { 6, 0, 0, 0 }).ToArray();
        File.WriteAllBytes(utoc, bytes);

        Assert.Equal(6, UnrealEngineProbe.ReadTocVersion(utoc));
    }

    // The executable is streamed in 4 MB chunks; a build string straddling a chunk boundary must
    // still be found whole.
    [Fact]
    public void A_build_string_across_a_chunk_boundary_is_still_found()
    {
        var exe = Path.Combine(_root, "Game-Win64-Shipping.exe");
        var marker = Encoding.Unicode.GetBytes("++UE4+Release-4.21-CL-4753647");
        var bytes = new byte[4 * 1024 * 1024 + 4096];
        var at = 4 * 1024 * 1024 - 10;   // starts 10 bytes before the boundary
        marker.CopyTo(bytes, at);
        File.WriteAllBytes(exe, bytes);

        Assert.Equal((4, 21), UnrealEngineProbe.ReadExeBuildVersion(exe));
    }

    [Fact]
    public void The_executable_build_string_outranks_the_pak_estimate()
    {
        var root = Game("Exact");
        MakeProject(root, "Exact", exe: false, pak: false);
        var exe = Path.Combine(root, "Exact", "Binaries", "Win64", "Exact-Win64-Shipping.exe");
        File.WriteAllBytes(exe, new byte[64].Concat(Encoding.Unicode.GetBytes("++UE4+Release-4.21")).ToArray());

        var pak = Path.Combine(root, "Exact", "Content", "Paks", "Exact-WindowsNoEditor.pak");
        var pakBytes = new byte[1024];
        BitConverter.GetBytes(UnrealEngineProbe.PakMagic).CopyTo(pakBytes, 1000);
        BitConverter.GetBytes(11).CopyTo(pakBytes, 1004);   // would estimate 4.27
        File.WriteAllBytes(pak, pakBytes);

        var guess = UnrealEngineProbe.Probe(Path.Combine(root, "Exact"), readExecutable: true);

        Assert.True(guess.IsExact);
        Assert.Equal(EGame.GAME_UE4_21, guess.Game);
    }

    // ---- Steam artwork ----------------------------------------------------------------------

    private string SteamRoot()
    {
        var steam = Path.Combine(_root, "Steam");
        Directory.CreateDirectory(Path.Combine(steam, "appcache", "librarycache"));
        return steam;
    }

    private static string Cache(string steam) => Path.Combine(steam, "appcache", "librarycache");

    [Fact]
    public void Art_is_found_in_the_flat_per_app_layout()
    {
        var steam = SteamRoot();
        Touch(Path.Combine(Cache(steam), "1708850", "library_600x900.jpg"), 4096);
        Touch(Path.Combine(Cache(steam), "1708850", "header.jpg"), 4096);

        var art = SteamLibraryArt.Find(steam, 1708850);

        Assert.NotNull(art.Capsule);
        Assert.NotNull(art.Header);
    }

    // DDS1's real layout: one hash-named subfolder per asset.
    [Fact]
    public void Art_is_found_in_the_hashed_subfolder_layout()
    {
        var steam = SteamRoot();
        Touch(Path.Combine(Cache(steam), "682990", "d9982816b4915d16c9d6c14f1d582834b08e711f", "library_600x900.jpg"), 4096);
        Touch(Path.Combine(Cache(steam), "682990", "0b026e1335ece66f6271e3121c8551b305c0bf48", "library_hero.jpg"), 4096);

        var art = SteamLibraryArt.Find(steam, 682990);

        Assert.NotNull(art.Capsule);
        Assert.NotNull(art.Hero);
    }

    [Fact]
    public void Art_is_found_in_the_legacy_flat_file_layout()
    {
        var steam = SteamRoot();
        Touch(Path.Combine(Cache(steam), "629760_library_600x900.jpg"), 4096);

        Assert.NotNull(SteamLibraryArt.Find(steam, 629760).Capsule);
    }

    // Steam leaves tiny placeholders while art is pending; an Image fed one errors instead of
    // falling back.
    [Fact]
    public void Placeholder_files_are_ignored()
    {
        var steam = SteamRoot();
        Touch(Path.Combine(Cache(steam), "123", "library_600x900.jpg"), 4);

        Assert.Null(SteamLibraryArt.Find(steam, 123).Capsule);
    }

    [Fact]
    public void A_game_with_no_cached_art_has_none()
    {
        var steam = SteamRoot();

        var art = SteamLibraryArt.Find(steam, 999999);

        Assert.False(art.HasAny);
    }
}
