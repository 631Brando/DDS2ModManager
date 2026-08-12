using System.Text.Json.Serialization;

namespace DDS2ModManager.Models;

/// A ".dds2mod.json" file shipped with a mod, describing itself and where its updates come from.
///
/// This is how lua and patch mods opt in to updating. LogicMods can use it too, but don't need to:
/// they can declare a "ModUpdateUrl" string variable on their ModActor instead, which travels
/// inside the pak and can't be separated from the mod.
///
/// Every field is optional except UpdateUrl - a manifest that doesn't say where updates come from
/// has nothing to offer. Unknown fields are ignored rather than rejected, so a manifest written
/// for a later version of the manager still works here.
public class ModManifest
{
    /// Manifest format version, so a future breaking change can be detected rather than
    /// misinterpreted. Anything higher than the manager understands is refused.
    [JsonPropertyName("schema")]
    public int Schema { get; set; } = 1;

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("author")]
    public string? Author { get; set; }

    /// The mod's own version. Compared against the version in the update's manifest to decide
    /// whether there's anything newer; without it the manager falls back to comparing release tags.
    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// A GitHub repository whose releases hold this mod's downloads. Only github.com is accepted -
    /// see GitHubUrlParser for why.
    [JsonPropertyName("updateUrl")]
    public string? UpdateUrl { get => _updateUrl; set => _updateUrl ??= value; }

    private string? _updateUrl;

    /// Accepted alias for UpdateUrl.
    ///
    /// Two spellings went out in two different places - MODDING.md documented "updateUrl" while
    /// the in-app author guide documented "modUpdateUrl" - so both are in the wild. Reading only
    /// one would silently stop updates for whichever authors followed the other guide, and "my
    /// mod quietly stopped offering updates" is close to undiagnosable from the outside.
    ///
    /// Whichever key appears first wins. The getter returns null so this never serialises back
    /// out: it is a read-compatibility shim, not a second way to write a manifest.
    [JsonPropertyName("modUpdateUrl")]
    public string? ModUpdateUrl { get => null; set => _updateUrl ??= value; }

    /// Name of the release asset to download, when a release carries several. Without it the
    /// manager picks the single archive asset, and refuses to guess when there's more than one.
    [JsonPropertyName("asset")]
    public string? Asset { get; set; }

    /// The highest manifest schema this build knows how to read.
    public const int SupportedSchema = 1;

    /// The filename authors ship. Leading dot keeps it out of the way alphabetically and marks it
    /// as metadata rather than mod content.
    public const string FileName = ".dds2mod.json";
}
