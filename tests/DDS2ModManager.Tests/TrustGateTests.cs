using System.Reflection;

namespace DDS2ModManager.Tests;

/// The rules around trusting a mod author.
///
/// An earlier revision had a setting that let updates from trusted authors install with no
/// prompt. It was removed rather than defaulted off, so the invariant these tests protect is
/// now much simpler and much stronger: there is NO path that installs a mod update without
/// asking. Trust changes how much the prompt explains; it never removes it.
///
/// Note on scope: ModTrustService is a singleton that persists to the real %AppData%, so these
/// tests deliberately do not exercise it directly - a unit test has no business rewriting the
/// user's actual trusted-authors list. What is covered here is everything derivable without it.
public class TrustGateTests
{
    /// The guard against the auto-install setting being reintroduced by habit.
    ///
    /// Reflection rather than a compile error because a re-added property would compile fine
    /// everywhere; the point is to make bringing it back a deliberate act that has to delete a
    /// test explaining why it went away.
    [Fact]
    public void There_is_no_setting_that_installs_mod_updates_without_asking()
    {
        var suspicious = typeof(AppSettings)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.Name.Contains("AutoInstall", StringComparison.OrdinalIgnoreCase)
                     || p.Name.Contains("Silent", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Name)
            .ToList();

        Assert.True(suspicious.Count == 0,
            "A mod update is executable content from the author's own repository that Nexus never "
            + "scanned, and a lua mod runs code in the game's process. If you are adding "
            + $"{string.Join(", ", suspicious)} on purpose, delete this test and say why in the commit. "
            + "Do not just flip it to default-off.");
    }

    [Fact]
    public void Update_checking_defaults_to_on() =>
        Assert.True(new AppSettings().CheckForModUpdatesOnStartup);

    // ---- UpdateUrlChanged: the moved-address detector ---------------------------------------

    private static ModInfo WithSource(string declaredUrl, string? installedWith) => new()
    {
        Name = "T",
        UpdateSource = new ModUpdateSource
        {
            Declaration = ModUpdateDeclaration.Manifest,
            Owner = "owner",
            Repo = "repo",
            DeclaredUrl = declaredUrl
        },
        InstalledUpdateUrl = installedWith
    };

    [Fact]
    public void An_unchanged_address_is_not_flagged() =>
        Assert.False(WithSource("https://github.com/owner/repo", "https://github.com/owner/repo").UpdateUrlChanged);

    /// The case the whole mechanism exists for: the mod now points somewhere other than where it
    /// pointed when the user installed it, which is what a hijacked update channel looks like.
    [Fact]
    public void A_moved_address_is_flagged() =>
        Assert.True(WithSource("https://github.com/someone-else/theirs", "https://github.com/owner/repo").UpdateUrlChanged);

    [Fact]
    public void Case_alone_is_not_a_move() =>
        Assert.False(WithSource("https://GitHub.com/Owner/Repo", "https://github.com/owner/repo").UpdateUrlChanged);

    /// A mod installed before this existed has nothing recorded to compare against. That must
    /// read as "unknown", not as "moved" - otherwise every pre-existing mod would light up as
    /// compromised the first time the user updated the manager.
    [Fact]
    public void No_recorded_address_is_not_a_move() =>
        Assert.False(WithSource("https://github.com/owner/repo", null).UpdateUrlChanged);

    [Fact]
    public void A_mod_declaring_nothing_is_not_a_move() =>
        Assert.False(new ModInfo { Name = "T", InstalledUpdateUrl = "https://github.com/owner/repo" }.UpdateUrlChanged);

    // ---- the verified list ------------------------------------------------------------------

    [Fact]
    public void An_empty_verified_list_verifies_nothing() =>
        Assert.False(new VerifiedList().IsVerified("owner", "repo"));

    /// Trust is granted per GitHub ACCOUNT, because whoever controls the account controls every
    /// release under it. A mod's declared Author never overrides who actually publishes it.
    [Fact]
    public void The_trust_key_is_rooted_in_the_repository_owner()
    {
        var source = new ModUpdateSource { Owner = "realowner", Repo = "r", DeclaredUrl = "https://github.com/realowner/r" };
        Assert.Contains("realowner", source.TrustKey);

        source.Author = "Someone Claiming To Be Else";
        Assert.Contains("realowner", source.TrustKey);
    }
}
