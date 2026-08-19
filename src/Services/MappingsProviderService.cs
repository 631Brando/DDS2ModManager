using System.Reflection;

namespace DDS2ModManager.Services;

/// Extracts the embedded mappings.usmap to disk once, so CUE4Parse's file-based
/// mappings provider has a real path to read - while the end user only ever
/// deals with a single .exe.
public static class MappingsProviderService
{
    public static string EnsureExtracted()
    {
        AppPaths.EnsureRoot();
        var dest = AppPaths.Mappings;

        var asm = Assembly.GetExecutingAssembly();
        var resourceName = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("mappings.usmap", StringComparison.OrdinalIgnoreCase));

        if (resourceName == null)
            throw new FileNotFoundException(
                "Embedded mappings.usmap not found. Make sure Assets\\mappings.usmap is set as an " +
                "EmbeddedResource in the .csproj and actually contains your real mappings file.");

        using var stream = asm.GetManifestResourceStream(resourceName)!;

        // Re-extract whenever the cached copy's size doesn't match the embedded resource, not just
        // when it's missing. A previous build that shipped an empty/placeholder mappings.usmap would
        // otherwise leave a permanently-stuck 0-byte file behind, since File.Exists alone can't tell
        // a corrupt cache from a good one and every later run just silently reused it.
        if (!File.Exists(dest) || new FileInfo(dest).Length != stream.Length)
        {
            using var fs = new FileStream(dest, FileMode.Create, FileAccess.Write);
            stream.CopyTo(fs);
        }

        return dest;
    }
}
