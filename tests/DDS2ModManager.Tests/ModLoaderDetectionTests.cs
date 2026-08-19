namespace DDS2ModManager.Tests;

/// Finding the mod loaders a game actually has, and knowing which we may install.
///
/// Two separate dangers here, and they pull in opposite directions:
///
///  * Under-detecting. UE4SS's older layout puts UE4SS.dll straight in Binaries\Win64. The manager
///    used to look only in ue4ss\, so a working install read as "absent" - which lights up the
///    Install button and drops an incompatible second copy on top of it.
///  * Over-removing. Under that same layout there is no folder that belongs solely to UE4SS. The
///    one containing it is Binaries\Win64, which holds the game's executable.
public class ModLoaderDetectionTests : IDisposable
{
    private readonly List<string> _temps = [];

    public void Dispose()
    {
        foreach (var t in _temps) { try { Directory.Delete(t, true); } catch { } }
    }

    private GameInstallation Install(string projectFolder)
    {
        var root = Path.Combine(Path.GetTempPath(), "dds_loader_" + Guid.NewGuid().ToString("N")[..8]);
        _temps.Add(root);
        Directory.CreateDirectory(Path.Combine(root, projectFolder, "Binaries", "Win64"));
        return new GameInstallation { RootPath = root };
    }

    private static void Touch(string path, string content = "x")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    /// A stand-in for the real proxy: dxgi.dll is only UnrealModUnlocker when the binary says so.
    private static void TouchUnlocker(GameInstallation game) =>
        Touch(Path.Combine(game.Win64Path, "dxgi.dll"), "MZ...UnrealModUnlocker.dll...");

    // ---- detection ----------------------------------------------------------------------------

    [Fact]
    public void The_modern_ue4ss_layout_is_detected()
    {
        var game = Install("DrugDealerSimulator2");
        Touch(Path.Combine(game.Win64Path, "ue4ss", "UE4SS.dll"));
        Touch(Path.Combine(game.Win64Path, "dwmapi.dll"));

        var ue4ss = new ModLoaderService().Detect(game, ModLoaders.UE4SS)!;

        Assert.True(ue4ss.IsInstalled);
        Assert.Equal(LoaderLayout.Modern, ue4ss.Layout);
        Assert.Equal(Path.Combine(game.Win64Path, "ue4ss"), ue4ss.RemovableRoot);
        Assert.Contains(Path.Combine(game.Win64Path, "dwmapi.dll"), ue4ss.RemovableFiles);
    }

    // The case that used to read as "not installed" and light up the Install button.
    [Fact]
    public void The_legacy_ue4ss_layout_is_detected_as_installed()
    {
        var game = Install("DrugDealerSimulator");
        Touch(Path.Combine(game.Win64Path, "UE4SS.dll"));
        Touch(Path.Combine(game.Win64Path, "Mods", "mods.txt"));

        var ue4ss = new ModLoaderService().Detect(game, ModLoaders.UE4SS)!;

        Assert.True(ue4ss.IsInstalled);
        Assert.Equal(LoaderLayout.Legacy, ue4ss.Layout);
        Assert.Equal(Path.Combine(game.Win64Path, "Mods"), ue4ss.ModsPath);
    }

    // THE load-bearing invariant. Under the legacy layout the only folder "containing UE4SS" is
    // Binaries\Win64, which holds the game executable and the user's mods. There must be no
    // removable root at all - and specifically it must never be Win64 itself.
    [Fact]
    public void The_legacy_layout_offers_no_removable_root()
    {
        var game = Install("DrugDealerSimulator");
        Touch(Path.Combine(game.Win64Path, "UE4SS.dll"));

        var ue4ss = new ModLoaderService().Detect(game, ModLoaders.UE4SS)!;

        Assert.Null(ue4ss.RemovableRoot);
        Assert.NotEqual(game.Win64Path, ue4ss.RemovableRoot);
        Assert.Empty(ue4ss.RemovableFiles);
        Assert.False(ue4ss.CanRemoveAutomatically);
        Assert.NotNull(ue4ss.RemovalBlockedReason);
    }

    // Nothing any backend proposes to delete may be the Win64 folder itself, or anything outside it.
    [Fact]
    public void No_removal_plan_ever_escapes_the_binaries_folder()
    {
        foreach (var game in new[] { Install("DrugDealerSimulator2"), Install("DrugDealerSimulator") })
        {
            Touch(Path.Combine(game.Win64Path, "ue4ss", "UE4SS.dll"));
            Touch(Path.Combine(game.Win64Path, "dwmapi.dll"));
            TouchUnlocker(game);
            Touch(Path.Combine(game.Win64Path, "ModLoaderInfo.ini"));

            var win64 = Path.TrimEndingDirectorySeparator(Path.GetFullPath(game.Win64Path));

            foreach (var loader in new ModLoaderService().DetectAll(game))
            {
                foreach (var target in loader.RemovableFiles.Concat(new[] { loader.RemovableRoot })
                             .Where(t => t != null)!)
                {
                    var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target!));

                    Assert.NotEqual(win64, full);
                    Assert.StartsWith(win64 + Path.DirectorySeparatorChar, full);
                }
            }
        }
    }

    // Most DDS1 mods ship as loose .uasset files and load only when this is present. Failing to see
    // it means never being able to tell the user why their mods silently do nothing.
    [Fact]
    public void Unreal_mod_unlocker_is_detected_from_its_proxy_dll()
    {
        var game = Install("DrugDealerSimulator");
        TouchUnlocker(game);

        var umu = new ModLoaderService().Detect(game, ModLoaders.UnrealModUnlocker)!;

        Assert.True(umu.IsInstalled);
        Assert.Equal(LoaderLayout.Flat, umu.Layout);
        Assert.True(umu.CanRemoveAutomatically);   // one file, no folder
        Assert.Null(umu.RemovableRoot);
    }

    // dxgi.dll is one of the most-reused proxy names there is. Trusting the filename was tolerable
    // while detection only REPORTED presence; it now chooses where a DLL plugin gets installed, and
    // ReShade's folder is not a place a mod loads from.
    [Fact]
    public void Some_other_tools_dxgi_dll_is_not_the_unlocker()
    {
        var game = Install("DrugDealerSimulator");
        Touch(Path.Combine(game.Win64Path, "dxgi.dll"), "MZ...ReShade 6.1.1...");

        var umu = new ModLoaderService().Detect(game, ModLoaders.UnrealModUnlocker)!;

        Assert.False(umu.IsInstalled);
        Assert.Null(umu.PluginFolder);
    }

    // A loader that makes no sense for a game is never even looked for - DDS2 is IoStore, so loose
    // assets cannot load and reporting the unlocker as "missing" would be noise.
    [Fact]
    public void Only_loaders_the_game_could_use_are_reported()
    {
        var game = Install("DrugDealerSimulator2");
        TouchUnlocker(game);

        var loaders = new ModLoaderService().DetectAll(game);

        Assert.DoesNotContain(loaders, l => l.Loader == ModLoaders.UnrealModUnlocker);
        Assert.Contains(loaders, l => l.Loader == ModLoaders.UE4SS);
    }

    // ---- the install gate ---------------------------------------------------------------------

    // The whole reason InstallableLoaders exists. Stock and experimental UE4SS both crash DDS1 on
    // startup; the build it needs is not published. Detecting it is fine, installing it is not.
    [Fact]
    public void Dds1_never_offers_to_install_a_loader()
    {
        Assert.Equal(ModLoaders.None, GameProfiles.Dds1.InstallableLoaders);
        Assert.Equal(ModLoaders.UE4SS, GameProfiles.Dds2.InstallableLoaders);
    }

    [Fact]
    public void The_install_gate_is_reported_with_a_reason()
    {
        var dds1 = Install("DrugDealerSimulator");
        var status = new UE4SSManagerService().GetCurrentStatus(dds1);

        Assert.False(status.CanInstall);
        Assert.False(string.IsNullOrWhiteSpace(status.InstallBlockedReason));

        var dds2 = Install("DrugDealerSimulator2");
        Assert.True(new UE4SSManagerService().GetCurrentStatus(dds2).CanInstall);
    }

    // ---- config discovery -----------------------------------------------------------------------

    // The legacy settings file sits among the game's binaries. Looking only in ue4ss\ showed a DDS1
    // user no loader settings at all, while the loader read that file on every launch.
    [Fact]
    public void The_config_folder_follows_the_layout()
    {
        var legacy = Install("DrugDealerSimulator");
        Touch(Path.Combine(legacy.Win64Path, "UE4SS.dll"));
        Assert.Equal(legacy.Win64Path, new ModLoaderService().Detect(legacy, ModLoaders.UE4SS)!.ConfigFolder);

        var modern = Install("DrugDealerSimulator2");
        Touch(Path.Combine(modern.Win64Path, "ue4ss", "UE4SS.dll"));
        Assert.Equal(Path.Combine(modern.Win64Path, "ue4ss"),
            new ModLoaderService().Detect(modern, ModLoaders.UE4SS)!.ConfigFolder);
    }
}
