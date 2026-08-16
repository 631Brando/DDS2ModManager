using DDS2ModManager.Services;

namespace DDS2ModManager.Tests;

/// The experimental channel numbers a preview ABOVE the release it previews: "v1.1.0-exp.1" ships
/// as 1.1.0.1, and stable "v1.1.0" ships as 1.1.0. Plain numeric comparison therefore calls the
/// preview newer than the finished release, which is backwards - and getting it backwards offers
/// people older code as an update.
public class ChannelOrderingTests
{
    private static Version V(string tag) => AppUpdateService.ParseVersion(tag)!;

    // The one that matters. This is the case that shipped: stable v1.1.0 came out after
    // v1.1.0-exp.1 and contains strictly more work, but scores lower numerically.
    [Fact]
    public void Stable_release_supersedes_its_own_previews()
    {
        Assert.True(AppUpdateService.CompareBuilds(V("v1.1.0"), V("v1.1.0-exp.1")) > 0);
        Assert.True(AppUpdateService.CompareBuilds(V("v1.1.0"), V("v1.1.0-exp.9")) > 0);

        // ...and the numeric comparison it replaces gets it wrong, which is why this exists.
        Assert.True(V("v1.1.0-exp.1") > V("v1.1.0"));
    }

    [Fact]
    public void Later_previews_supersede_earlier_ones()
    {
        Assert.True(AppUpdateService.CompareBuilds(V("v1.1.0-exp.2"), V("v1.1.0-exp.1")) > 0);
        Assert.True(AppUpdateService.CompareBuilds(V("v1.1.0-exp.10"), V("v1.1.0-exp.9")) > 0);
    }

    // A preview still leads the release line it belongs to: v1.2.0-exp.1 is where you go after
    // v1.1.0, which is the entire point of the experimental channel.
    [Fact]
    public void Preview_of_a_later_version_beats_an_earlier_stable_release()
    {
        Assert.True(AppUpdateService.CompareBuilds(V("v1.2.0-exp.1"), V("v1.1.0")) > 0);
        Assert.True(AppUpdateService.CompareBuilds(V("v1.2.0"), V("v1.2.0-exp.1")) > 0);
    }

    [Theory]
    [InlineData("v1.1.0", "v1.1.0")]
    [InlineData("v1.1.0-exp.3", "v1.1.0-exp.3")]
    public void Equal_builds_compare_equal(string a, string b) =>
        Assert.Equal(0, AppUpdateService.CompareBuilds(V(a), V(b)));

    [Fact]
    public void Ordering_is_antisymmetric()
    {
        string[] tags = ["v1.0.6", "v1.1.0-exp.1", "v1.1.0", "v1.2.0-exp.1", "v1.2.0"];

        foreach (var a in tags)
        foreach (var b in tags)
        {
            var forward = AppUpdateService.CompareBuilds(V(a), V(b));
            var backward = AppUpdateService.CompareBuilds(V(b), V(a));
            Assert.Equal(Math.Sign(forward), -Math.Sign(backward));
        }
    }

    // Sorting the real release list has to put the current stable release on top, not the preview
    // that predates it.
    [Fact]
    public void Newest_of_the_real_release_list_is_the_stable_one()
    {
        string[] published = ["v1.0.5", "v1.0.6", "v1.1.0-exp.1", "v1.1.0"];

        var newest = published
            .OrderByDescending(V, Comparer<Version>.Create(AppUpdateService.CompareBuilds))
            .First();

        Assert.Equal("v1.1.0", newest);
    }

    [Fact]
    public void Experimental_is_behind_when_stable_has_caught_up()
    {
        var status = new AppUpdateService.ChannelStatus(
            LatestStable: Release("v1.1.0"),
            LatestExperimental: Release("v1.1.0-exp.1"));

        Assert.True(status.ExperimentalIsBehindStable);
    }

    [Fact]
    public void Experimental_is_not_behind_when_it_leads()
    {
        var status = new AppUpdateService.ChannelStatus(
            LatestStable: Release("v1.1.0"),
            LatestExperimental: Release("v1.2.0-exp.1"));

        Assert.False(status.ExperimentalIsBehindStable);
    }

    // "Behind" is a claim about two known releases. With either side missing we don't know, and
    // an unwarranted warning on the settings page is worse than no line at all.
    [Theory]
    [InlineData("v1.1.0", null)]
    [InlineData(null, "v1.1.0-exp.1")]
    [InlineData(null, null)]
    public void Unknown_channels_are_never_reported_as_behind(string? stable, string? experimental)
    {
        var status = new AppUpdateService.ChannelStatus(
            stable == null ? null : Release(stable),
            experimental == null ? null : Release(experimental));

        Assert.False(status.ExperimentalIsBehindStable);
    }

    // The version number and the code can move in opposite directions, and the three cases read
    // very differently to a user. Flattening them back into one "is this a downgrade" bool would
    // tell someone moving from a preview onto its finished release that their features are about
    // to disappear, which is the opposite of what happens.
    [Theory]
    // Ordinary update: bigger number, newer code.
    [InlineData("v1.2.0", "1.1.0.0", AppUpdateService.VersionChange.Update)]
    // Leaving a preview for the release it previewed: smaller number, newer code.
    [InlineData("v1.1.0", "1.1.0.1", AppUpdateService.VersionChange.SupersedingPreview)]
    // Stable is genuinely behind the line the user is on: smaller number, older code.
    [InlineData("v1.1.0", "1.2.0.1", AppUpdateService.VersionChange.Rollback)]
    public void Classifies_what_the_move_actually_does(string offered, string installed, AppUpdateService.VersionChange expected)
    {
        var candidate = V(offered);
        var current = Version.Parse(installed);

        var order = AppUpdateService.CompareBuilds(candidate, current);
        var actual = order < 0
            ? AppUpdateService.VersionChange.Rollback
            : candidate < current
                ? AppUpdateService.VersionChange.SupersedingPreview
                : AppUpdateService.VersionChange.Update;

        Assert.Equal(expected, actual);
    }

    private static GitHubReleaseInfo Release(string tag) => new() { TagName = tag };
}
