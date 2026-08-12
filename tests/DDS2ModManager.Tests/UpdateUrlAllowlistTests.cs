namespace DDS2ModManager.Tests;

/// The security boundary of the whole update feature.
///
/// A mod's update address comes from a file INSIDE the mod, and whatever it points at gets
/// downloaded and installed on the player's machine. An arbitrary URL field would be a malware
/// delivery channel; a github.com-only field is a public repository anyone can read first.
/// These are the tests that keep it that way.
///
/// Everything downstream trusts whatever comes out of GitHubUrlParser, so the parser has to
/// refuse anything that isn't unambiguously a GitHub repository rather than guess at it.
public class GitHubUrlParserTests
{
    [Theory]
    [InlineData("https://github.com/mifsopo1/MifBridge", "mifsopo1", "MifBridge")]
    [InlineData("https://github.com/631Brando/DDS2ModManager", "631Brando", "DDS2ModManager")]
    [InlineData("https://www.github.com/owner/repo", "owner", "repo")]
    [InlineData("https://github.com/owner/repo.git", "owner", "repo")]
    [InlineData("  https://github.com/owner/repo  ", "owner", "repo")]
    [InlineData("https://github.com/owner/repo/", "owner", "repo")]
    [InlineData("https://github.com/owner/repo/releases/tag/v1.2.0", "owner", "repo")]
    // The forms authors actually write, beyond a full URL.
    [InlineData("github.com/owner/repo", "owner", "repo")]
    [InlineData("owner/repo", "owner", "repo")]
    public void Accepts_and_parses(string url, string owner, string repo)
    {
        Assert.True(GitHubUrlParser.TryParse(url, out var o, out var r));
        Assert.Equal(owner, o);
        Assert.Equal(repo, r);
    }

    // Each of these is a real way an update channel gets hijacked.
    [Theory]
    [InlineData("http://github.com/owner/repo", "plain http is hijackable on a hostile network")]
    [InlineData("https://github.com.evil.com/owner/repo", "suffix attack")]
    [InlineData("https://evilgithub.com/owner/repo", "prefix attack")]
    [InlineData("https://notgithub.com/owner/repo", "lookalike host")]
    [InlineData("https://raw.githubusercontent.com/a/b", "not the repository host")]
    [InlineData("https://gitlab.com/owner/repo", "another forge")]
    [InlineData("ftp://github.com/owner/repo", "non-http scheme")]
    [InlineData("file:///C:/Windows/System32", "local file")]
    [InlineData("javascript:alert(1)", "script scheme")]
    [InlineData("https://github.com/owner", "no repo segment")]
    [InlineData("", "empty")]
    [InlineData("   ", "whitespace")]
    [InlineData(null, "null")]
    public void Rejects(string? url, string why) =>
        Assert.False(GitHubUrlParser.TryParse(url, out _, out _), why);

    /// Uri.Host already excludes anything before '@', so the host here is evil.example.com and
    /// not github.com. Worth pinning: this is the classic way a URL is made to LOOK like it
    /// points somewhere safe, and it is exactly the trick a mod would use.
    [Fact]
    public void Userinfo_cannot_disguise_the_real_host()
    {
        Assert.False(GitHubUrlParser.TryParse("https://github.com@evil.example.com/owner/repo", out _, out _));
        Assert.False(GitHubUrlParser.TryParse("https://github.com:x@evil.example.com/o/r", out _, out _));
    }

    // ---- a trailing full stop must not become part of the name ------------------------------

    /// The most plausible silent failure on this whole surface: a URL written at the end of a
    /// sentence. Every other trailing punctuation mark already failed the character check; the
    /// full stop survived into the repository name and produced a 404 mentioning no dot.
    [Theory]
    [InlineData("https://github.com/owner/repo.")]
    [InlineData("https://github.com/631Brando/DDS2ModManager.")]
    [InlineData("https://github.com/owner/repo..")]
    [InlineData("https://github.com/owner./repo")]
    [InlineData("owner/repo.")]
    public void A_trailing_full_stop_is_refused_rather_than_absorbed(string url) =>
        Assert.False(GitHubUrlParser.TryParse(url, out _, out _));

    /// ...but a LEADING dot stays legal. ".github" is a real and widely used repository name, so
    /// this must not be "reject dots at the edges".
    [Fact]
    public void A_leading_dot_is_still_a_valid_repository_name()
    {
        Assert.True(GitHubUrlParser.TryParse("https://github.com/owner/.github", out var owner, out var repo));
        Assert.Equal("owner", owner);
        Assert.Equal(".github", repo);
    }

    [Fact]
    public void A_dot_inside_a_name_is_still_fine()
    {
        Assert.True(GitHubUrlParser.TryParse("https://github.com/my.owner/My.Mod", out var owner, out var repo));
        Assert.Equal("my.owner", owner);
        Assert.Equal("My.Mod", repo);
    }

    // ---- github.com pages that are not repositories ------------------------------------------

    /// Any two-segment github.com link used to parse as a confident owner/repo pair, so pasting an
    /// organisation page gave players a trust prompt for a publisher called "orgs". The owner is
    /// the identity ModTrustService keys on, so a wrong one is not cosmetic.
    [Theory]
    [InlineData("https://github.com/orgs/631Brando/repositories")]
    [InlineData("https://github.com/sponsors/631Brando")]
    [InlineData("https://github.com/users/631Brando/projects")]
    [InlineData("https://github.com/topics/modding")]
    [InlineData("https://github.com/settings/profile")]
    [InlineData("https://github.com/apps/dependabot")]
    [InlineData("https://github.com/marketplace/actions/checkout")]
    [InlineData("https://github.com/collections/game-engines")]
    [InlineData("https://github.com/codespaces/new")]
    [InlineData("https://github.com/stars/631Brando/lists/mods")]
    [InlineData("orgs/631Brando")]
    public void Site_routes_are_not_owners(string url) =>
        Assert.False(GitHubUrlParser.TryParse(url, out _, out _));

    /// The reserved list is route-based, not "words that look reserved", and this is the case that
    /// forced that distinction: 'watching' IS a live GitHub user account (verified against the
    /// API), even though github.com/watching is also a page. It is safe because that page takes no
    /// second segment. Blocking it would break a real owner with no workaround.
    [Fact]
    public void A_real_account_whose_name_matches_a_single_segment_page_still_works()
    {
        Assert.True(GitHubUrlParser.TryParse("https://github.com/watching/SomeMod", out var owner, out var repo));
        Assert.Equal("watching", owner);
        Assert.Equal("SomeMod", repo);
    }

    /// Only the OWNER position is restricted. A repository may legitimately be called "topics".
    [Fact]
    public void A_repository_may_be_named_after_a_route()
    {
        Assert.True(GitHubUrlParser.TryParse("https://github.com/631Brando/topics", out var owner, out var repo));
        Assert.Equal("631Brando", owner);
        Assert.Equal("topics", repo);
    }

    /// Whatever comes out of here is interpolated straight into a GitHub API path, so a segment
    /// that isn't a plain repository reference has to be refused rather than passed along. A
    /// traversal attempt must never come back out as a usable owner/repo pair.
    [Theory]
    [InlineData("https://github.com/../../etc/passwd")]
    [InlineData("https://github.com/owner/../../../x")]
    [InlineData("https://github.com/own er/repo")]
    [InlineData("https://github.com/owner/repo%2F..%2Fother")]
    public void Refuses_anything_that_is_not_a_plain_repository_reference(string url)
    {
        // Either rejected outright, or normalised into something inert - never a pair containing
        // path syntax that could escape the API path it gets pasted into.
        if (!GitHubUrlParser.TryParse(url, out var owner, out var repo)) return;

        Assert.DoesNotContain("..", owner);
        Assert.DoesNotContain("..", repo);
        Assert.DoesNotContain("/", owner);
        Assert.DoesNotContain("/", repo);
        Assert.DoesNotContain(" ", owner);
        Assert.DoesNotContain(" ", repo);
    }
}
