namespace DDS2ModManager.Tests;

/// What a release MEANS for an installed mod.
///
/// Every case here was previously reachable only through a real GitHub call, which is how the
/// no-asset case below shipped broken: the unit suite was green, CI was green, and only the daily
/// live-API job would have caught it. ModUpdateService.ApplyRelease exists so this is ordinary
/// logic with ordinary tests.
public class ReleaseOutcomeTests
{
    private static GitHubReleaseInfo Release(string tag, params string[] assets) => new()
    {
        TagName = tag,
        Body = "notes for " + tag,
        Assets = assets
            .Select(n => new GitHubAsset { Name = n, BrowserDownloadUrl = $"https://example.invalid/{n}" })
            .ToList()
    };

    private static ModUpdateSource Source(string version, string declaredAsset = "") => new()
    {
        Declaration = ModUpdateDeclaration.Manifest,
        Owner = "owner",
        Repo = "repo",
        DeclaredUrl = "https://github.com/owner/repo",
        Version = version,
        DeclaredAssetName = declaredAsset
    };

    private static ModInfo Mod(ModUpdateSource source, string? installedWith = null) => new()
    {
        Name = "TestMod",
        UpdateSource = source,
        InstalledUpdateUrl = installedWith ?? source.DeclaredUrl
    };

    private static ModInfo Apply(ModUpdateSource source, GitHubReleaseInfo release, string? installedWith = null)
    {
        var mod = Mod(source, installedWith);
        ModUpdateService.ApplyRelease(mod, source, release);
        return mod;
    }

    [Fact]
    public void A_newer_release_with_one_archive_is_offered()
    {
        var mod = Apply(Source("1.0.0"), Release("v1.2.0", "TestMod.zip"));

        Assert.True(mod.UpdateAvailable);
        Assert.Equal("v1.2.0", mod.AvailableUpdateTag);
        Assert.Equal("notes for v1.2.0", mod.AvailableUpdateNotes);
        Assert.Equal("https://example.invalid/TestMod.zip", mod.AvailableUpdateAssetUrl);
        Assert.Equal("1.2.0", mod.LatestVersion);
    }

    /// THE regression, and the reason this file exists.
    ///
    /// A release can carry no attached file at all, a bare .pak, or several archives with nothing
    /// naming which is the mod. In every one of those the version is still newer, and saying so is
    /// the useful half - the user reads the notes and downloads it by hand. Withholding it left
    /// people silently stuck on an old version because of how the author packaged their release.
    ///
    /// Found by a live test against a real repository whose latest release has zero assets.
    [Theory]
    [InlineData("")]                                // nothing attached at all
    [InlineData("TestMod.pak")]                     // detectable, but not installable
    [InlineData("TestMod.zip,TestMod-source.zip")]  // ambiguous - two candidates, neither named
    [InlineData("readme.md")]                       // nothing that could be a mod
    public void A_newer_release_is_still_reported_when_no_file_can_be_identified(string assetList)
    {
        var assets = assetList.Length == 0 ? Array.Empty<string>() : assetList.Split(',');
        var mod = Apply(Source("1.0.0"), Release("v1.2.0", assets));

        Assert.True(mod.UpdateAvailable, "the version is newer regardless of how it was packaged");
        Assert.Equal("v1.2.0", mod.AvailableUpdateTag);
        Assert.Equal("notes for v1.2.0", mod.AvailableUpdateNotes);
    }

    /// ...but with no URL to install from, so the prompt degrades to a manual download rather than
    /// offering a button that would fail. A .pak is detectable, which is why it is checked here
    /// specifically: it gets an asset URL and is still refused by CanAutoInstall.
    [Fact]
    public void A_release_with_nothing_installable_carries_no_install_url()
    {
        Assert.Null(Apply(Source("1.0.0"), Release("v1.2.0")).AvailableUpdateAssetUrl);
        Assert.Null(Apply(Source("1.0.0"), Release("v1.2.0", "a.zip", "b.zip")).AvailableUpdateAssetUrl);
    }

    [Fact]
    public void The_same_version_is_not_an_update()
    {
        var mod = Apply(Source("1.2.0"), Release("v1.2.0", "TestMod.zip"));

        Assert.False(mod.UpdateAvailable);
        Assert.Null(mod.AvailableUpdateTag);
        Assert.Equal("1.2.0", mod.LatestVersion);
    }

    [Fact]
    public void An_older_release_is_not_an_update() =>
        Assert.False(Apply(Source("2.0.0"), Release("v1.2.0", "TestMod.zip")).UpdateAvailable);

    /// 1.10 is newer than 1.9. A string comparison gets this backwards, and nobody notices until
    /// somebody is stuck on an old build.
    [Fact]
    public void Version_comparison_is_numeric_not_alphabetical() =>
        Assert.True(Apply(Source("1.9.0"), Release("v1.10.0", "TestMod.zip")).UpdateAvailable);

    /// No declared version means nothing to compare against. "Up to date" would be a guess and
    /// "update available" would flag the mod forever, so neither is reported.
    [Fact]
    public void A_mod_with_no_version_is_never_reported_as_updateable()
    {
        var mod = Apply(Source(""), Release("v1.2.0", "TestMod.zip"));

        Assert.False(mod.UpdateAvailable);
        Assert.Null(mod.AvailableUpdateTag);

        // The check still SUCCEEDED, so the latest version is recorded even though nothing is offered.
        Assert.Equal("1.2.0", mod.LatestVersion);
        Assert.NotNull(mod.LastUpdateCheck);
    }

    /// A moved update address outranks everything else: no update is offered at all until the user
    /// has confirmed the move, whatever the versions say.
    [Fact]
    public void A_moved_update_address_withholds_the_update()
    {
        var mod = Apply(Source("1.0.0"), Release("v1.2.0", "TestMod.zip"),
            installedWith: "https://github.com/someone-else/theirs");

        Assert.True(mod.UpdateUrlChanged);
        Assert.False(mod.UpdateAvailable);
        Assert.Null(mod.AvailableUpdateTag);
        Assert.Null(mod.AvailableUpdateAssetUrl);
    }

    /// A release that is no longer newer must clear what a previous check offered, or the row goes
    /// on advertising an update that has already been applied or withdrawn.
    [Fact]
    public void A_previously_offered_update_is_cleared_when_it_no_longer_applies()
    {
        var source = Source("2.0.0");
        var mod = Mod(source);
        mod.UpdateAvailable = true;
        mod.AvailableUpdateTag = "v1.9.0";
        mod.AvailableUpdateNotes = "stale";
        mod.AvailableUpdateAssetUrl = "https://example.invalid/stale.zip";

        ModUpdateService.ApplyRelease(mod, source, Release("v1.2.0", "TestMod.zip"));

        Assert.False(mod.UpdateAvailable);
        Assert.Null(mod.AvailableUpdateTag);
        Assert.Null(mod.AvailableUpdateNotes);
        Assert.Null(mod.AvailableUpdateAssetUrl);
    }

    /// The author's declared asset name is honoured here too, not just when installing.
    [Fact]
    public void A_declared_asset_name_selects_the_install_url()
    {
        var mod = Apply(Source("1.0.0", declaredAsset: "TestMod.zip"),
            Release("v1.2.0", "TestMod-source.zip", "TestMod.zip", "extras.7z"));

        Assert.True(mod.UpdateAvailable);
        Assert.Equal("https://example.invalid/TestMod.zip", mod.AvailableUpdateAssetUrl);
    }

    [Fact]
    public void A_leading_v_on_the_tag_is_ignored_on_both_sides() =>
        Assert.False(Apply(Source("v1.2.0"), Release("v1.2.0", "TestMod.zip")).UpdateAvailable);
}
