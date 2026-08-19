namespace DDS2ModManager.Tests;

/// Things the UI says and shows, where saying the wrong thing is worse than saying nothing.
///
/// All four of these were caught from one screenshot of the app running on DDS1: the tabs were in
/// the wrong order, the Nexus banner named DDS2 while DDS1 was open, that banner was stale from the
/// previous game entirely, and the UE4SS card described the build as "experimental" - which on DDS1
/// names the exact build that crashes it on startup.
public class UiWordingTests
{
    // ---- tab order ------------------------------------------------------------------------------

    // Game order, left to right. Deliberately NOT GameProfiles.All's order, which decides which game
    // a brand-new user with both installed opens on and still favours DDS2.
    [Fact]
    public void Tabs_read_in_game_order()
    {
        var order = GameProfiles.InDisplayOrder.Select(p => p.Id).ToList();

        Assert.Equal(["dds1", "dds2"], order);
    }

    [Fact]
    public void Display_order_does_not_change_which_game_a_new_user_opens_on()
    {
        // All still leads with DDS2, so detection and the default profile are unaffected.
        Assert.Equal("dds2", GameProfiles.All[0].Id);
        Assert.Equal("dds2", GameProfiles.Default.Id);
    }

    // ---- UE4SS card wording -----------------------------------------------------------------------

    // The one that mattered. On DDS1 the manager must never describe what is installed as the
    // experimental build, nor imply the user should go and get it - that build crashes DDS1.
    [Fact]
    public void A_game_we_cannot_install_for_is_never_told_about_the_experimental_build()
    {
        var legacy = new UE4SSInstallInfo
        {
            IsInstalled = true, Layout = LoaderLayout.Legacy, CanInstall = false
        };
        Assert.Equal("UE4SS installed (older layout)", legacy.StatusLabel);
        Assert.DoesNotContain("experimental", legacy.StatusLabel, StringComparison.OrdinalIgnoreCase);

        var absent = new UE4SSInstallInfo { IsInstalled = false, CanInstall = false };
        Assert.DoesNotContain("experimental", absent.StatusLabel, StringComparison.OrdinalIgnoreCase);

        var modernUnmanaged = new UE4SSInstallInfo
        {
            IsInstalled = true, Layout = LoaderLayout.Modern, CanInstall = false
        };
        Assert.DoesNotContain("experimental", modernUnmanaged.StatusLabel, StringComparison.OrdinalIgnoreCase);
    }

    // DDS2's wording is unchanged - the card still says what it always did.
    [Fact]
    public void Dds2_wording_is_unchanged()
    {
        Assert.Equal("UE4SS not installed",
            new UE4SSInstallInfo { IsInstalled = false, CanInstall = true }.StatusLabel);

        Assert.Equal("UE4SS installed (unverified experimental)",
            new UE4SSInstallInfo { IsInstalled = true, Layout = LoaderLayout.Modern, CanInstall = true }.StatusLabel);

        Assert.Equal("UE4SS experimental - up to date",
            new UE4SSInstallInfo
            {
                IsInstalled = true, Layout = LoaderLayout.Modern, CanInstall = true, IsManagedByUs = true
            }.StatusLabel);
    }

    // ---- the manifest filename ---------------------------------------------------------------------

    // The original stays first so an existing mod behaves exactly as before; the neutral name is an
    // alternative for DDS1 authors, not a replacement.
    [Fact]
    public void Both_manifest_names_are_accepted_original_first()
    {
        Assert.Equal(".dds2mod.json", ModManifest.FileName);
        Assert.Equal(".ddsmod.json", ModManifest.NeutralFileName);
        Assert.Equal([".dds2mod.json", ".ddsmod.json"], ModManifest.FileNames);
    }

    [Fact]
    public void A_manifest_is_recognised_under_either_name()
    {
        Assert.True(ModManifest.IsManifestFile(@"C:\mods\MyMod.dds2mod.json"));
        Assert.True(ModManifest.IsManifestFile(@"C:\mods\MyMod.ddsmod.json"));
        Assert.True(ModManifest.IsManifestFile(@"C:\mods\.dds2mod.json"));
        Assert.True(ModManifest.IsManifestFile(@"C:\mods\.ddsmod.json"));

        Assert.False(ModManifest.IsManifestFile(@"C:\mods\MyMod.json"));
        Assert.False(ModManifest.IsManifestFile(@"C:\mods\MyMod.pak"));
    }
}
