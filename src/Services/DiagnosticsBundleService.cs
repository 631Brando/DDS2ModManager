using System.IO;
using System.IO.Compression;
using System.Text;

namespace DDS2ModManager.Services;

/// Packs everything needed to diagnose a problem into one zip the user can attach.
///
/// The point is to end the round trip. Without this, every report starts with "send me your log",
/// then "and your mod list", then "which version?", and each question costs a day. This collects
/// all of it at once, and — just as importantly — collects it CONSISTENTLY, so two reports can
/// be compared.
///
/// Deliberately excluded: save games (personal, large, and never the cause), the game's config
/// files (the shipped ini carries the developers' BugSplat credentials, recovered from decompiled
/// content — those must not travel in a file a user posts publicly), and mod files themselves.
public class DiagnosticsBundleService
{
    /// Logs are rotated at 20 files; the recent handful is what is diagnostically useful and
    /// keeps the bundle small enough to attach to a Discord message.
    private const int LogsToInclude = 5;

    public record BundleRequest(
        GameInstallation? Game,
        IReadOnlyList<ModInfo> Mods,
        IReadOnlyList<ModConflictGroup> Conflicts,
        UE4SSInstallInfo? Ue4ss,
        string ManagerVersion);

    /// Writes the bundle and returns its path, or null if it couldn't be written.
    public string? Create(BundleRequest request, string destinationPath)
    {
        try
        {
            using var zip = ZipFile.Open(destinationPath, ZipArchiveMode.Create);

            Add(zip, "summary.txt", BuildSummary(request));
            Add(zip, "mods.txt", BuildModList(request.Mods));
            Add(zip, "conflicts.txt", BuildConflicts(request.Conflicts));
            AddRecentLogs(zip);

            LoggingService.Instance.Success($"Diagnostics saved to {destinationPath}");
            return destinationPath;
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Error($"Couldn't create the diagnostics bundle: {ex.Message}");
            return null;
        }
    }

    private static void Add(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(content);
    }

    private static string BuildSummary(BundleRequest r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{AppPaths.AppDisplayName} - diagnostics");
        sb.AppendLine($"Created            {DateTime.Now:yyyy-MM-dd HH:mm:ss} ({TimeZoneInfo.Local.StandardName})");
        sb.AppendLine($"Manager version    {r.ManagerVersion}");
        sb.AppendLine($"Windows            {Environment.OSVersion.Version}  ({(Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit")})");
        sb.AppendLine($".NET               {Environment.Version}");
        sb.AppendLine();

        if (r.Game == null) sb.AppendLine("Game               NOT FOUND - the manager could not locate the game folder.");
        else
        {
            sb.AppendLine($"Game folder        {r.Game.RootPath}");
            sb.AppendLine($"Paks folder        {r.Game.PaksPath}");

            var stamp = GameVersionWatchService.Read(r.Game);
            sb.AppendLine($"Game build         {(stamp == null ? "unreadable" : stamp.Display)}");

            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(r.Game.RootPath)!);
                sb.AppendLine($"Free space         {drive.AvailableFreeSpace / 1024 / 1024 / 1024} GB on {drive.Name}");
            }
            catch { /* a drive we can't stat is not worth failing the bundle over */ }
        }

        sb.AppendLine();
        sb.AppendLine(r.Ue4ss == null
            ? "UE4SS              unknown"
            : $"UE4SS              installed={r.Ue4ss.IsInstalled}  managedByUs={r.Ue4ss.IsManagedByUs}  " +
              // The ASSET name, not the tag. For a by-tag fetch the tag is always the literal
              // "experimental-latest", so every bundle ever generated said version=experimental-latest
              // and the build a user was actually running was nowhere in it - which is exactly what
              // you need when someone reports a regression after an update.
              $"asset={r.Ue4ss.InstalledAssetName ?? "unknown"}  " +
              $"reported={r.Ue4ss.DetectedVersion ?? "unknown"}  " +
              $"tag={r.Ue4ss.InstalledVersionTag ?? "unknown"}  experimental={r.Ue4ss.IsConfirmedExperimental}");

        sb.AppendLine();
        sb.AppendLine($"Mods               {r.Mods.Count} tracked, {r.Mods.Count(m => m.IsEnabled)} enabled");
        sb.AppendLine($"Conflicts          {r.Conflicts.Count}");

        return sb.ToString();
    }

    private static string BuildModList(IReadOnlyList<ModInfo> mods)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{mods.Count} mods tracked. [on]/[off] is whether the mod is currently enabled.");
        sb.AppendLine();

        foreach (var m in mods.OrderByDescending(m => m.IsEnabled).ThenBy(m => m.Name))
        {
            sb.AppendLine($"{(m.IsEnabled ? "[on] " : "[off]")} {m.Name}  ({m.Type})");
            if (!string.IsNullOrWhiteSpace(m.InstalledVersion)) sb.AppendLine($"        version   {m.InstalledVersion}");
            if (!string.IsNullOrWhiteSpace(m.ModUpdateUrl)) sb.AppendLine($"        updates   {m.ModUpdateUrl}");
            if (m.UpdateUrlChanged) sb.AppendLine($"        WARNING   update address changed since install (was {m.InstalledUpdateUrl})");
            if (m.IsPartOfSet) sb.AppendLine($"        parts     {m.LinkedPartCount} (installs in more than one place)");
            if (!string.IsNullOrWhiteSpace(m.Notes)) sb.AppendLine($"        note      {m.Notes}");
            sb.AppendLine($"        files     {m.InstallFiles.Count} at {m.InstallPath}");
        }

        return sb.ToString();
    }

    private static string BuildConflicts(IReadOnlyList<ModConflictGroup> conflicts)
    {
        if (conflicts.Count == 0) return "No conflicts detected.";

        var sb = new StringBuilder();
        sb.AppendLine($"{conflicts.Count} conflict(s).");
        sb.AppendLine();

        foreach (var c in conflicts)
        {
            sb.AppendLine($"[{c.Severity}] {c.Kind}");
            sb.AppendLine($"  {c.ModNamesDisplay}");
            sb.AppendLine($"  {c.Summary}");
            foreach (var path in c.AssetPaths.Take(20)) sb.AppendLine($"    {path}");
            if (c.AssetPaths.Count > 20) sb.AppendLine($"    ...and {c.AssetPaths.Count - 20} more");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static void AddRecentLogs(ZipArchive zip)
    {
        try
        {
            var folder = AppSettingsService.Instance.GetLogsFolder();
            if (!Directory.Exists(folder)) return;

            var logs = new DirectoryInfo(folder).GetFiles("*.txt")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(LogsToInclude);

            foreach (var log in logs)
            {
                // Copied through a stream rather than CreateEntryFromFile: the current session's
                // log is open for writing, and CreateEntryFromFile would fail on it.
                var entry = zip.CreateEntry("logs/" + log.Name, CompressionLevel.Optimal);
                using var source = new FileStream(log.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var target = entry.Open();
                source.CopyTo(target);
            }
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Warn($"Couldn't include the logs in the bundle: {ex.Message}");
        }
    }
}
