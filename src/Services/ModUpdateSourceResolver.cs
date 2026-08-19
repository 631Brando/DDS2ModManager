using CUE4Parse.FileProvider;

namespace DDS2ModManager.Services;

/// Works out where a mod's updates come from, if the author declared anywhere at all.
///
/// Two routes, because the two kinds of mod have different places to put the information:
///
///   LogicMods    a "ModUpdateUrl" string variable on the ModActor Blueprint. It travels inside
///                the pak, so it can't be lost or separated from the mod.
///   lua / patch  a .dds2mod.json file shipped with the mod's files.
///
/// Both are read from what's already installed on disk, so a mod installed long before this
/// feature existed still gets picked up - nothing has to be re-downloaded through the manager to
/// become updatable.
///
/// Authors opt in. A mod that declares nothing is simply never checked, and that is not an error.
public class ModUpdateSourceResolver
{
    /// The variable names authors set on their ModActor.
    ///
    /// Fixed by agreement with the SDK's ModActor template - changing them here breaks every mod
    /// already published. Constants rather than literals so the in-app author guide quotes the
    /// same strings this reader looks for, and the two can't drift.
    public const string UrlProperty = "ModUpdateUrl";
    public const string VersionProperty = "ModVersion";
    public const string AuthorProperty = "ModAuthor";

    /// Looks for a manifest belonging to an installed mod. Cheap: no game mount required, so this
    /// runs for every mod on startup.
    public ModUpdateSource? FromManifest(ModInfo mod)
    {
        foreach (var path in CandidateManifestPaths(mod))
        {
            if (!File.Exists(path)) continue;

            var source = Build(ReadManifest(path, mod.Name), mod.Name);
            if (source != null) return source;
        }

        return null;
    }

    /// What a manifest inside this folder says the mod is CALLED, or null.
    ///
    /// Separate from FromManifestFolder because Build refuses a manifest with no updateUrl - it has
    /// nothing to offer the updater - and a manifest that names the mod but declares no updates is
    /// still telling the truth about its name. Routing the name through ModUpdateSource instead
    /// would mean handing the trust and update code a source with no repository behind it.
    ///
    /// The schema refusal still applies: ReadManifest rejects a manifest declaring a schema this
    /// build doesn't understand, and a manifest refused whole must not name the mod either.
    ///
    /// Sanitised, because this becomes a folder name for some mod types: a manifest is author-
    /// supplied text, and a "name" containing a path separator would write outside the destination.
    public string? NameFromManifestFolder(string modFolderPath, string fallbackName)
    {
        var path = FindManifest(modFolderPath);
        if (path == null) return null;

        var declared = ReadManifest(path, fallbackName)?.Name?.Trim();
        if (string.IsNullOrWhiteSpace(declared)) return null;

        if (declared.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;

        return declared;
    }

    /// The one manifest inside a folder the mod owns outright, or null. See the warning below about
    /// which folders may be searched.
    private static string? FindManifest(string modFolderPath)
    {
        try
        {
            if (!Directory.Exists(modFolderPath)) return null;

            return Directory
                .EnumerateFiles(modFolderPath, "*", SearchOption.AllDirectories)
                .FirstOrDefault(f => ModManifest.IsManifestFile(f));
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Warn($"Couldn't read a manifest from '{modFolderPath}': {ex.Message}");
            return null;
        }
    }

    /// Finds a .dds2mod.json anywhere inside a folder the mod owns outright.
    ///
    /// A recursive search is only safe for a folder the mod owns - an extracted archive, or a lua
    /// mod's own directory. Do NOT point this at a shared folder such as Content\Paks\LogicMods:
    /// every pak mod lives in there together, so a search would find a neighbour's manifest and
    /// then offer updates from someone else's repository. Installed mods go through the ModInfo
    /// overload above, which draws that distinction.
    public ModUpdateSource? FromManifestFolder(string modFolderPath, string modName)
    {
        var path = FindManifest(modFolderPath);
        return path == null ? null : Build(ReadManifest(path, modName), modName);
    }

    /// Reads one specific manifest file. For callers that have already worked out WHICH manifest
    /// belongs to the mod - the shared-folder case, where only a name match is safe.
    public ModUpdateSource? FromManifestFile(string manifestPath, string modName) =>
        Build(ReadManifest(manifestPath, modName), modName);

    /// Turns a parsed manifest into a usable source, or rejects it. The URL check lives here so
    /// every manifest route is gated by exactly the same rule.
    private static ModUpdateSource? Build(ModManifest? manifest, string modName)
    {
        if (manifest?.UpdateUrl == null) return null;

        if (!GitHubUrlParser.TryParse(manifest.UpdateUrl, out var owner, out var repo))
        {
            LoggingService.Instance.Warn(
                $"'{modName}' declares an update URL that isn't a GitHub repository " +
                $"('{manifest.UpdateUrl}') - ignoring it. Updates can only come from github.com.");
            return null;
        }

        return new ModUpdateSource
        {
            Declaration = ModUpdateDeclaration.Manifest,
            Owner = owner,
            Repo = repo,
            DeclaredUrl = manifest.UpdateUrl,
            Author = manifest.Author ?? "",
            Version = manifest.Version ?? "",
            DeclaredAssetName = manifest.Asset ?? ""
        };
    }

    /// Reads "ModUpdateUrl" off a LogicMod's ModActor. Needs a mounted provider, so this only runs
    /// during a scan rather than on every startup.
    ///
    /// The value lives on the Blueprint's class default object, alongside every other variable the
    /// author declared. Unreal only serialises values that differ from the class default, so an
    /// unset or blank ModUpdateUrl simply isn't present - which is exactly the behaviour wanted.
    public ModUpdateSource? FromModActor(AbstractFileProvider provider, ModInfo mod) =>
        FromModActor(provider, mod.ContainedAssetPaths, mod.Name);

    /// The same read, from a raw asset list rather than a ModInfo.
    ///
    /// The analyzer and the unmanaged-mod scanner both need this before any ModInfo exists - they
    /// are what produce one - and both already hold a mounted provider, so reading the property
    /// there costs nothing beyond the lookup.
    public ModUpdateSource? FromModActor(
        AbstractFileProvider provider, IEnumerable<string> assetPaths, string modName)
    {
        var modActorPath = assetPaths.FirstOrDefault(p =>
            Path.GetFileNameWithoutExtension(p).Equals("ModActor", StringComparison.OrdinalIgnoreCase));
        if (modActorPath == null) return null;

        try
        {
            var pkg = provider.LoadPackage(modActorPath);

            foreach (var export in pkg.GetExports())
            {
                // The class default object is the one named "Default__<Class>".
                if (!export.Name.StartsWith("Default__", StringComparison.OrdinalIgnoreCase)) continue;

                var declared = ReadStringProperty(export, UrlProperty);
                if (string.IsNullOrWhiteSpace(declared)) continue;

                if (!GitHubUrlParser.TryParse(declared, out var owner, out var repo))
                {
                    LoggingService.Instance.Warn(
                        $"'{modName}' has a ModUpdateUrl that isn't a GitHub repository ('{declared}') - " +
                        "ignoring it. Updates can only come from github.com.");
                    return null;
                }

                return new ModUpdateSource
                {
                    Declaration = ModUpdateDeclaration.BlueprintVariable,
                    Owner = owner,
                    Repo = repo,
                    DeclaredUrl = declared,
                    Author = ReadStringProperty(export, AuthorProperty) ?? "",
                    Version = ReadStringProperty(export, VersionProperty) ?? ""
                };
            }
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Warn($"Couldn't read update info from '{modName}': {ex.Message}");
        }

        return null;
    }

    /// Blueprint string variables come through as StrProperty; names are matched case-insensitively
    /// because authors won't all capitalise the variable the same way.
    private static string? ReadStringProperty(CUE4Parse.UE4.Assets.Exports.UObject export, string name)
    {
        try
        {
            var prop = export.Properties.FirstOrDefault(p =>
                string.Equals(p.Name.Text, name, StringComparison.OrdinalIgnoreCase));
            var value = prop?.Tag?.GenericValue?.ToString();
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
        catch
        {
            return null;
        }
    }

    /// Where a manifest could reasonably sit for each kind of mod: inside a lua mod's own folder,
    /// or beside a pak. Both the install folder and the folders holding the mod's actual files are
    /// checked, since those differ for pak mods.
    private static IEnumerable<string> CandidateManifestPaths(ModInfo mod)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(mod.InstallPath) && seen.Add(mod.InstallPath))
            foreach (var ending in ModManifest.FileNames)
                yield return Path.Combine(mod.InstallPath, ending);

        foreach (var file in mod.InstallFiles)
        {
            var dir = Path.GetDirectoryName(file);
            if (string.IsNullOrEmpty(dir) || !seen.Add(dir)) continue;

            // A pak's own folder is shared with every other pak, so only a manifest named after
            // this specific mod counts there - a bare .dds2mod.json would be ambiguous.
            var baseName = Path.GetFileNameWithoutExtension(file);
            foreach (var ending in ModManifest.FileNames)
                yield return Path.Combine(dir, baseName + ending);
        }

        if (!string.IsNullOrWhiteSpace(mod.InstallPath))
            foreach (var ending in ModManifest.FileNames)
                yield return Path.Combine(mod.InstallPath, mod.Name + ending);
    }

    /// Case-insensitive on purpose. A manifest is hand-written by a mod author in a text editor,
    /// and "UpdateUrl" or "updateURL" is a spelling mistake, not a different field - matching
    /// strictly would silently give them a mod that never offers updates, with nothing to see
    /// wrong in the file. Allowing comments and trailing commas is the same argument.
    private static readonly JsonSerializerOptions ManifestOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static ModManifest? ReadManifest(string path, string modName)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<ModManifest>(File.ReadAllText(path), ManifestOptions);
            if (manifest == null) return null;

            if (manifest.Schema > ModManifest.SupportedSchema)
            {
                LoggingService.Instance.Warn(
                    $"'{modName}' ships a manifest written for a newer version of this manager " +
                    $"(schema {manifest.Schema}, this build understands {ModManifest.SupportedSchema}). " +
                    "Ignoring it rather than guessing at what it means.");
                return null;
            }

            return manifest;
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Warn($"Couldn't read {Path.GetFileName(path)} for '{modName}': {ex.Message}");
            return null;
        }
    }
}
