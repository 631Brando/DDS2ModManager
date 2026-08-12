namespace DDS2ModManager.Tests;

/// The security boundary of the whole update feature.
///
/// A mod's update address comes from a file INSIDE the mod, and whatever it points at gets
/// downloaded and installed on the player's machine. An arbitrary URL field would be a malware
/// delivery channel; a github.com-only field is a public repository anyone can read first.
/// These are the tests that keep it that way.
public class UpdateUrlAllowlistTests
{
    [Theory]
    [InlineData("https://github.com/mifsopo1/MifBridge")]
    [InlineData("https://github.com/631Brando/DDS2ModManager")]
    [InlineData("https://www.github.com/owner/repo")]
    [InlineData("https://github.com/owner/repo.git")]
    [InlineData("  https://github.com/owner/repo  ")]
    public void Accepts_github_https(string url) =>
        Assert.True(ModUpdateSourceReader.IsAllowedUpdateUrl(url));

    // Each of these is a real way an update channel gets hijacked.
    [Theory]
    [InlineData("http://github.com/owner/repo", "plain http is hijackable on a hostile network")]
    [InlineData("https://github.com.evil.com/owner/repo", "suffix attack")]
    [InlineData("https://evilgithub.com/owner/repo", "prefix attack")]
    [InlineData("https://raw.githubusercontent.com/a/b", "not the repository host")]
    [InlineData("https://gitlab.com/owner/repo", "another forge")]
    [InlineData("ftp://github.com/owner/repo", "non-http scheme")]
    [InlineData("file:///C:/Windows/System32", "local file")]
    [InlineData("javascript:alert(1)", "script scheme")]
    [InlineData("github.com/owner/repo", "no scheme")]
    [InlineData("", "empty")]
    [InlineData("   ", "whitespace")]
    [InlineData(null, "null")]
    public void Rejects(string? url, string why) =>
        Assert.False(ModUpdateSourceReader.IsAllowedUpdateUrl(url), why);

    [Theory]
    [InlineData("https://github.com/mifsopo1/MifBridge", "mifsopo1", "MifBridge")]
    [InlineData("https://github.com/owner/repo.git", "owner", "repo")]
    [InlineData("https://github.com/owner/repo/releases/tag/v1.2.0", "owner", "repo")]
    [InlineData("https://github.com/owner/repo/", "owner", "repo")]
    public void Parses_owner_and_repo(string url, string owner, string repo)
    {
        Assert.True(ModUpdateSourceReader.TryParseGitHubRepo(url, out var o, out var r));
        Assert.Equal(owner, o);
        Assert.Equal(repo, r);
    }

    [Theory]
    [InlineData("https://github.com/owner")]            // no repo segment
    [InlineData("https://gitlab.com/owner/repo")]       // right shape, wrong host
    public void Refuses_to_parse(string url) =>
        Assert.False(ModUpdateSourceReader.TryParseGitHubRepo(url, out _, out _));
}
