namespace DDS2ModManager.Services;

/// Turns whatever a mod author wrote into a GitHub owner/repo pair, or rejects it.
///
/// This is the security boundary for mod auto-updating. The URL is supplied by the mod itself, so
/// everything downstream trusts whatever comes out of here - which means anything that isn't
/// unambiguously a GitHub repository has to be refused rather than guessed at.
///
/// Rejected on purpose:
///   - any host that isn't github.com (no redirects to follow, no "close enough" matches)
///   - userinfo tricks like https://github.com@evil.example.com/x/y
///   - lookalike hosts such as github.com.evil.example.com or notgithub.com
///   - http:// (an update fetched over plain HTTP could be swapped in transit)
public static class GitHubUrlParser
{
    private static readonly string[] AllowedHosts = { "github.com", "www.github.com" };

    /// Accepts the forms authors actually write:
    ///   https://github.com/owner/repo          (with or without .git, trailing slash, extra path)
    ///   github.com/owner/repo                  (no scheme)
    ///   owner/repo                             (shorthand)
    public static bool TryParse(string? declared, out string owner, out string repo)
    {
        owner = "";
        repo = "";
        if (string.IsNullOrWhiteSpace(declared)) return false;

        var text = declared.Trim();

        // Bare "owner/repo" shorthand, with no scheme and no host.
        if (!text.Contains("://") && !text.Contains('.') && text.Count(c => c == '/') == 1)
            return TrySplitPath(text, out owner, out repo);

        // Uri needs a scheme; assume https for "github.com/owner/repo". This never upgrades a
        // stated http:// to https - an author who wrote http is rejected below, not silently fixed.
        if (!text.Contains("://")) text = "https://" + text;

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps) return false;

        // Uri.Host already excludes any userinfo before '@', so this comparison can't be fooled by
        // https://github.com@evil.example.com/owner/repo - Host there is evil.example.com.
        if (!AllowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase)) return false;

        return TrySplitPath(uri.AbsolutePath, out owner, out repo);
    }

    private static bool TrySplitPath(string path, out string owner, out string repo)
    {
        owner = "";
        repo = "";

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2) return false;

        var candidateOwner = segments[0];
        var candidateRepo = segments[1];

        if (candidateRepo.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            candidateRepo = candidateRepo[..^4];

        if (!IsValidSegment(candidateOwner) || !IsValidSegment(candidateRepo)) return false;

        owner = candidateOwner;
        repo = candidateRepo;
        return true;
    }

    /// GitHub allows letters, digits, hyphens, underscores and dots in these. Anything else - a
    /// path traversal attempt, a query string, an encoded character - means this isn't a plain
    /// repository reference and shouldn't be treated as one.
    private static bool IsValidSegment(string segment)
    {
        if (segment.Length is 0 or > 100) return false;
        if (segment is "." or "..") return false;

        foreach (var c in segment)
        {
            if (char.IsAsciiLetterOrDigit(c)) continue;
            if (c is '-' or '_' or '.') continue;
            return false;
        }

        return true;
    }
}
