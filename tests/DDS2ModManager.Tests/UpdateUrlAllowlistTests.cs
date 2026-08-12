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
