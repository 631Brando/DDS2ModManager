using System.Diagnostics;
using Microsoft.Win32;

namespace DDS2ModManager.Services;

/// What Steam Cloud is doing with the game's saves.
///
/// This matters because the manager edits saves on disk while Steam is separately syncing that
/// same folder. DDS2 uses Steam Auto-Cloud pointed straight at
/// %LocalAppData%\DrugDealerSimulator2\Saved\SaveGames, so deleting or moving a save here isn't
/// the whole story: Steam reconciles the folder against the cloud when the game next starts or
/// stops. A deleted save can come back, and a save moved out of the folder (which is how Disable
/// works) can be removed from the cloud, and therefore from other machines.
///
/// None of that is something this app should silently work around - Steam owns that sync, and
/// fighting it would be worse than saying so. So this only *reports*: it never edits Steam's
/// configuration, which Steam would overwrite anyway while it's running.
public class SteamCloudService
{
    private readonly GameInstallation _game;

    public SteamCloudService(GameInstallation game) => _game = game;

    public SteamCloudStatus GetStatus()
    {
        var status = new SteamCloudStatus();

        try
        {
            status.SteamRunning = Process.GetProcessesByName("steam").Length > 0;

            var manifest = FindAppManifest();
            if (manifest == null) return status;    // not a Steam copy, or an unusual layout

            status.AppId = manifest.Value.AppId;
            status.AppName = manifest.Value.Name;
            status.IsSteamInstall = true;

            var steamRoot = FindSteamRoot();
            if (steamRoot == null) return status;

            // userdata lives under the main Steam install even when the game is on another drive.
            var accountId = ToAccountId(manifest.Value.OwnerSteamId);
            var userDir = FindUserDirectory(steamRoot, accountId, status.AppId);
            if (userDir == null) return status;

            status.SyncedFileCount = CountSyncedSaveFiles(Path.Combine(userDir, status.AppId.ToString(), "remotecache.vdf"));
            status.CloudEnabledForApp = ReadCloudEnabled(Path.Combine(userDir, "config", "localconfig.vdf"), status.AppId);
        }
        catch (Exception ex)
        {
            // Never let a Steam layout we don't recognise break the saves window.
            LoggingService.Instance.Warn($"Couldn't determine Steam Cloud status: {ex.Message}");
        }

        return status;
    }

    /// The appmanifest sitting beside the game's own install folder - that's the one that names
    /// this copy, even if the library is on a different drive from Steam itself.
    private (int AppId, string Name, string OwnerSteamId)? FindAppManifest()
    {
        var common = Path.GetDirectoryName(_game.RootPath.TrimEnd(Path.DirectorySeparatorChar));
        var steamApps = common == null ? null : Path.GetDirectoryName(common);
        if (steamApps == null || !Directory.Exists(steamApps)) return null;

        var installDir = Path.GetFileName(_game.RootPath.TrimEnd(Path.DirectorySeparatorChar));

        foreach (var file in Directory.GetFiles(steamApps, "appmanifest_*.acf"))
        {
            var vdf = SteamVdf.Load(file)?.Find("AppState");
            if (vdf == null) continue;
            if (!string.Equals(vdf.ValueOf("installdir"), installDir, StringComparison.OrdinalIgnoreCase)) continue;
            if (!int.TryParse(vdf.ValueOf("appid"), out var appId)) continue;

            return (appId, vdf.ValueOf("name") ?? installDir, vdf.ValueOf("LastOwner") ?? "");
        }

        return null;
    }

    private static string? FindSteamRoot()
    {
        try
        {
            var path = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")?.GetValue("SteamPath") as string;
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path)) return path;
        }
        catch { /* fall through to the default location */ }

        var fallback = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        return Directory.Exists(fallback) ? fallback : null;
    }

    /// Steam's userdata folders are keyed by the 32-bit account id, not the full 64-bit id.
    private static string? ToAccountId(string steamId64) =>
        ulong.TryParse(steamId64, out var id) && id > 76561197960265728
            ? (id - 76561197960265728).ToString()
            : null;

    /// Prefers the account that owns this copy; falls back to any user with data for the app, so
    /// a shared machine still reports something useful.
    private static string? FindUserDirectory(string steamRoot, string? accountId, int appId)
    {
        var userData = Path.Combine(steamRoot, "userdata");
        if (!Directory.Exists(userData)) return null;

        if (accountId != null)
        {
            var owned = Path.Combine(userData, accountId);
            if (Directory.Exists(Path.Combine(owned, appId.ToString()))) return owned;
        }

        return Directory.GetDirectories(userData)
            .FirstOrDefault(d => Directory.Exists(Path.Combine(d, appId.ToString())));
    }

    /// remotecache.vdf lists every file Steam is syncing for the app. Counting the ones under
    /// SaveGames is what proves the cloud is actually covering the folder this app edits, rather
    /// than assuming it from the game having cloud support at all.
    private static int CountSyncedSaveFiles(string remoteCachePath)
    {
        if (!File.Exists(remoteCachePath)) return 0;

        var root = SteamVdf.Load(remoteCachePath);
        var app = root?.Children.Values.FirstOrDefault();
        if (app == null) return 0;

        return app.Children.Keys.Count(k =>
            k.Contains("SaveGames", StringComparison.OrdinalIgnoreCase));
    }

    /// Steam only writes "cloudenabled" when the user has turned it off for an app, so a missing
    /// key means enabled. Reported as a nullable so "we couldn't tell" stays distinct from "on".
    private static bool? ReadCloudEnabled(string localConfigPath, int appId)
    {
        if (!File.Exists(localConfigPath)) return null;

        var root = SteamVdf.Load(localConfigPath);
        var app = root?.Find("UserLocalConfigStore", "Software", "Valve", "Steam", "apps", appId.ToString())
                  ?? root?.Find("Software", "Valve", "Steam", "apps", appId.ToString());
        if (app == null) return null;

        var flag = app.ValueOf("cloudenabled");
        return flag == null ? true : flag != "0";
    }
}

/// A snapshot of how Steam Cloud relates to this game's saves.
public class SteamCloudStatus
{
    public bool IsSteamInstall { get; set; }
    public int AppId { get; set; }
    public string AppName { get; set; } = "";
    public bool SteamRunning { get; set; }

    /// How many files under the save folder Steam has on record as synced.
    public int SyncedFileCount { get; set; }

    /// Null when it couldn't be determined.
    public bool? CloudEnabledForApp { get; set; }

    /// True when Steam is demonstrably syncing this game's save folder - i.e. it has actually
    /// synced files from it and the user hasn't switched cloud off for the game.
    public bool IsSyncingSaves => SyncedFileCount > 0 && CloudEnabledForApp != false;

    private string GameLabel => AppName.Length > 0 ? AppName : "the game";

    public string Headline => "Steam Cloud is syncing these saves - your changes here may not survive the next launch.";

    /// Leads with the point that catches people out: this isn't only about deletions. Steam
    /// reconciles the whole folder in both directions, so a clone can vanish and an edit can be
    /// rolled back, with no warning from the game.
    public string Detail =>
        $"Steam has {SyncedFileCount:N0} files from this folder in the cloud and reconciles the whole folder each " +
        $"time {GameLabel} starts or stops. It can copy either way, so anything you change here - cloning, renaming, " +
        "deleting, or editing a save - can be undone when you next launch: a new copy can disappear, and a deleted " +
        "save can come back. Disabling is the riskiest, because it moves the save out of the folder and Steam can " +
        "read that as a deletion, dropping it from the cloud and from your other machines.";

    /// What to actually do about it, in the order it should be done.
    public string HowToDisable =>
        "To make changes that stick: close the game, then Steam → Library → right-click " +
        $"{GameLabel} → Properties → General → untick \"Keep game saves in the Steam Cloud\". " +
        "Either way, Back Up first - backups are kept outside the synced folder, so Steam never touches them.";

    /// Short form for dialogs, where the full banner would bury the actual question.
    public string ShortWarning =>
        $"Steam Cloud is syncing this folder. It reconciles with the cloud when {GameLabel} next starts, " +
        "so this change can be undone. Turn Steam Cloud off for the game first if you want it to stick.";
}
