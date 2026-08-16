using System.Text.Json.Serialization;

namespace DDS2ModManager.Models;

/// One Nexus author the maintainers are happy to recommend.
public class TrustedNexusAuthor
{
    /// Nexus username, exactly as it appears in their profile URL. Matched case-insensitively.
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// Shown to the user, so a name they don't recognise comes with a reason.
    [JsonPropertyName("note")]
    public string? Note { get; set; }

    [JsonPropertyName("addedBy")]
    public string? AddedBy { get; set; }
}

/// The curated list of Nexus authors behind the Trusted Mods page.
///
/// Deliberately a separate list from <see cref="VerifiedList"/>, and the difference is the whole
/// reason this type exists rather than reusing that one:
///
///   VerifiedList              GitHub accounts. A security boundary. It changes how the update
///                             prompt describes where executable content is being fetched from.
///   TrustedNexusAuthorList    Nexus accounts. A recommendation. It decides whose mods appear on a
///                             browsing page that only ever opens a web page.
///
/// Folding them together would be a mistake in one specific direction: a name added here to make
/// someone's mods easier to find would silently start vouching for their GitHub releases. Keeping
/// two lists means adding a browsing recommendation can never widen what installs without asking.
///
/// Nothing here is a claim about a particular mod. People publish faster than anyone can read, so
/// this says "this author's work is worth finding", not "everything they release has been checked".
public class TrustedNexusAuthorList
{
    [JsonPropertyName("schema")]
    public int Schema { get; set; } = 1;

    [JsonPropertyName("updated")]
    public string? Updated { get; set; }

    [JsonPropertyName("authors")]
    public List<TrustedNexusAuthor> Authors { get; set; } = new();

    public const int SupportedSchema = 1;

    public bool Contains(string? uploader) =>
        !string.IsNullOrWhiteSpace(uploader)
        && Authors.Any(a => string.Equals(a.Name, uploader, StringComparison.OrdinalIgnoreCase));

    public TrustedNexusAuthor? Find(string? uploader) =>
        Authors.FirstOrDefault(a => string.Equals(a.Name, uploader, StringComparison.OrdinalIgnoreCase));

    /// Compiled into the build so the page works on a machine that has never reached GitHub.
    ///
    /// A browsing page that comes up empty reads as broken rather than as offline, and the fetched
    /// list only ever adds to this - so the worst case is a first run that shows a slightly older
    /// set of authors than the published file.
    public static TrustedNexusAuthorList Default => new()
    {
        Authors =
        {
            new TrustedNexusAuthor { Name = "brando136", Note = "Creator and maintainer of DDS2 Mod Manager." },
            new TrustedNexusAuthor { Name = "mifsopo", Note = "Contributor to DDS2 Mod Manager." },
            new TrustedNexusAuthor { Name = "huslaa", Note = "Long-running DDS2 modding tools and gameplay mods." }
        }
    };
}
