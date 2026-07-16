using SharpCompress.Archives;
using SharpCompress.Common;

namespace DDS2ModManager.Services;

/// Wraps SharpCompress so the rest of the app doesn't care whether a mod (or a UE4SS
/// release) shipped as .zip, .7z, or .rar - System.IO.Compression only understands .zip,
/// so anything else needs a real archive library.
public static class ArchiveExtractionService
{
    public static readonly string[] SupportedExtensions = { ".zip", ".7z", ".rar" };

    public static bool IsSupportedArchive(string path) =>
        SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static void ExtractToDirectory(string archivePath, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);

        using var archive = ArchiveFactory.OpenArchive(archivePath);
        foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
        {
            entry.WriteToDirectory(destinationDir, new ExtractionOptions
            {
                ExtractFullPath = true,
                Overwrite = true
            });
        }

        LoggingService.Instance.Info(
            $"Extracted {Path.GetFileName(archivePath)} ({archive.Entries.Count(e => !e.IsDirectory)} file(s)).");
    }
}
