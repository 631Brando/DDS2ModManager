namespace DDS2ModManager.Services;

/// Handles detecting, installing and updating UE4SS from the experimental-latest
/// release tag. We deliberately never touch the stable release channel - the user's
/// game needs the experimental build specifically.
public class UE4SSManagerService
{
    private const string Owner = "UE4SS-RE";
    private const string Repo = "RE-UE4SS";
    private const string Tag = "experimental-latest";

    private readonly GitHubReleaseService _github = new();

    public Task<GitHubReleaseInfo?> GetLatestExperimentalReleaseAsync() =>
        _github.GetReleaseByTagAsync(Owner, Repo, Tag);

    /// The release always ships 6 assets: the real UE4SS_v*.zip, zCustomGameConfigs.zip,
    /// zDEV-UE4SS_v*.zip, zMapGenBP.zip, and two source archives. This is the standard build -
    /// starts with "UE4SS_" (not "z...") and ends in .zip. No console window opens with this one.
    public GitHubAsset? FindMainAsset(GitHubReleaseInfo release) =>
        release.Assets.FirstOrDefault(a =>
            a.Name.StartsWith("UE4SS_", StringComparison.OrdinalIgnoreCase) &&
            a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

    /// The "zDEV-UE4SS_v*.zip" asset - functionally identical for mods, but opens a console
    /// window showing live UE4SS logs while the game runs. The build picker (shown before every
    /// install/update) is what tells the user about that difference - this method just finds it.
    public GitHubAsset? FindDevAsset(GitHubReleaseInfo release) =>
        release.Assets.FirstOrDefault(a =>
            a.Name.StartsWith("zDEV-UE4SS_", StringComparison.OrdinalIgnoreCase) &&
            a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

    public GitHubAsset? FindAsset(GitHubReleaseInfo release, bool devBuild) =>
        devBuild ? FindDevAsset(release) : FindMainAsset(release);

    public UE4SSInstallInfo GetCurrentStatus(GameInstallation game)
    {
        var info = new UE4SSInstallInfo();
        var dwmapi = Path.Combine(game.Win64Path, "dwmapi.dll");
        info.IsInstalled = File.Exists(dwmapi) && Directory.Exists(game.UE4SSRootPath);

        var manifestPath = GetManifestPath(game);
        if (File.Exists(manifestPath))
        {
            try
            {
                var manifest = JsonSerializer.Deserialize<UE4SSManifest>(File.ReadAllText(manifestPath));
                if (manifest != null)
                {
                    info.IsManagedByUs = true;
                    info.IsConfirmedExperimental = true;
                    info.InstalledVersionTag = manifest.InstalledTag;
                    info.InstalledAssetName = manifest.InstalledAssetName;
                    info.InstalledAt = manifest.InstalledAt;
                }
            }
            catch { /* corrupt manifest - treat as unmanaged */ }
        }

        return info;
    }

    private string GetManifestPath(GameInstallation game) =>
        Path.Combine(game.UE4SSRootPath, ".dds2modmanager_manifest.json");

    public async Task<bool> InstallOrUpdateAsync(GameInstallation game, GitHubReleaseInfo release, GitHubAsset asset,
        IProgress<double>? progress = null)
    {
        var log = LoggingService.Instance;
        var tempDir = Path.Combine(Path.GetTempPath(), "DDS2MM_UE4SS_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var zipPath = Path.Combine(tempDir, asset.Name);

        try
        {
            log.Info($"Downloading {asset.Name} ({asset.Size / 1024.0 / 1024.0:F1} MB)...");
            await _github.DownloadAssetAsync(asset.BrowserDownloadUrl, zipPath, progress);
            log.Success("Download complete. Extracting...");

            var extractDir = Path.Combine(tempDir, "extracted");
            ArchiveExtractionService.ExtractToDirectory(zipPath, extractDir);

            var dwmapiSrc = Path.Combine(extractDir, "dwmapi.dll");
            var ue4ssSrc = Path.Combine(extractDir, "ue4ss");

            if (!File.Exists(dwmapiSrc) || !Directory.Exists(ue4ssSrc))
            {
                log.Error("Downloaded archive layout didn't match the expected dwmapi.dll + ue4ss\\ structure. " +
                           "UE4SS-RE may have changed the release layout - install manually and report this.");
                return false;
            }

            Directory.CreateDirectory(game.Win64Path);
            File.Copy(dwmapiSrc, Path.Combine(game.Win64Path, "dwmapi.dll"), true);

            // Never clobber the user's existing mods.txt / mods.json when updating.
            var preserve = new List<string> { Path.Combine("Mods", "mods.txt"), Path.Combine("Mods", "mods.json") };

            // ...and don't discard settings the user edited through Saves & Config either. Keyed on
            // the backup this manager takes the first time a file is saved, so it means "the user
            // changed this", not "this file exists".
            //
            // That distinction is the point. Preserving every settings file unconditionally would
            // pin people to an old default forever and hide new options a UE4SS release adds;
            // preserving none silently threw away their work. Only the ones actually edited are
            // worth keeping, and everyone else gets the new version.
            foreach (var ini in SafeEnumerateIni(game.UE4SSRootPath))
            {
                if (File.Exists(ini + GameConfigService.BackupSuffix))
                    preserve.Add(Path.GetFileName(ini));
            }

            CopyDirectoryPreserving(ue4ssSrc, game.UE4SSRootPath, preserve.ToArray());

            var manifest = new UE4SSManifest
            {
                InstalledTag = release.TagName,
                InstalledAssetName = asset.Name,
                InstalledAt = DateTime.Now
            };
            File.WriteAllText(GetManifestPath(game), JsonSerializer.Serialize(manifest));

            log.Success($"UE4SS ({asset.Name}) installed/updated successfully.");
            return true;
        }
        catch (Exception ex)
        {
            log.Error($"UE4SS install failed: {ex.Message}");
            return false;
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    /// Top-level .ini files in an existing UE4SS folder. Returns nothing rather than throwing when
    /// the folder is missing or unreadable - this only decides what to preserve, and a first-time
    /// install has nothing to preserve anyway.
    private static IEnumerable<string> SafeEnumerateIni(string ue4ssRoot)
    {
        try
        {
            return Directory.Exists(ue4ssRoot)
                ? Directory.GetFiles(ue4ssRoot, "*.ini", SearchOption.TopDirectoryOnly)
                : Enumerable.Empty<string>();
        }
        catch
        {
            return Enumerable.Empty<string>();
        }
    }

    private void CopyDirectoryPreserving(string source, string dest, string[] preserveRelativePaths)
    {
        Directory.CreateDirectory(dest);
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(dest, Path.GetRelativePath(source, dir)));

        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(source, file);
            var target = Path.Combine(dest, rel);

            if (preserveRelativePaths.Contains(rel, StringComparer.OrdinalIgnoreCase) && File.Exists(target))
                continue; // keep the user's existing file

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }
    }

}
