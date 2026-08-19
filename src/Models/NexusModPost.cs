namespace DDS2ModManager.Models;

/// One mod newly published on Nexus, as returned by the v2 GraphQL API.
///
/// Read-only and display-only: nothing here is installed or downloaded. Nexus is used purely to
/// discover that a mod EXISTS - actually updating an installed mod goes through the mod's own
/// ModUpdateUrl (see ModUpdateService), which is why none of this needs an API key.
public class NexusModPost
{
    public string Uid { get; set; } = "";
    public int ModId { get; set; }
    public string Name { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Uploader { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public string GameDomain { get; set; } = "";

    /// The mod's own version string, as the author wrote it. Free-form - "1", "1.2", "1.0.2" all
    /// appear in the live catalogue - so it is shown, never parsed or compared.
    public string Version { get; set; } = "";

    public DateTime UpdatedAt { get; set; }
    public int Downloads { get; set; }
    public int Endorsements { get; set; }

    /// Full-size image. Measured at 1322x1413 for one mod, so it is NOT what a tooltip should
    /// fetch - see ThumbnailUrl.
    public string? PictureUrl { get; set; }

    /// The small version, and what the hover card uses. Both are served from
    /// staticdelivery.nexusmods.com as image/webp despite ending in .png.
    public string? ThumbnailUrl { get; set; }

    /// The image to show on a card: the thumbnail, falling back to the full picture only if the
    /// thumbnail is missing.
    public string? CardImageUrl => ThumbnailUrl ?? PictureUrl;

    /// Nexus does not filter adult content server-side (the Nexus Discord bot does it in code
    /// for the same reason), so this comes back on every mod and is filtered here.
    ///
    /// Populated from `adultContent`. The older `adult` field still resolves but introspection
    /// reports it deprecated in favour of that one.
    public bool Adult { get; set; }

    public string Url => UrlFor(GameDomain, ModId);

    /// The one place a Nexus mod-page address is composed. Two copies of this drift the moment
    /// Nexus changes a path segment, and a declared link needs the same address a matched post
    /// produces or the two disagree about the same mod.
    public static string UrlFor(string gameDomain, int modId) =>
        $"https://www.nexusmods.com/{gameDomain}/mods/{modId}";

    /// "3 days ago" reads better than a timestamp on a banner that is about newness.
    public string AgeDisplay
    {
        get
        {
            var age = DateTime.UtcNow - CreatedAt.ToUniversalTime();
            if (age.TotalHours < 1) return "just now";
            if (age.TotalHours < 24) return $"{(int)age.TotalHours}h ago";
            if (age.TotalDays < 30) return $"{(int)age.TotalDays}d ago";
            return CreatedAt.ToLocalTime().ToString("d MMM");
        }
    }
}
