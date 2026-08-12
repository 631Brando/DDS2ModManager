namespace DDS2ModManager.Tests.Integration;

/// Tests that talk to the real GitHub and Nexus APIs.
///
/// Traited so CI can exclude them: a build must not go red because GitHub rate-limited the
/// runner or Nexus had a bad afternoon. Run them deliberately with
///
///     dotnet test --filter Category=Live
///
/// They earn their keep by catching the thing unit tests structurally cannot - an API changing
/// shape underneath us. The Nexus one in particular asserts the property the whole banner rests
/// on: that the query works with NO credentials.
[Trait("Category", "Live")]
public class LiveApiTests
{
    [Fact]
    public async Task GitHub_latest_release_is_readable_without_a_token()
    {
        var svc = new ModUpdateService();
        var mod = new ModInfo
        {
            Name = "MifBridge",
            ModUpdateUrl = "https://github.com/mifsopo1/MifBridge",
            InstalledVersion = "0.0.1"   // deliberately ancient
        };

        var ok = await svc.CheckOneAsync(mod);

        Assert.True(ok, "the check itself should succeed (network or rate limit?)");
        Assert.False(string.IsNullOrWhiteSpace(mod.LatestVersion));
        Assert.NotNull(mod.LastUpdateCheck);
        Assert.True(mod.UpdateAvailable, "0.0.1 should be behind whatever is published");
    }

    /// A mod that declares no version must be reported neither way. "We cannot tell" is not
    /// "up to date", and it is not "there is an update" either.
    [Fact]
    public async Task A_mod_with_no_declared_version_is_not_reported_as_updateable()
    {
        var mod = new ModInfo { Name = "NoVersion", ModUpdateUrl = "https://github.com/mifsopo1/MifBridge" };

        if (!await new ModUpdateService().CheckOneAsync(mod)) return;   // offline: nothing to assert

        Assert.False(mod.UpdateAvailable);
        Assert.False(string.IsNullOrWhiteSpace(mod.LatestVersion));
    }

    /// The property the Nexus banner depends on. If this ever fails, the feature needs an API
    /// key and every user has to paste one in - so it is worth knowing immediately.
    [Fact]
    public async Task Nexus_new_mods_query_needs_no_api_key()
    {
        var posts = await new NexusFeedService()
            .GetNewModsAsync("drugdealersimulator2", DateTime.UtcNow.AddDays(-90));

        Assert.NotEmpty(posts);
        Assert.All(posts, p =>
        {
            Assert.NotEqual(0, p.ModId);
            Assert.False(string.IsNullOrWhiteSpace(p.Name));
            Assert.Contains("nexusmods.com", p.Url);
        });
    }

    [Fact]
    public async Task Nexus_feed_excludes_adult_content_by_default()
    {
        var posts = await new NexusFeedService()
            .GetNewModsAsync("drugdealersimulator2", DateTime.UtcNow.AddDays(-365));

        Assert.All(posts, p => Assert.False(p.Adult));
    }

    /// Mods with no usable update address are skipped rather than counted as failures - so a
    /// user whose mods all predate the convention sees "nothing to check", not an error.
    [Fact]
    public async Task Mods_without_an_update_address_are_skipped_not_failed()
    {
        var result = await new ModUpdateService().CheckAllAsync(new[]
        {
            new ModInfo { Name = "Plain" },
            new ModInfo { Name = "WrongHost", ModUpdateUrl = "https://gitlab.com/a/b" }
        });

        Assert.True(result.Succeeded);
        Assert.Equal(0, result.Checked);
        Assert.Equal(0, result.UpdatesFound);
    }
}
