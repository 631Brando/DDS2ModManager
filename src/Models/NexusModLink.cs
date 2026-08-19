using System.Text.Json.Serialization;

namespace DDS2ModManager.Models;

/// The Nexus page the USER says this mod is.
///
/// NexusModMatcher does exact name equality and refuses everything else. That is measured and
/// correct, and it leaves "AERR" permanently uncardable - its page is titled "AE Revolutions
/// Reloaded", the acronym is nowhere in it, and no normalisation reaches one from the other. This
/// is the id-based path that file's header once claimed to have and never did. A declared id is
/// not a guess.
///
/// Persisted - note the absence of [property: JsonIgnore] on ModInfo.NexusLink, unlike the runtime
/// fields beside it. This belongs to the user, like Notes and Tags, and survives a reinstall.
/// NexusInfo stays runtime-only: this stores an ID, not a copy of the post, so the cached
/// catalogue remains the one source of truth for what the post SAYS.
///
/// The domain is stored, never inferred from whichever game happens to be open. Nexus ids restart
/// per game: mod 79 is "AE Revolutions Reloaded" on drugdealersimulator and "Gh0sted - Rebalance"
/// on drugdealersimulator2, and 85 ids collide across the two live catalogues with zero shared
/// titles. A record that cannot name its own game cannot refuse when it is read under the wrong one.
///
/// PROPERTIES, not fields. ModRegistryService's JsonSerializerOptions does not set IncludeFields,
/// and System.Text.Json ignores public fields by default - fields here would round-trip as {} and
/// every link the user set would vanish on the next launch, with no error anywhere.
public sealed class NexusModLink
{
    public int ModId { get; set; }

    public string GameDomain { get; set; } = "";

    public NexusLinkKind Kind { get; set; } = NexusLinkKind.Linked;

    /// The ONLY gate anything may read. A hand-edited registry can hold Kind "Linked" with ModId 0;
    /// a raw null-check anywhere reintroduces that state. Same shape as ModUpdateSource.IsUsable.
    [JsonIgnore]
    public bool IsUsable => Kind == NexusLinkKind.Linked && ModId > 0 && GameDomain.Length > 0;

    [JsonIgnore]
    public string Url => NexusModPost.UrlFor(GameDomain, ModId);
}

/// Linked is 0 deliberately: a hand-written record with no Kind property must mean "linked", never
/// a suppression nobody asked for.
///
/// Append only, and keep this OUT of ProfileMod and ModBackup. ModRegistryService passes a
/// JsonStringEnumConverter so it writes as "Linked"/"NoPage" there, but ModProfileService and
/// ModBackupService build bare options with no converter and would write it as an int - the same
/// pinned-ordinal hazard ModType carries.
public enum NexusLinkKind
{
    Linked = 0,
    NoPage = 1
}
