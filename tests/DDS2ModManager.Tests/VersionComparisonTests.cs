namespace DDS2ModManager.Tests;

/// Version strings are author-authored free text, so this is all judgement calls - and one of
/// them is the difference between a working updater and one that strands people on old builds.
public class VersionComparisonTests
{
    // The one that matters. A string comparison gets this backwards, and nobody notices until
    // a user is stuck on 1.9 forever because "1.9" > "1.10" alphabetically.
    [Fact]
    public void Compares_numerically_not_alphabetically()
    {
        Assert.True(ModUpdateService.IsNewer("1.10.0", "1.9.0"));
        Assert.False(ModUpdateService.IsNewer("1.9.0", "1.10.0"));
    }

    [Theory]
    [InlineData("2.0.0", "1.9.9", true)]
    [InlineData("1.2.3", "1.2.3", false)]
    [InlineData("1.0.0", "1.2.0", false)]
    [InlineData("2", "1", true)]                    // bare major, padded to 2.0
    [InlineData("1.3.0-beta", "1.2.0", true)]       // compares on the numeric run
    public void Ordering(string latest, string installed, bool expected) =>
        Assert.Equal(expected, ModUpdateService.IsNewer(latest, installed));

    // When neither side parses, "different" is the best available answer: the tag came from
    // GitHub's own latest release, so different really does mean something was published. The
    // cost of being wrong is a prompt the user can decline, not a silent install.
    [Fact]
    public void Unparseable_falls_back_to_difference()
    {
        Assert.True(ModUpdateService.IsNewer("final-FINAL", "final"));
        Assert.False(ModUpdateService.IsNewer("beta", "beta"));
    }

    // An empty side means "we do not know", which must never read as "there is an update".
    [Theory]
    [InlineData("", "1.0")]
    [InlineData("1.0", "")]
    public void Unknown_is_never_newer(string latest, string installed) =>
        Assert.False(ModUpdateService.IsNewer(latest, installed));

    [Theory]
    [InlineData("v1.2.0", "1.2.0")]
    [InlineData("V2.0", "2.0")]
    [InlineData("1.2.0", "1.2.0")]
    [InlineData("version-two", "version-two")]      // a word starting with v is left alone
    public void Normalizes_tag_prefixes(string raw, string expected) =>
        Assert.Equal(expected, ModUpdateService.NormalizeVersion(raw));
}
