namespace DDS2ModManager.Tests;

/// Deciding which rows are two halves of one mod.
///
/// This governs what gets enabled, disabled and — with a prompt — uninstalled, so an over-eager
/// rule deletes files the user didn't point at. The grouping key is deliberately the same one
/// NexusModMatcher uses, so "same mod" means one thing in this app rather than two.
///
/// These test the KEY rule directly. The ViewModel's GroupOf() adds the "must be different types"
/// constraint on top, which is asserted through the key pairs below plus the collision case.
public class ModGroupingTests
{
    private static string Key(string name) => NexusModMatcher.KeyForInstalled(name);

    /// The real pairs on a live install: a lua half under ue4ss\Mods and a pak half under
    /// Content\Paks\LogicMods, named by the manager's own packaging convention.
    [Theory]
    [InlineData("EthanolExtraction", "EthanolExtraction_Lua")]
    [InlineData("SpecialClientMarker", "SpecialClientMarker (LogicMod)")]
    [InlineData("DriveableScooter", "DriveableScooter (LogicMod)")]
    [InlineData("BotanistExpansion_P", "BotanistExpansion_Lua")]
    public void Two_halves_of_one_mod_share_a_key(string a, string b) =>
        Assert.Equal(Key(a), Key(b));

    /// Different mods must not collapse together, however similar the names look.
    [Theory]
    [InlineData("MifCore", "MifCentrifuge_P")]
    [InlineData("BotanistExpansion_P", "BrandosCartelExpansion_P")]
    [InlineData("BiggerPackages_P", "LargeBolivars_P")]
    [InlineData("MifQuestKit", "MifTools")]
    public void Different_mods_do_not_share_a_key(string a, string b) =>
        Assert.NotEqual(Key(a), Key(b));

    /// A mod that installs to one place only is a group of itself, and must not be dragged into
    /// someone else's group by a shared word.
    [Fact]
    public void A_standalone_mod_has_a_key_of_its_own()
    {
        var keys = new[] { "CartelDemandFlags", "MifEconLogger", "MifMenuProbe" }.Select(Key).ToList();

        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    /// The grouping key ignores the packaging suffix, which is the whole point - it is how the
    /// manager names the halves, not something the author chose.
    [Theory]
    [InlineData("MyMod_P")]
    [InlineData("MyMod_Lua")]
    [InlineData("MyMod (LogicMod)")]
    [InlineData("MyMod (LuaMod)")]
    [InlineData("MyMod")]
    public void Every_packaging_suffix_reduces_to_the_same_key(string name) =>
        Assert.Equal("mymod", Key(name));

    /// Case and punctuation differences between the two halves must not split a group.
    [Fact]
    public void Case_and_punctuation_do_not_split_a_group() =>
        Assert.Equal(Key("Brando's DDS2 Helper_P"), Key("brandos-dds2-helper"));

    /// An empty or punctuation-only name yields an empty key. GroupOf treats that as "stands
    /// alone", so a nameless row can never hoover up every other nameless row.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("---")]
    public void A_nameless_row_produces_no_key(string name) =>
        Assert.Equal("", Key(name));
}
