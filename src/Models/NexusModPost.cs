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
    public string? PictureUrl { get; set; }
    public DateTime CreatedAt { get; set; }
    public string GameDomain { get; set; } = "";

    /// Nexus does not filter adult content server-side (the Nexus Discord bot does it in code
    /// for the same reason), so this comes back on every mod and is filtered here.
    public bool Adult { get; set; }

    public string Url => $"https://www.nexusmods.com/{GameDomain}/mods/{ModId}";

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
