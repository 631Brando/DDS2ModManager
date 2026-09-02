namespace DDS2ModManager.Tests;

/// Reading a UE4SS build out of a release asset name, so a specific one can be installed by name.
///
/// The whole point: every build in the 3.0.1 line calls itself "v3.0.1 Beta". Hundreds of them.
/// The build number and commit in the filename are the only things that tell them apart, which is
/// why a user reporting a regression quotes those and not a version.
public class Ue4ssBuildTests
{
    private static UE4SSBuild? Parse(string name) => UE4SSBuild.FromAssetName(name, "https://x/" + name, 1024);

    // ---- the shapes actually published ----------------------------------------------------------

    [Fact]
    public void A_standard_build_parses()
    {
        var b = Parse("UE4SS_v3.0.1-1093-gba2efd55.zip");

        Assert.NotNull(b);
        Assert.Equal("3.0.1", b!.Version);
        Assert.Equal(1093, b.Build);
        Assert.Equal("gba2efd55", b.Sha);
        Assert.False(b.IsDevBuild);
    }

    /// The console build. The difference is invisible in the version, which is how someone can be
    /// moved off it by an update and see only that their logging stopped.
    [Fact]
    public void A_console_build_parses_and_is_marked()
    {
        var b = Parse("zDEV-UE4SS_v3.0.1-1111-g97b7e501.zip");

        Assert.NotNull(b);
        Assert.True(b!.IsDevBuild);
        Assert.Equal(1111, b.Build);
        Assert.Contains("console", b.Display);
    }

    /// Older shapes from an era with a different archive layout. The installer expects dwmapi.dll
    /// beside a ue4ss\ folder, so offering these would fail AFTER the download rather than before.
    [Theory]
    [InlineData("UE4SS_Standard_v2.5.2-178-g73fcad4.zip")]
    [InlineData("UE4SS_Xinput_v2.5.2-178-g73fcad4.zip")]
    [InlineData("UE4SS-2.XDev-windows.zip")]
    [InlineData("zCustomGameConfigs.zip")]
    [InlineData("Source code (zip)")]
    [InlineData("UE4SS_v3.0.1-1093-gba2efd55.7z")]
    public void Shapes_this_installer_cannot_handle_are_not_offered(string name) =>
        Assert.Null(Parse(name));

    // ---- ordering, which is the part that goes wrong silently -------------------------------------

    /// Sorted as text, "998" lands above "1111" and the newest build is not at the top of a list
    /// somebody is picking from. It has to be compared as a number.
    [Fact]
    public void Builds_order_by_number_not_by_text()
    {
        var older = Parse("UE4SS_v3.0.1-998-g32d8a381.zip")!;
        var newer = Parse("UE4SS_v3.0.1-1111-g97b7e501.zip")!;

        Assert.True(UE4SSBuild.Newest(newer, older) < 0, "1111 must sort above 998");
        Assert.True(string.CompareOrdinal("998", "1111") > 0, "text sort really does get this wrong");
    }

    [Fact]
    public void A_newer_version_outranks_a_higher_build_number_on_an_older_one()
    {
        var newVersion = Parse("UE4SS_v3.1.0-2-gaaaaaaa.zip")!;
        var oldVersionHighBuild = Parse("UE4SS_v3.0.1-1111-g97b7e501.zip")!;

        Assert.True(UE4SSBuild.Newest(newVersion, oldVersionHighBuild) < 0);
    }

    [Fact]
    public void Sorting_a_mixed_list_puts_the_newest_first()
    {
        var list = new[]
        {
            "UE4SS_v3.0.1-998-g32d8a381.zip",
            "UE4SS_v3.0.1-1111-g97b7e501.zip",
            "UE4SS_v2.5.2-500-gbbbbbbb.zip",
            "UE4SS_v3.0.1-1093-gba2efd55.zip",
        }.Select(n => Parse(n)!).ToList();

        list.Sort(UE4SSBuild.Newest);

        Assert.Equal([1111, 1093, 998, 500], list.Select(b => b.Build));
    }

    // ---- what the user reads ----------------------------------------------------------------------

    /// The two things a bug report asks for have to be on screen, because the version is useless.
    [Fact]
    public void The_display_carries_the_build_and_the_commit()
    {
        var b = Parse("UE4SS_v3.0.1-1093-gba2efd55.zip")!;

        Assert.Contains("1093", b.Display);
        Assert.Contains("gba2efd55", b.Display);
        Assert.Contains("3.0.1", b.Display);
    }

    [Fact]
    public void Case_in_the_asset_name_does_not_matter()
    {
        Assert.NotNull(Parse("ZDEV-UE4SS_V3.0.1-1093-GBA2EFD55.ZIP"));
    }
}
