namespace DDS2ModManager.Models;

/// How a mod told us where its updates come from.
public enum ModUpdateDeclaration
{
    /// Nothing found - the mod doesn't opt in to updates.
    None,

    /// A "ModUpdateUrl" string variable on the LogicMod's ModActor Blueprint.
    BlueprintVariable,

    /// A .dds2mod.json manifest shipped alongside the mod's files.
    Manifest
}

/// A mod's declared update source, as found on disk.
///
/// Authors opt in; nothing is assumed. A LogicMod declares a "ModUpdateUrl" string variable on its
/// ModActor, and lua/patch mods ship a .dds2mod.json next to their files. Either way it resolves
/// to a GitHub repository whose releases the manager can check.
///
/// Only GitHub is accepted. The URL comes from inside the mod itself, so an arbitrary address
/// would let a mod point the updater at any server it liked - and since a lua mod runs code in
/// the game's process, that's a route worth closing. Restricting to a repository host also means
/// the source of an update is always something a user can go and read.
public class ModUpdateSource
{
    public ModUpdateDeclaration Declaration { get; set; } = ModUpdateDeclaration.None;

    /// GitHub owner and repository parsed out of the declared URL.
    public string Owner { get; set; } = "";
    public string Repo { get; set; } = "";

    /// Exactly what the mod declared, kept verbatim so the UI can show the user where an update
    /// would come from before they agree to anything.
    public string DeclaredUrl { get; set; } = "";

    /// Author name, when the mod states one.
    ///
    /// Recorded but not displayed: the prompt and the mod list both name Owner, the GitHub account
    /// that actually publishes the release, because that is the identity trust is granted against
    /// and it is verifiable. This is free text the mod says about itself.
    public string Author { get; set; } = "";

    /// The mod's own version string, when it states one.
    ///
    /// Required in practice, despite plenty of mods not setting it: there is no tag-only fallback.
    /// With nothing to compare a release tag against, ModUpdateService.CheckOneAsync reports
    /// neither "up to date" nor "update available" - both would be guesses - so the mod is checked
    /// and then left alone. MODDING.md tells authors as much.
    public string Version { get; set; } = "";

    /// Release asset the author named, for repositories whose releases carry several files. When
    /// absent the manager only proceeds if there's exactly one installable archive, rather than
    /// picking one and possibly installing the wrong thing.
    public string DeclaredAssetName { get; set; } = "";

    public bool IsUsable => Declaration != ModUpdateDeclaration.None
                            && Owner.Length > 0 && Repo.Length > 0;

    public string RepositoryUrl => $"https://github.com/{Owner}/{Repo}";

    /// The identity trust is granted against. Falls back to the repository owner when the mod
    /// doesn't name an author, which is the same person in practice and is verifiable, unlike a
    /// free-text name the mod supplies about itself.
    public string TrustKey => Author.Length > 0 ? $"{Owner}/{Author}" : Owner;
}
