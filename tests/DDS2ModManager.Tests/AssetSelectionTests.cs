namespace DDS2ModManager.Tests;

/// Which file in a GitHub release IS the mod.
///
/// This was implemented three times - in the update check, in the install path, and in the mod
/// catalog - and the copies had drifted. An author who named a specific file with the "asset"
/// field got the update DETECTED via that file and INSTALLED from whichever archive happened to
/// sort first. These tests exist to keep one rule.
public class AssetSelectionTests
{
    private static GitHubReleaseInfo Release(params string[] assetNames) => new()
    {
        TagName = "v1.2.0",
        Assets = assetNames
            .Select(n => new GitHubAsset { Name = n, BrowserDownloadUrl = $"https://example.invalid/{n}" })
            .ToList()
    };

    private static ModUpdateSource Source(string declaredAsset = "") => new()
    {
        Declaration = ModUpdateDeclaration.Manifest,
        Owner = "owner",
        Repo = "repo",
        DeclaredUrl = "https://github.com/owner/repo",
        Version = "1.0.0",
        DeclaredAssetName = declaredAsset
    };

    [Fact]
    public void One_archive_is_picked_without_being_named()
    {
        var picked = ModUpdateService.PickAsset(Release("MyMod.zip"), Source());

        Assert.Equal("MyMod.zip", picked?.Name);
    }

    /// Two candidates and no instruction means the author has not said which is the mod. Guessing
    /// installs the wrong one, which is worse than doing nothing.
    [Fact]
    public void Two_archives_with_no_declared_name_is_refused()
    {
        Assert.Null(ModUpdateService.PickAsset(Release("MyMod.zip", "MyMod-source.zip"), Source()));
    }

    /// THE regression. The declared name has to win, and it has to win on the install path too -
    /// not just when spotting the update.
    [Fact]
    public void A_declared_name_wins_over_the_other_archives()
    {
        var picked = ModUpdateService.PickAsset(
            Release("MyMod-source.zip", "MyMod.zip", "extras.7z"), Source("MyMod.zip"));

        Assert.Equal("MyMod.zip", picked?.Name);
    }

    [Fact]
    public void A_declared_name_is_matched_ignoring_case()
    {
        var picked = ModUpdateService.PickAsset(Release("MyMod.zip", "other.zip"), Source("mymod.ZIP"));

        Assert.Equal("MyMod.zip", picked?.Name);
    }

    /// No fallback when the declared name matches nothing. The author named a file; quietly
    /// installing a different one is precisely the failure naming it was meant to prevent.
    /// Documented in MODDING.md, because the symptom (updates silently stop) is hard to diagnose.
    [Fact]
    public void A_declared_name_that_matches_nothing_does_not_fall_back()
    {
        Assert.Null(ModUpdateService.PickAsset(Release("MyMod-1.3.0.zip"), Source("MyMod.zip")));
    }

    [Fact]
    public void Non_archive_files_are_not_candidates()
    {
        Assert.Null(ModUpdateService.PickAsset(Release("readme.md", "source.tar.gz", "mod.dll"), Source()));
    }

    // ---- detected vs installable ------------------------------------------------------------

    /// A bare .pak is a real release of a real mod, so it is worth telling the user about even
    /// though the installer cannot unpack one. Detection is deliberately wider than install.
    [Fact]
    public void A_bare_pak_is_detected_as_the_release()
    {
        Assert.Equal("MyMod.pak", ModUpdateService.PickAsset(Release("MyMod.pak"), Source())?.Name);
    }

    /// ...but must not be handed to the installer. ModInstallerService.PrepareInstall THROWS on
    /// anything that is not a folder or a zip/7z/rar, so the prompt has to know the difference.
    [Fact]
    public void A_bare_pak_is_not_installable()
    {
        var pak = ModUpdateService.PickAsset(Release("MyMod.pak"), Source())!;

        Assert.False(ModUpdateService.CanAutoInstall(pak));
    }

    [Theory]
    [InlineData("MyMod.zip")]
    [InlineData("MyMod.7z")]
    [InlineData("MyMod.rar")]
    [InlineData("MyMod.ZIP")]
    public void Archives_the_installer_understands_are_installable(string name)
    {
        Assert.True(ModUpdateService.CanAutoInstall(new GitHubAsset { Name = name }));
    }

    /// The list of installable types must stay tied to what the extractor actually supports.
    /// These drifting apart is what produced a release that was offered and then refused.
    [Fact]
    public void Installable_types_match_what_the_extractor_supports()
    {
        foreach (var ext in ArchiveExtractionService.SupportedExtensions)
            Assert.True(ModUpdateService.CanAutoInstall(new GitHubAsset { Name = "Mod" + ext }), ext);
    }
}
