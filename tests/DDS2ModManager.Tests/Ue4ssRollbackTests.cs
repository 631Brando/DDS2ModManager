namespace DDS2ModManager.Tests;

/// Being able to undo a UE4SS update.
///
/// The reason this has to exist: the manager installs from the "experimental-latest" tag — ONE
/// rolling release whose assets are replaced in place. The build a user was running cannot be
/// downloaded again once it is superseded, and the update overwrote it and deleted the zip. A user
/// broken by an update had no way back at all, which is what a real report surfaced.
public class Ue4ssRollbackTests : IDisposable
{
    private readonly List<string> _temps = [];

    public void Dispose()
    {
        foreach (var t in _temps) { try { Directory.Delete(t, true); } catch { } }
    }

    private GameInstallation Install()
    {
        var root = Path.Combine(Path.GetTempPath(), "dds_rb_" + Guid.NewGuid().ToString("N")[..8]);
        _temps.Add(root);
        Directory.CreateDirectory(Path.Combine(root, "DrugDealerSimulator2", "Binaries", "Win64"));
        return new GameInstallation { RootPath = root };
    }

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    /// Nothing has been replaced yet, so there is nothing to offer. The button is absent rather
    /// than disabled — there is no useful thing to say to someone who has never updated.
    [Fact]
    public void A_fresh_install_has_nothing_to_go_back_to()
    {
        Assert.Null(UE4SSManagerService.FindPreviousBuild(Install()));
    }

    /// The asset name is the only string that identifies a build. Several different UE4SS builds
    /// all report themselves as "v3.0.1 Beta", which is exactly why the report that prompted this
    /// quoted SHAs rather than versions.
    [Fact]
    public void A_kept_build_remembers_which_build_it_was()
    {
        var game = Install();
        var kept = AppPaths.PreviousUE4SSFor(game.RootPath);
        _temps.Add(kept);

        Write(Path.Combine(kept, "ue4ss", "UE4SS.dll"), "old");
        Write(Path.Combine(kept, "asset.txt"), "zDEV-UE4SS_v3.0.1-1093-gba2efd55.zip");

        var previous = UE4SSManagerService.FindPreviousBuild(game);

        Assert.NotNull(previous);
        Assert.Equal("zDEV-UE4SS_v3.0.1-1093-gba2efd55.zip", previous!.AssetName);
        Assert.Contains("1093", previous.Display);
    }

    /// Restoring is "undo the update", so the whole payload goes back as it was — including files
    /// the newer build added, which must not survive.
    [Fact]
    public void Restoring_puts_the_old_payload_back_and_removes_the_new_one()
    {
        var game = Install();
        var kept = AppPaths.PreviousUE4SSFor(game.RootPath);
        _temps.Add(kept);

        Write(Path.Combine(kept, "ue4ss", "UE4SS.dll"), "old build");
        Write(Path.Combine(kept, "ue4ss", "Mods", "BPModLoaderMod", "Scripts", "main.lua"), "-- old lua");
        Write(Path.Combine(kept, "dwmapi.dll"), "old proxy");
        Write(Path.Combine(kept, "asset.txt"), "UE4SS_v3.0.1-1093-gba2efd55.zip");

        Write(Path.Combine(game.UE4SSRootPath, "UE4SS.dll"), "new build");
        Write(Path.Combine(game.UE4SSRootPath, "AddedByTheNewBuild.dll"), "new");
        Write(Path.Combine(game.Win64Path, "dwmapi.dll"), "new proxy");

        Assert.True(UE4SSManagerService.RestorePreviousBuild(game));

        Assert.Equal("old build", File.ReadAllText(Path.Combine(game.UE4SSRootPath, "UE4SS.dll")));
        Assert.Equal("-- old lua",
            File.ReadAllText(Path.Combine(game.UE4SSRootPath, "Mods", "BPModLoaderMod", "Scripts", "main.lua")));

        // The proxy is half the install and lives OUTSIDE the ue4ss folder, so a restore that
        // skipped it would put back the loader's files and none of what loads them.
        Assert.Equal("old proxy", File.ReadAllText(Path.Combine(game.Win64Path, "dwmapi.dll")));

        Assert.False(File.Exists(Path.Combine(game.UE4SSRootPath, "AddedByTheNewBuild.dll")));
    }

    /// The copy is left behind on purpose: a restore that turns out not to have been the problem
    /// has to be repeatable, and re-updating is the way forward again.
    [Fact]
    public void The_kept_copy_survives_being_restored()
    {
        var game = Install();
        var kept = AppPaths.PreviousUE4SSFor(game.RootPath);
        _temps.Add(kept);

        Write(Path.Combine(kept, "ue4ss", "UE4SS.dll"), "old");
        Write(Path.Combine(kept, "asset.txt"), "UE4SS_old.zip");
        Write(Path.Combine(game.UE4SSRootPath, "UE4SS.dll"), "new");

        Assert.True(UE4SSManagerService.RestorePreviousBuild(game));
        Assert.NotNull(UE4SSManagerService.FindPreviousBuild(game));
    }

    [Fact]
    public void Restoring_nothing_fails_rather_than_wiping_what_is_there()
    {
        var game = Install();
        Write(Path.Combine(game.UE4SSRootPath, "UE4SS.dll"), "current");

        Assert.False(UE4SSManagerService.RestorePreviousBuild(game));
        Assert.Equal("current", File.ReadAllText(Path.Combine(game.UE4SSRootPath, "UE4SS.dll")));
    }

    /// Two installs update independently, so one game's kept build must never be offered as the
    /// other's — the same reasoning that keys every other per-game store.
    [Fact]
    public void Each_game_keeps_its_own()
    {
        var a = Install();
        var b = Install();

        Assert.NotEqual(AppPaths.PreviousUE4SSFor(a.RootPath), AppPaths.PreviousUE4SSFor(b.RootPath));

        var kept = AppPaths.PreviousUE4SSFor(a.RootPath);
        _temps.Add(kept);
        Write(Path.Combine(kept, "ue4ss", "UE4SS.dll"), "a's build");
        Write(Path.Combine(kept, "asset.txt"), "UE4SS_a.zip");

        Assert.NotNull(UE4SSManagerService.FindPreviousBuild(a));
        Assert.Null(UE4SSManagerService.FindPreviousBuild(b));
    }

    /// Kept out of the game folder deliberately: Unreal enumerates Content\Paks recursively and
    /// UE4SS scans its own Mods folder, so a spare copy parked beside either reads as live content.
    [Fact]
    public void The_copy_is_not_kept_inside_the_game()
    {
        var game = Install();

        Assert.DoesNotContain(game.RootPath, AppPaths.PreviousUE4SSFor(game.RootPath));
    }
}
