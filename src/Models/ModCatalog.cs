using System.Text.Json.Serialization;

namespace DDS2ModManager.Models;

/// One mod offered by a curated catalog.
public class CatalogMod
{
    /// Stable identifier, used to match a catalog entry against something already installed.
    /// Should not change even if the display name does.
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("summary")]
    public string? Summary { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// "LogicMod", "PatchMod" or "LuaMod". Display only - the installer still determines the real
    /// type by reading the downloaded files, because a catalog saying otherwise wouldn't make it so.
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    /// GitHub repository holding this mod's releases. Same rule as everywhere else: github.com
    /// only, and parsed through GitHubUrlParser rather than trusted as written.
    [JsonPropertyName("repo")]
    public string Repo { get; set; } = "";

    /// Which release asset to download, when a release carries several.
    [JsonPropertyName("asset")]
    public string? Asset { get; set; }

    /// Optional mods this one needs, by catalog id. Shown to the user; not auto-installed, since
    /// silently pulling in extra mods is exactly the kind of surprise this app tries to avoid.
    [JsonPropertyName("requires")]
    public List<string> Requires { get; set; } = new();

    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = new();

    /// Set when the catalog entry matches a mod already installed - filled in at runtime, not
    /// read from the file.
    [JsonIgnore]
    public ModInfo? Installed { get; set; }

    [JsonIgnore]
    public bool IsInstalled => Installed != null;

    [JsonIgnore]
    public string TypeDisplay => string.IsNullOrWhiteSpace(Type) ? "Mod" : Type;

    [JsonIgnore]
    public string VersionDisplay => string.IsNullOrWhiteSpace(Version) ? "" : $"v{Version}";

    [JsonIgnore]
    public string StatusDisplay => IsInstalled ? "Installed" : "Not installed";

    [JsonIgnore]
    public string TagsDisplay => Tags.Count == 0 ? "" : string.Join("  ·  ", Tags);
}

/// A published list of mods from one author, so they can be browsed and installed from inside the
/// manager instead of hunting them down individually.
///
/// Deliberately the same shape as the verified list: a JSON file in a repository, fetched over
/// HTTPS and cached locally. That keeps it editable with a commit, readable by anyone who wants to
/// check what they're being offered, and functional offline.
///
/// A catalog is only a list of pointers. Everything it offers still goes through the ordinary
/// install path - the same type detection, the same conflict checking, the same confirmation - so
/// being in a catalog grants a mod no special treatment.
public class ModCatalog
{
    [JsonPropertyName("schema")]
    public int Schema { get; set; } = 1;

    /// Shown as the page heading.
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("author")]
    public string? Author { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("updated")]
    public string? Updated { get; set; }

    [JsonPropertyName("mods")]
    public List<CatalogMod> Mods { get; set; } = new();

    public const int SupportedSchema = 1;
}
