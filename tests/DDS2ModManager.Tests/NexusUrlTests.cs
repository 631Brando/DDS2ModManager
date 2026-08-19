namespace DDS2ModManager.Tests;

/// Reading a Nexus mod-page address, or a bare mod number, into (domain, id).
///
/// The refusals matter more than the successes. What comes out of here is stored as the user's own
/// declaration and then shown as their mod's identity — so a URL that is *nearly* a mod page must
/// be refused rather than read optimistically into a confident pair.
public class NexusUrlTests
{
    private const string Dds1 = "drugdealersimulator";
    private const string Dds2 = "drugdealersimulator2";

    private static (string Domain, int ModId)? Parse(string? input, string active = Dds1) =>
        NexusUrlParser.TryParse(input, active, out var d, out var id) ? (d, id) : null;

    // ---- the shapes people actually paste --------------------------------------------------------

    [Theory]
    [InlineData("https://www.nexusmods.com/drugdealersimulator/mods/79")]
    [InlineData("https://nexusmods.com/drugdealersimulator/mods/79")]
    [InlineData("http://www.nexusmods.com/drugdealersimulator/mods/79")]
    [InlineData("nexusmods.com/drugdealersimulator/mods/79")]
    [InlineData("www.nexusmods.com/drugdealersimulator/mods/79")]
    [InlineData("https://www.nexusmods.com/drugdealersimulator/mods/79/")]
    [InlineData("https://www.nexusmods.com/drugdealersimulator/mods/79?tab=files")]
    [InlineData("https://www.nexusmods.com/drugdealersimulator/mods/79#description")]
    [InlineData("https://www.nexusmods.com/drugdealersimulator/mods/79/files")]
    [InlineData("  https://www.nexusmods.com/drugdealersimulator/mods/79  ")]
    public void The_classic_layout_parses(string url) =>
        Assert.Equal((Dds1, 79), Parse(url));

    // Nexus has served both. Skipping a leading "games" segment covers the newer one.
    [Theory]
    [InlineData("https://www.nexusmods.com/games/drugdealersimulator/mods/79")]
    [InlineData("https://www.nexusmods.com/games/drugdealersimulator/mods/79?tab=posts")]
    public void The_games_prefixed_layout_parses(string url) =>
        Assert.Equal((Dds1, 79), Parse(url));

    // A bare number can only mean the game currently open - it was typed against a mod installed
    // under it. This is the shortest route for a user who can already see the id.
    [Fact]
    public void A_bare_number_takes_the_active_game()
    {
        Assert.Equal((Dds1, 79), Parse("79"));
        Assert.Equal((Dds2, 79), Parse("79", Dds2));
    }

    // ---- host spoofing ----------------------------------------------------------------------------

    // Uri.Host excludes any userinfo before '@', which is what makes the allowlist comparison safe.
    [Theory]
    [InlineData("https://nexusmods.com@evil.example.com/drugdealersimulator/mods/79")]
    [InlineData("https://nexusmods.com.evil.example.com/drugdealersimulator/mods/79")]
    [InlineData("https://evil.example.com/drugdealersimulator/mods/79")]
    [InlineData("https://notnexusmods.com/drugdealersimulator/mods/79")]
    public void Only_nexusmods_com_is_accepted(string url) => Assert.Null(Parse(url));

    // ---- shapes that are not a mod page -----------------------------------------------------------

    // Without the structural rule this reads as a confident pair naming a game called "profile" -
    // the same failure GitHubUrlParser's reserved-route list exists to prevent.
    [Theory]
    [InlineData("https://www.nexusmods.com/profile/Someone/mods")]
    [InlineData("https://www.nexusmods.com/drugdealersimulator")]
    [InlineData("https://www.nexusmods.com/games/drugdealersimulator")]
    [InlineData("https://www.nexusmods.com/drugdealersimulator/users/12345")]
    [InlineData("https://www.nexusmods.com/")]
    public void A_url_that_is_not_a_mod_page_is_refused(string url) => Assert.Null(Parse(url));

    // int.TryParse silently accepts all of these. The id becomes the user's stored identity for
    // their mod, so "/mods/79." must be refused outright rather than read as 79.
    [Theory]
    [InlineData("https://www.nexusmods.com/drugdealersimulator/mods/79.")]
    [InlineData("https://www.nexusmods.com/drugdealersimulator/mods/+79")]
    [InlineData("https://www.nexusmods.com/drugdealersimulator/mods/-79")]
    [InlineData("https://www.nexusmods.com/drugdealersimulator/mods/7 9")]
    [InlineData("https://www.nexusmods.com/drugdealersimulator/mods/seventynine")]
    [InlineData("https://www.nexusmods.com/drugdealersimulator/mods/0")]
    public void A_sloppy_id_is_refused_rather_than_read_optimistically(string url) =>
        Assert.Null(Parse(url));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("not a url at all")]
    [InlineData("0")]
    [InlineData("-5")]
    public void Nonsense_is_refused(string? input) => Assert.Null(Parse(input));

    // ---- the parser reports, the caller refuses ----------------------------------------------------

    // Deliberately NOT compared against the active domain here. The dialog does that, so its
    // refusal can name both games - "that address is for DDS1, this mod is installed under DDS2".
    [Fact]
    public void A_foreign_domain_is_reported_faithfully_not_rewritten()
    {
        Assert.Equal((Dds2, 79), Parse("https://www.nexusmods.com/drugdealersimulator2/mods/79", active: Dds1));
        Assert.Equal(("skyrimspecialedition", 1234),
            Parse("https://www.nexusmods.com/skyrimspecialedition/mods/1234", active: Dds1));
    }

    // Whatever scheme was pasted, only (domain, id) survives - the address later opened is
    // recomposed as https by NexusModPost.UrlFor, so accepting http is not a silent downgrade.
    [Fact]
    public void An_http_address_yields_an_https_page()
    {
        var parsed = Parse("http://www.nexusmods.com/drugdealersimulator/mods/79");

        Assert.NotNull(parsed);
        Assert.Equal("https://www.nexusmods.com/drugdealersimulator/mods/79",
            NexusModPost.UrlFor(parsed!.Value.Domain, parsed.Value.ModId));
    }
}
