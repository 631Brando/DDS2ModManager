namespace DDS2ModManager.Services;

/// Reads a Nexus mod-page address, or a bare mod number, into (game domain, mod id).
///
/// Separate from GitHubUrlParser rather than an extension of it. That class calls itself the
/// security boundary for mod auto-updating and its value is being narrow about one host; this one
/// answers a different question about a different site, and the signature has to diverge anyway -
/// every form GitHubUrlParser accepts carries both of its values, while a bare Nexus number does
/// not carry its game, so the active domain has to be passed in rather than assumed inside.
///
/// This parser REPORTS what it read and never compares the domain it found against the active
/// game. The caller does that, so the refusal can name both games.
public static class NexusUrlParser
{
    private static readonly string[] AllowedHosts = { "nexusmods.com", "www.nexusmods.com" };

    /// Accepts:
    ///   https://www.nexusmods.com/drugdealersimulator/mods/79   (+ query, fragment, trailing /)
    ///   https://www.nexusmods.com/games/drugdealersimulator/mods/79
    ///   nexusmods.com/drugdealersimulator/mods/79               (no scheme)
    ///   79                                                      (bare id, takes activeDomain)
    public static bool TryParse(string? declared, string activeDomain, out string domain, out int modId)
    {
        domain = "";
        modId = 0;

        var text = (declared ?? "").Trim();
        if (text.Length == 0) return false;

        // A bare mod number takes the game currently open, because that is the only game it could
        // mean - the number was typed against a mod that is installed under it.
        if (IsAllDigits(text))
        {
            if (activeDomain.Length == 0) return false;
            if (!int.TryParse(text, out modId) || modId <= 0) return false;

            domain = activeDomain;
            return true;
        }

        // Uri needs a scheme. http:// is ACCEPTED here and the scheme discarded - a deliberate
        // divergence from GitHubUrlParser, which rejects http because an update fetched over plain
        // HTTP could be swapped in transit. Nothing is ever fetched from this URL: only (domain,
        // id) survives, and the address later opened is recomposed as https by NexusModPost.UrlFor.
        // Discarding a scheme is not the silent upgrade the sibling refuses to do.
        if (!text.Contains("://")) text = "https://" + text;

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) return false;

        // Uri.Host already excludes any userinfo before '@', so this cannot be fooled by
        // https://nexusmods.com@evil.example.com/x/mods/1 - Host there is evil.example.com.
        if (!AllowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase)) return false;

        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Nexus has served both layouts. Skipping a leading "games" segment covers the newer one
        // without a second code path.
        if (segments.Length > 0 && segments[0].Equals("games", StringComparison.OrdinalIgnoreCase))
            segments = segments[1..];

        // The shape IS the validation, and it is this parser's analogue of GitHubUrlParser's
        // reserved-route list: without it, nexusmods.com/profile/Someone/mods reads as a confident
        // pair naming a game called "profile".
        if (segments.Length < 3) return false;
        if (!segments[1].Equals("mods", StringComparison.OrdinalIgnoreCase)) return false;

        // All digits, NOT int.TryParse - that silently accepts "+79", " 79" and "079", and would
        // read "/mods/79." as 79 rather than refusing a URL nobody meant to paste.
        if (!IsAllDigits(segments[2])) return false;
        if (!int.TryParse(segments[2], out modId) || modId <= 0) return false;

        domain = segments[0];
        return domain.Length > 0;
    }

    private static bool IsAllDigits(string s) => s.Length > 0 && s.All(char.IsAsciiDigit);
}
