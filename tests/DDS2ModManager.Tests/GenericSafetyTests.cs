using System.Reflection;
using DDS2ModManager.Services;

namespace DDS2ModManager.Tests;

/// Everything that only makes sense for a game someone actually studied - Nexus, installing UE4SS,
/// trusting base-pak names enough to delete by - must fail CLOSED on a game with no profile of its
/// own. Each of these used to fail open, usually by quietly treating the game as DDS2.
public class GenericSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dds2mm_safe_" + Guid.NewGuid().ToString("N")[..8]);

    public GenericSafetyTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private GameInstallation Game(string project, bool generic)
    {
        var root = Path.Combine(_root, project + Guid.NewGuid().ToString("N")[..4]);
        var win64 = Path.Combine(root, project, "Binaries", "Win64");
        var paks = Path.Combine(root, project, "Content", "Paks");
        Directory.CreateDirectory(win64);
        Directory.CreateDirectory(paks);
        File.WriteAllBytes(Path.Combine(win64, $"{project}-Win64-Shipping.exe"), new byte[16]);
        File.WriteAllBytes(Path.Combine(paks, "pakchunk0-WindowsNoEditor.pak"), new byte[16]);

        return new GameInstallation
        {
            RootPath = root,
            Profile = generic
                ? GenericGameProfiles.Create(root, project, null, readExecutable: false)
                : GameProfiles.Dds2
        };
    }

    // ---- Nexus ------------------------------------------------------------------------------

    [Fact]
    public void Only_games_with_a_domain_have_nexus()
    {
        Assert.True(GameProfiles.Dds1.HasNexus);
        Assert.True(GameProfiles.Dds2.HasNexus);
        Assert.False(Game("Generic", generic: true).Profile.HasNexus);
    }

    // Live-tested: Nexus IGNORES a blank gameDomainName and returns the newest mods across the whole
    // site. A generic game must never reach the API with one - it would be shown strangers' mods as
    // "new mods for this game", and get their cards attached to its own mods by name.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task The_catalogue_service_refuses_a_blank_domain(string domain)
    {
        var mods = await new NexusIndexService().GetAsync(domain);

        Assert.Empty(mods);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task The_feed_service_refuses_a_blank_domain(string domain)
    {
        var posts = await new NexusFeedService().GetNewModsAsync(domain, DateTime.UtcNow.AddDays(-14));

        Assert.Empty(posts);
    }

    // ---- what counts as a mod ---------------------------------------------------------------

    // For a game with no profile of its own, nothing directly in Content\Paks is a mod candidate:
    // the base-pak name rules are DDS-specific, and this list feeds Reset's File.Delete.
    [Fact]
    public void A_generic_game_is_never_scanned_at_the_top_of_its_paks_folder()
    {
        var game = Game("Generic", generic: true);

        var folders = UnmanagedModScannerService.PakModFolders(game);

        Assert.DoesNotContain(game.PaksPath, folders, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(game.LogicModsPath, folders, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(Path.Combine(game.PaksPath, "~mods"), folders, StringComparer.OrdinalIgnoreCase);
    }

    // The regression direction: DDS1/DDS2's base names ARE known, so loose patch mods at the top of
    // Paks stay manageable there.
    [Fact]
    public void A_built_in_game_still_scans_the_top_of_its_paks_folder()
    {
        var game = Game("DrugDealerSimulator2", generic: false);

        Assert.Contains(game.PaksPath, UnmanagedModScannerService.PakModFolders(game), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Mod_subfolders_of_a_generic_game_are_searched()
    {
        var game = Game("Generic", generic: true);
        var perMod = Path.Combine(game.PaksPath, "~mods", "BetterLockers");
        Directory.CreateDirectory(perMod);

        Assert.Contains(perMod, UnmanagedModScannerService.PakModFolders(game), StringComparer.OrdinalIgnoreCase);
    }

    // ---- lua mods without UE4SS -------------------------------------------------------------

    private static bool InstallLua(GameInstallation game, string modDir)
    {
        var installer = new ModInstallerService(
            game,
            new ModAnalyzerService(game, "", game.Profile.EngineVersion),
            new ModRegistryService(Path.Combine(Path.GetTempPath(), "dds2mm_reg_" + Guid.NewGuid().ToString("N") + ".json")));

        var method = typeof(ModInstallerService).GetMethod("InstallLuaMod", BindingFlags.Instance | BindingFlags.NonPublic)
                     ?? throw new InvalidOperationException("InstallLuaMod not found - was it renamed?");

        var mod = new ModInfo { Name = Path.GetFileName(modDir), Type = ModType.LuaMod };
        return (bool)method.Invoke(installer, [modDir, mod, false])!;
    }

    private string LuaMod(string name)
    {
        var dir = Path.Combine(_root, "src_" + name, name);
        Directory.CreateDirectory(Path.Combine(dir, "Scripts"));
        File.WriteAllText(Path.Combine(dir, "Scripts", "main.lua"), "print('hi')");
        return dir;
    }

    // Installing a lua mod onto a game without UE4SS used to create ue4ss\Mods and mods.txt from
    // nothing - a mod that never loads, and a fake "installed UE4SS" for Reset to act on.
    [Fact]
    public void A_lua_mod_is_refused_where_ue4ss_is_absent_and_cannot_be_installed()
    {
        var game = Game("Generic", generic: true);

        Assert.False(InstallLua(game, LuaMod("CoolMod")));
        Assert.False(Directory.Exists(game.UE4SSRootPath));
    }

    [Fact]
    public void A_lua_mod_installs_where_ue4ss_is_present()
    {
        var game = Game("Generic", generic: true);
        Directory.CreateDirectory(game.UE4SSRootPath);
        File.WriteAllBytes(Path.Combine(game.UE4SSRootPath, "UE4SS.dll"), new byte[16]);

        Assert.True(InstallLua(game, LuaMod("CoolMod")));
        Assert.True(File.Exists(Path.Combine(game.UE4SSModsPath, "CoolMod", "Scripts", "main.lua")));
    }

    // A bare ue4ss\ folder is what the old behaviour left behind, so it must not count as UE4SS.
    [Fact]
    public void An_empty_ue4ss_folder_does_not_count_as_ue4ss()
    {
        var game = Game("Generic", generic: true);
        Directory.CreateDirectory(game.UE4SSModsPath);

        Assert.False(InstallLua(game, LuaMod("CoolMod")));
    }

    // DDS2 can have UE4SS installed afterwards with one button, so its old behaviour stands.
    [Fact]
    public void Dds2_still_accepts_a_lua_mod_before_ue4ss_is_installed()
    {
        var game = Game("DrugDealerSimulator2", generic: false);

        Assert.True(InstallLua(game, LuaMod("CoolMod")));
    }
}
