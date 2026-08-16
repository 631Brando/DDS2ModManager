using System.Text.Json.Serialization;

namespace DDS2ModManager.Models;

/// One curated entry in the verified list.
public class VerifiedEntry
{
    /// GitHub owner the entry covers, e.g. "631Brando". Matched case-insensitively.
    [JsonPropertyName("owner")]
    public string Owner { get; set; } = "";

    /// Optional repository. When set, only that one repo is verified; when absent, everything
    /// published by the owner is. Naming the repo is the tighter option and is preferred for
    /// authors who publish a mix of reviewed and unreviewed work.
    [JsonPropertyName("repo")]
    public string? Repo { get; set; }

    /// Shown to the user, so they know who vouched for it and roughly when.
    [JsonPropertyName("note")]
    public string? Note { get; set; }

    [JsonPropertyName("verifiedBy")]
    public string? VerifiedBy { get; set; }

    public bool Covers(string owner, string repo) =>
        string.Equals(Owner, owner, StringComparison.OrdinalIgnoreCase)
        && (string.IsNullOrEmpty(Repo) || string.Equals(Repo, repo, StringComparison.OrdinalIgnoreCase));
}

/// The curated list of mod sources the maintainers have looked at.
///
/// Fetched from a repository the maintainers control, so entries can be added without shipping a
/// new build of the manager, and cached on disk so it still works offline.
///
/// What an entry actually asserts is narrow, and worth stating exactly: the maintainers have
/// checked the GitHub ACCOUNT, so a download fetched from it is coming from somewhere known.
///
/// It says nothing about any particular mod file. A mod declares its own update address, and
/// nothing is signed, so any mod can name any account and inherit its badge - the claim would be
/// useless to an attacker (updates would then be fetched from the real account's repository, which
/// they do not control) but it would still look reassuring, which is why the wording everywhere
/// describes the address rather than the mod.
///
/// Nor does it mean "safe forever": an account can be compromised later. It exists so a user
/// doesn't have to weigh up every unknown address alone, not to remove the decision from them -
/// the manager still asks before installing anything, verified or not.
public class VerifiedList
{
    [JsonPropertyName("schema")]
    public int Schema { get; set; } = 1;

    /// When the maintainers last changed it, for display.
    [JsonPropertyName("updated")]
    public string? Updated { get; set; }

    [JsonPropertyName("entries")]
    public List<VerifiedEntry> Entries { get; set; } = new();

    public const int SupportedSchema = 1;

    public bool IsVerified(string owner, string repo) =>
        !string.IsNullOrEmpty(owner) && Entries.Any(e => e.Covers(owner, repo));

    public VerifiedEntry? Find(string owner, string repo) =>
        Entries.FirstOrDefault(e => e.Covers(owner, repo));
}
