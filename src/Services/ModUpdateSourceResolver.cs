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
    /// Looks for a manifest belonging to an installed mod. Cheap: no game mount required, so this
    /// runs for every mod on startup.
    public ModUpdateSource? FromManifest(ModInfo mod)
    {
        foreach (var path in CandidateManifestPaths(mod))
        {
            if (!File.Exists(path)) continue;

            var manifest = ReadManifest(path, mod.Name);
            if (manifest?.UpdateUrl == null) continue;

            if (!GitHubUrlParser.TryParse(manifest.UpdateUrl, out var owner, out var repo))
            {
                LoggingService.Instance.Warn(
                    $"'{mod.Name}' declares an update URL that isn't a GitHub repository " +
                    $"('{manifest.UpdateUrl}') - ignoring it. Updates can only come from github.com.");
                continue;
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

        return null;
    }

    /// Reads "ModUpdateUrl" off a LogicMod's ModActor. Needs a mounted provider, so this only runs
    /// during a scan rather than on every startup.
    ///
    /// The value lives on the Blueprint's class default object, alongside every other variable the
    /// author declared. Unreal only serialises values that differ from the class default, so an
    /// unset or blank ModUpdateUrl simply isn't present - which is exactly the behaviour wanted.
    public ModUpdateSource? FromModActor(AbstractFileProvider provider, ModInfo mod)
    {
        var modActorPath = mod.ContainedAssetPaths.FirstOrDefault(p =>
            Path.GetFileNameWithoutExtension(p).Equals("ModActor", StringComparison.OrdinalIgnoreCase));
        if (modActorPath == null) return null;

        try
        {
            var pkg = provider.LoadPackage(modActorPath);

            foreach (var export in pkg.GetExports())
            {
                // The class default object is the one named "Default__<Class>".
                if (!export.Name.StartsWith("Default__", StringComparison.OrdinalIgnoreCase)) continue;

                var declared = ReadStringProperty(export, "ModUpdateUrl");
                if (string.IsNullOrWhiteSpace(declared)) continue;

                if (!GitHubUrlParser.TryParse(declared, out var owner, out var repo))
                {
                    LoggingService.Instance.Warn(
                        $"'{mod.Name}' has a ModUpdateUrl that isn't a GitHub repository ('{declared}') - " +
                        "ignoring it. Updates can only come from github.com.");
                    return null;
                }

                return new ModUpdateSource
                {
                    Declaration = ModUpdateDeclaration.BlueprintVariable,
                    Owner = owner,
                    Repo = repo,
                    DeclaredUrl = declared,
                    Author = ReadStringProperty(export, "ModAuthor") ?? "",
                    Version = ReadStringProperty(export, "ModVersion") ?? ""
                };
            }
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Warn($"Couldn't read update info from '{mod.Name}': {ex.Message}");
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
            yield return Path.Combine(mod.InstallPath, ModManifest.FileName);

        foreach (var file in mod.InstallFiles)
        {
            var dir = Path.GetDirectoryName(file);
            if (string.IsNullOrEmpty(dir) || !seen.Add(dir)) continue;

            // A pak's own folder is shared with every other pak, so only a manifest named after
            // this specific mod counts there - a bare .dds2mod.json would be ambiguous.
            var baseName = Path.GetFileNameWithoutExtension(file);
            yield return Path.Combine(dir, baseName + ModManifest.FileName);
        }

        if (!string.IsNullOrWhiteSpace(mod.InstallPath))
            yield return Path.Combine(mod.InstallPath, mod.Name + ModManifest.FileName);
    }

    private static ModManifest? ReadManifest(string path, string modName)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<ModManifest>(File.ReadAllText(path));
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
