namespace DDS2ModManager.Tests;

/// The Nexus page the user declares, when name matching can never reach it.
///
/// The worked example throughout is AERR: installed as "AERR", published as "AE Revolutions
/// Reloaded" (mod 79 on drugdealersimulator). `Normalise("AERR")` is `"aerr"` — four characters,
/// below MinimumKeyLength — and even without that gate the key is absent from the catalogue
/// entirely, because the author's acronym appears nowhere in their own title.
public class NexusLinkTests
{
    private static NexusModPost Post(int id, string name, string domain = "drugdealersimulator") =>
        new() { ModId = id, Name = name, GameDomain = domain };

    private static NexusModLink Link(int id, string domain = "drugdealersimulator") =>
        new() { ModId = id, GameDomain = domain, Kind = NexusLinkKind.Linked };

    // ---- the stored record ---------------------------------------------------------------------

    [Fact]
    public void A_link_with_an_id_and_a_domain_is_usable_and_composes_its_url()
    {
        var link = Link(79);

        Assert.True(link.IsUsable);
        Assert.Equal("https://www.nexusmods.com/drugdealersimulator/mods/79", link.Url);
    }

    // A hand-edited registry can hold Kind "Linked" with no id. IsUsable is the only gate anything
    // may read, so a raw null-check elsewhere cannot reintroduce that state.
    [Theory]
    [InlineData(0, "drugdealersimulator")]   // no id
    [InlineData(79, "")]                     // no domain
    public void An_incomplete_link_is_not_usable(int modId, string domain)
    {
        Assert.False(new NexusModLink { ModId = modId, GameDomain = domain }.IsUsable);
    }

    // "This mod has no Nexus page" is a real answer, not a broken link.
    [Fact]
    public void A_no_page_declaration_is_not_usable_even_with_an_id()
    {
        Assert.False(new NexusModLink
        {
            ModId = 79, GameDomain = "drugdealersimulator", Kind = NexusLinkKind.NoPage
        }.IsUsable);
    }

    // A hand-written record with no Kind must mean "linked", never a suppression nobody asked for.
    [Fact]
    public void The_default_kind_is_linked()
    {
        Assert.Equal(NexusLinkKind.Linked, default(NexusLinkKind));
        Assert.Equal(NexusLinkKind.Linked, new NexusModLink().Kind);
    }

    // ---- precedence ------------------------------------------------------------------------------

    // The whole point: the acronym reaches nothing, the declared id reaches the real page.
    [Fact]
    public void A_declared_id_reaches_a_page_the_name_never_could()
    {
        var catalogue = new[] { Post(79, "AE Revolutions Reloaded") };
        var index = NexusModMatcher.BuildIndex(catalogue);

        Assert.Null(NexusModMatcher.Match("AERR", index));

        var hit = NexusModMatcher.Resolve("AERR", Link(79), catalogue, index, "drugdealersimulator");

        Assert.NotNull(hit);
        Assert.Equal("AE Revolutions Reloaded", hit!.Name);
    }

    // A user links a mod BECAUSE the name match was absent or wrong. Falling through to the match
    // would restore the exact thing being corrected.
    [Fact]
    public void A_declared_id_wins_over_a_name_that_would_have_matched_something_else()
    {
        var catalogue = new[] { Post(10, "Bigger Packages"), Post(79, "AE Revolutions Reloaded") };
        var index = NexusModMatcher.BuildIndex(catalogue);

        var hit = NexusModMatcher.Resolve("Bigger Packages", Link(79), catalogue, index, "drugdealersimulator");

        Assert.Equal(79, hit!.ModId);
    }

    [Fact]
    public void No_link_behaves_exactly_like_name_matching()
    {
        var catalogue = new[] { Post(10, "Bigger Packages") };
        var index = NexusModMatcher.BuildIndex(catalogue);

        var resolved = NexusModMatcher.Resolve("Bigger Packages", null, catalogue, index, "drugdealersimulator");

        Assert.Equal(NexusModMatcher.Match("Bigger Packages", index)!.ModId, resolved!.ModId);
    }

    [Fact]
    public void A_no_page_declaration_suppresses_a_name_that_would_have_matched()
    {
        var catalogue = new[] { Post(10, "Bigger Packages") };
        var index = NexusModMatcher.BuildIndex(catalogue);
        var none = new NexusModLink { Kind = NexusLinkKind.NoPage, GameDomain = "drugdealersimulator" };

        Assert.Null(NexusModMatcher.Resolve("Bigger Packages", none, catalogue, index, "drugdealersimulator"));
    }

    // ---- the cross-game trap ---------------------------------------------------------------------

    // Mod 79 is "AE Revolutions Reloaded" on DDS1 and "Gh0sted - Rebalance" on DDS2; 85 ids collide
    // across the two live catalogues and not one shares a title. Resolving a foreign link against
    // whatever that number happens to mean here is how a stranger's picture lands on your own mod.
    [Fact]
    public void A_link_for_another_game_resolves_to_nothing_not_to_that_number_here()
    {
        var dds2 = new[] { Post(79, "Gh0sted - Rebalance", "drugdealersimulator2") };
        var index = NexusModMatcher.BuildIndex(dds2);

        var hit = NexusModMatcher.Resolve("AERR", Link(79, "drugdealersimulator"), dds2, index, "drugdealersimulator2");

        Assert.Null(hit);
    }

    // Not even when the name WOULD have matched. The link is the user's answer; a wrong-game link
    // is still their answer and still not this game's.
    [Fact]
    public void A_foreign_link_does_not_fall_back_to_name_matching()
    {
        var dds2 = new[] { Post(10, "Bigger Packages", "drugdealersimulator2") };
        var index = NexusModMatcher.BuildIndex(dds2);

        var hit = NexusModMatcher.Resolve(
            "Bigger Packages", Link(999, "drugdealersimulator"), dds2, index, "drugdealersimulator2");

        Assert.Null(hit);
    }

    // Normal for a mod published inside the three-day catalogue window. The link still opens; the
    // card fills in later. It must not quietly become a name match.
    [Fact]
    public void A_link_whose_id_is_not_in_the_catalogue_resolves_to_nothing()
    {
        var catalogue = new[] { Post(10, "Bigger Packages") };
        var index = NexusModMatcher.BuildIndex(catalogue);

        Assert.Null(NexusModMatcher.Resolve("Bigger Packages", Link(214), catalogue, index, "drugdealersimulator"));
    }

    // ---- the two gates ---------------------------------------------------------------------------

    // A link carries a domain and an id, which is everything a URL needs and nothing a card needs.
    // Collapsing these into one gate is what would force a fabricated post - a blank title above
    // "0 downloads", asserted about the user's own mod.
    [Fact]
    public void A_link_gives_a_page_without_giving_a_card()
    {
        var mod = new ModInfo { Name = "AERR", NexusLink = Link(79) };

        Assert.True(mod.HasNexusPage);
        Assert.False(mod.HasNexusInfo);
        Assert.Equal("https://www.nexusmods.com/drugdealersimulator/mods/79", mod.NexusPageUrl);
    }

    // Offered for an unmatched mod, and for one already linked so a typo is one click from fixed;
    // not for a mod matching cleanly, where re-linking is an invitation to break something working.
    [Fact]
    public void The_link_button_shows_only_where_it_is_useful()
    {
        Assert.True(new ModInfo { Name = "AERR" }.CanEditNexusLink);
        Assert.True(new ModInfo { Name = "AERR", NexusLink = Link(79) }.CanEditNexusLink);

        var matched = new ModInfo { Name = "Bigger Packages", NexusInfo = Post(10, "Bigger Packages") };
        Assert.False(matched.CanEditNexusLink);
    }

    // ---- the picture belongs to the post, not the row ---------------------------------------------

    // Row_ToolTipOpening short-circuits on a non-null thumbnail, so without this, re-pointing a link
    // shows the PREVIOUS mod's picture above the new mod's title - the stranger's-picture failure
    // arriving through the very path that exists to correct it.
    [Fact]
    public void Re_pointing_a_mod_at_a_different_page_drops_the_old_picture()
    {
        var mod = new ModInfo { Name = "AERR", NexusInfo = Post(79, "AE Revolutions Reloaded") };
        mod.NexusThumbnail = new System.Windows.Media.Imaging.BitmapImage();

        mod.NexusInfo = Post(112, "Large Bolivars");

        Assert.Null(mod.NexusThumbnail);
    }

    [Fact]
    public void Re_assigning_the_same_page_keeps_the_picture()
    {
        var mod = new ModInfo { Name = "AERR", NexusInfo = Post(79, "AE Revolutions Reloaded") };
        mod.NexusThumbnail = new System.Windows.Media.Imaging.BitmapImage();

        mod.NexusInfo = Post(79, "AE Revolutions Reloaded");

        Assert.NotNull(mod.NexusThumbnail);
    }

    // Same id, different game - two unrelated mods. Keeping the picture would show DDS2's artwork
    // on a DDS1 mod, which is why NexusImageCache keys on both.
    [Fact]
    public void The_same_id_on_a_different_domain_drops_the_picture()
    {
        var mod = new ModInfo { Name = "AERR", NexusInfo = Post(79, "AE Revolutions Reloaded") };
        mod.NexusThumbnail = new System.Windows.Media.Imaging.BitmapImage();

        mod.NexusInfo = Post(79, "Gh0sted - Rebalance", "drugdealersimulator2");

        Assert.Null(mod.NexusThumbnail);
    }
}
