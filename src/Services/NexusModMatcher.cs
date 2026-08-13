using System.Text;

namespace DDS2ModManager.Services;

/// Matches an installed mod to its Nexus entry by name, or refuses to.
///
/// Refusing matters more than matching. A wrong match shows someone a stranger's picture and
/// description on their own mod, which is worse than a blank card - so this does EXACT key
/// equality and nothing else.
///
/// That is a measured decision, not a cautious guess. Fuzzy matching was evaluated against the
/// real 19-mod install and the real 99-mod DDS2 catalogue at similarity thresholds 0.75, 0.70,
/// 0.65, 0.60, 0.55 and 0.50. At every single threshold it found ZERO additional correct matches,
/// and below 0.60 it started inventing wrong ones - "BotanistExpansion" onto "Brando's Cartel
/// Expansion" (different author, shared word "Expansion"), and "MifTools" onto a stranger's "DDS2
/// Tools". There is no point on the curve where fuzzy buys anything, because the mods it fails to
/// match are simply not published on Nexus at all. Do not add it back.
///
/// Measured result of the rule below: 10 of 19 installed rows matched, 0 wrong, 0 ambiguous.
/// Every mod that WAS on Nexus was found. Expect materially worse recall for third-party mods,
/// whose names follow no convention - which is what NexusModId on the mod's own declaration is
/// for. A declared id always wins; this is the fallback for mods that declare nothing.
public static class NexusModMatcher
{
    /// Keys shorter than this are too generic to trust. "DDS2 - Mod Compilation WIP" produces the
    /// head key "dds2", which would swallow any installed mod named DDS2-something. The shortest
    /// legitimate matched key measured was "largebolivars" at 13, so this costs nothing real.
    private const int MinimumKeyLength = 6;

    /// Suffixes this manager appends to installed mod names, which are never part of the Nexus
    /// title. Order matters: the parenthesised form is stripped before the underscore form.
    private static readonly string[] ParentheticalSuffixes = { "(logicmod)", "(logic mod)", "(luamod)", "(lua mod)" };
    private static readonly string[] UnderscoreSuffixes = { "_p", "_lua", "_logicmod" };

    /// Builds the lookup a set of Nexus mods offers, dropping every key that more than one mod
    /// claims.
    ///
    /// The uniqueness guard is mandatory, not defensive. In the live catalogue the head rule
    /// collapses four separate mods - "Gh0sted - 2x Pricing", "Gh0sted - Rebalance", "Gh0sted -
    /// Money Stacks" and "Gh0sted - Small Island Vendor Inventory Adjustment" - onto the single
    /// key "gh0sted". Without this, anyone installing one of them gets a one-in-four guess.
    public static Dictionary<string, NexusModPost> BuildIndex(IEnumerable<NexusModPost> mods)
    {
        var claims = new Dictionary<string, List<NexusModPost>>(StringComparer.Ordinal);

        foreach (var mod in mods)
        {
            foreach (var key in CandidateKeys(mod.Name))
            {
                if (!claims.TryGetValue(key, out var list)) claims[key] = list = new List<NexusModPost>();
                if (list.All(m => m.ModId != mod.ModId)) list.Add(mod);
            }
        }

        return claims
            .Where(kv => kv.Value.Count == 1)
            .ToDictionary(kv => kv.Key, kv => kv.Value[0], StringComparer.Ordinal);
    }

    /// The Nexus entry for an installed mod, or null. Never guesses.
    public static NexusModPost? Match(string installedName, Dictionary<string, NexusModPost> index)
    {
        var key = KeyForInstalled(installedName);
        if (key.Length < MinimumKeyLength) return null;

        return index.TryGetValue(key, out var found) ? found : null;
    }

    /// An installed mod's key: packaging suffixes removed, then normalised.
    public static string KeyForInstalled(string name) => Normalise(StripPackagingSuffixes(name));

    /// The one or two keys a Nexus title can be reached by.
    ///
    /// The second is the "head" - everything before the first SPACED dash - because authors append
    /// a marketing tail to the title: "Ethanol Extraction - Brew Ethanol from Alcohol". Seven of
    /// the ten measured matches depend on it.
    ///
    /// The dash must be spaced. Splitting on a bare hyphen would destroy names that legitimately
    /// contain one - "Rembows-Infinity-Durability-x-..." and "DDS2 Reshade -DealersHigh-" both
    /// exist in the live catalogue.
    private static IEnumerable<string> CandidateKeys(string nexusName)
    {
        var full = Normalise(nexusName);
        if (full.Length >= MinimumKeyLength) yield return full;

        var cut = nexusName.IndexOf(" - ", StringComparison.Ordinal);
        if (cut <= 0) yield break;

        var head = Normalise(nexusName[..cut]);
        if (head.Length >= MinimumKeyLength && head != full) yield return head;
    }

    private static string StripPackagingSuffixes(string name)
    {
        var working = name.Trim();

        // Parenthesised first: "DriveableScooter (LogicMod)" -> "DriveableScooter".
        foreach (var suffix in ParentheticalSuffixes)
        {
            if (working.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                working = working[..^suffix.Length].TrimEnd();
                break;
            }
        }

        // Then the underscore forms: "BiggerPackages_P" -> "BiggerPackages".
        foreach (var suffix in UnderscoreSuffixes)
        {
            if (working.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                working = working[..^suffix.Length];
                break;
            }
        }

        return working;
    }

    /// Lowercase, then drop everything that is not a-z or 0-9.
    ///
    /// One pass removes spaces, underscores, hyphens, apostrophes, dots and brackets together, so
    /// "Brando's DDS2 Helper" and "BrandosDDS2Helper" converge - which is exactly the difference
    /// between how a Nexus title and an installed folder are written.
    public static string Normalise(string text)
    {
        var sb = new StringBuilder(text.Length);

        foreach (var c in text)
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(c);
            else if (c is >= 'A' and <= 'Z') sb.Append((char)(c + 32));
        }

        return sb.ToString();
    }
}
