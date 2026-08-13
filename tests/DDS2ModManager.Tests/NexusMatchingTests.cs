namespace DDS2ModManager.Tests;

/// Matching an installed mod to its Nexus entry.
///
/// Every case here comes from measuring the real 19-mod install against the real 99-mod DDS2
/// catalogue. The hazards are not hypothetical - they are the specific wrong matches that a
/// looser rule was observed to produce.
public class NexusMatchingTests
{
    private static NexusModPost Mod(int id, string name) =>
        new() { ModId = id, Name = name, GameDomain = "drugdealersimulator2" };

    private static Dictionary<string, NexusModPost> Index(params NexusModPost[] mods) =>
        NexusModMatcher.BuildIndex(mods);

    // ---- the matches that must work ----------------------------------------------------------

    /// Real pairs from the live catalogue. The installed name is the folder/registry name, the
    /// Nexus name is the published title.
    [Theory]
    [InlineData("DriveableScooter", "Driveable Scooter")]
    [InlineData("DriveableScooter (LogicMod)", "Driveable Scooter")]
    [InlineData("BiggerPackages_P", "Bigger Packages")]
    [InlineData("EthanolExtraction_Lua", "Ethanol Extraction - Brew Ethanol from Alcohol")]
    [InlineData("SpecialClientMarker", "Special Client Marker - Never Misassign a Special Client")]
    [InlineData("LargeBolivars_P", "Large Bolivars - Get Paid in Big Bills")]
    [InlineData("BrandosDDS2Helper_P", "Brando's DDS2 Helper - GUI  - Chat Commands")]
    public void Real_installed_names_match_their_nexus_entry(string installed, string nexusName)
    {
        var index = Index(Mod(42, nexusName));

        Assert.Equal(42, NexusModMatcher.Match(installed, index)?.ModId);
    }

    /// Both halves of a two-part mod resolving to the same entry is correct, not a collision.
    [Fact]
    public void Both_halves_of_a_two_part_mod_resolve_to_the_same_entry()
    {
        var index = Index(Mod(113, "Ethanol Extraction - Brew Ethanol from Alcohol"));

        Assert.Equal(113, NexusModMatcher.Match("EthanolExtraction", index)?.ModId);
        Assert.Equal(113, NexusModMatcher.Match("EthanolExtraction_Lua", index)?.ModId);
    }

    /// Punctuation is what separates a folder name from a published title.
    [Fact]
    public void Punctuation_and_case_are_ignored() =>
        Assert.Equal("brandosdds2helper", NexusModMatcher.Normalise("Brando's DDS2 Helper"));

    // ---- the refusals that matter more ------------------------------------------------------

    /// THE hazard. Four separate mods in the live catalogue share the head "Gh0sted - ...", so the
    /// head key maps to four different mod ids. Picking one would be a one-in-four guess shown to
    /// the user as fact.
    [Fact]
    public void An_ambiguous_head_key_matches_nothing_rather_than_guessing()
    {
        var index = Index(
            Mod(77, "Gh0sted - Small Island Vendor Inventory Adjustment"),
            Mod(78, "Gh0sted - 2x Pricing"),
            Mod(79, "Gh0sted - Rebalance"),
            Mod(84, "Gh0sted - Money Stacks"));

        Assert.Null(NexusModMatcher.Match("Gh0sted", index));
    }

    /// The unique full-name key still works even when the head is ambiguous, so the guard costs
    /// nothing for a mod whose whole title is written out.
    [Fact]
    public void An_ambiguous_head_does_not_break_the_full_name_key()
    {
        var index = Index(Mod(78, "Gh0sted - 2x Pricing"), Mod(79, "Gh0sted - Rebalance"));

        Assert.Equal(79, NexusModMatcher.Match("Gh0sted - Rebalance", index)?.ModId);
    }

    /// "DDS2 - Mod Compilation WIP" yields the head key "dds2", which would otherwise swallow any
    /// installed mod whose name normalises to that.
    [Fact]
    public void Short_generic_keys_are_refused()
    {
        var index = Index(Mod(96, "DDS2 - Mod Compilation WIP"));

        Assert.Null(NexusModMatcher.Match("DDS2", index));
    }

    /// A bare hyphen is part of the name, not a separator. Splitting on it would wreck both of
    /// these, which are real published titles.
    [Theory]
    [InlineData("Rembows-Infinity-Durability-x-Tools")]
    [InlineData("DDS2 Reshade -DealersHigh-")]
    public void A_bare_hyphen_does_not_split_the_title(string nexusName)
    {
        var index = Index(Mod(90, nexusName));

        Assert.Equal(90, NexusModMatcher.Match(nexusName, index)?.ModId);
    }

    /// The measured false positives fuzzy matching produced. Each shares one word with its victim
    /// and nothing else - different mod, and in two cases a different author entirely.
    [Theory]
    [InlineData("BotanistExpansion_P", 28, "Brando's Cartel Expansion - WIP Cartel Questline - More Drugs")]
    [InlineData("BotanistExpansion_Lua", 92, "Rembows Great Stock Expansion")]
    [InlineData("MifTools", 62, "DDS2 Tools")]
    public void Mods_that_are_not_published_match_nothing(string installed, int decoyId, string decoyName)
    {
        var index = Index(Mod(decoyId, decoyName));

        Assert.Null(NexusModMatcher.Match(installed, index));
    }

    /// The "Bigger X" namespace is crowded. Exact matching has to pick the right one and refuse
    /// the neighbours.
    [Fact]
    public void A_crowded_namespace_still_resolves_exactly()
    {
        var index = Index(
            Mod(110, "Bigger Packages"),
            Mod(60, "Bigger Backpacks and More"),
            Mod(56, "Bigger Substance Storage"));

        Assert.Equal(110, NexusModMatcher.Match("BiggerPackages_P", index)?.ModId);
        Assert.Null(NexusModMatcher.Match("BiggerThings", index));
    }

    [Fact]
    public void An_empty_catalogue_matches_nothing() =>
        Assert.Null(NexusModMatcher.Match("DriveableScooter", Index()));

    // ---- suffix stripping --------------------------------------------------------------------

    [Theory]
    [InlineData("DriveableScooter (LogicMod)", "driveablescooter")]
    [InlineData("DriveableScooter (LuaMod)", "driveablescooter")]
    [InlineData("BiggerPackages_P", "biggerpackages")]
    [InlineData("EthanolExtraction_Lua", "ethanolextraction")]
    [InlineData("PlainName", "plainname")]
    public void Packaging_suffixes_are_stripped(string installed, string expected) =>
        Assert.Equal(expected, NexusModMatcher.KeyForInstalled(installed));

    /// A mod legitimately ending in "p" must not lose it - only the "_P" suffix goes.
    [Fact]
    public void A_trailing_letter_is_not_mistaken_for_a_suffix() =>
        Assert.Equal("mifsopo", NexusModMatcher.KeyForInstalled("MifsopO"));
}
