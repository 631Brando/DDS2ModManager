namespace DDS2ModManager.Services;

/// Creates/removes Start Menu and Desktop shortcuts for this app. The actual .lnk creation is
/// in ShortcutCreator (kept dependency-free so the Setup project can share it); this class just
/// supplies this app's own paths/name and hooks up logging.
public static class ShortcutService
{
    private const string ShortcutName = AppPaths.AppDisplayName + ".lnk";

    /// What shortcuts were called before the app was renamed.
    ///
    /// Still looked for, and removed whenever a new one is written. Without this a rename leaves a
    /// second shortcut in the Start Menu pointing at the same exe under the old name, and
    /// IsInstalled() reports "no shortcut" to a user who can plainly see one.
    private const string LegacyShortcutName = "DDS2 Mod Manager.lnk";

    private static string LegacyStartMenuPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", LegacyShortcutName);

    private static string LegacyDesktopPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), LegacyShortcutName);

    private static void RemoveQuietly(string path)
    {
        try { ShortcutCreator.Delete(path); } catch { /* nothing there, or not ours to remove */ }
    }

    private static string StartMenuShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", ShortcutName);

    private static string DesktopShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ShortcutName);

    public static bool IsInstalled() =>
        ShortcutCreator.Exists(StartMenuShortcutPath) || ShortcutCreator.Exists(LegacyStartMenuPath);
    public static bool IsDesktopInstalled() =>
        ShortcutCreator.Exists(DesktopShortcutPath) || ShortcutCreator.Exists(LegacyDesktopPath);

    public static void Install()
    {
        ShortcutCreator.Create(StartMenuShortcutPath, ShellIntegrationService.GetExePath(), AppPaths.AppDisplayName);
        RemoveQuietly(LegacyStartMenuPath);
        LoggingService.Instance.Success("Added a Start Menu shortcut.");
    }

    public static void Uninstall()
    {
        try
        {
            ShortcutCreator.Delete(StartMenuShortcutPath);
            RemoveQuietly(LegacyStartMenuPath);
            LoggingService.Instance.Info("Removed the Start Menu shortcut.");
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Warn($"Couldn't remove Start Menu shortcut: {ex.Message}");
        }
    }

    public static void InstallDesktop()
    {
        ShortcutCreator.Create(DesktopShortcutPath, ShellIntegrationService.GetExePath(), AppPaths.AppDisplayName);
        RemoveQuietly(LegacyDesktopPath);
        LoggingService.Instance.Success("Added a Desktop shortcut.");
    }

    public static void UninstallDesktop()
    {
        try
        {
            ShortcutCreator.Delete(DesktopShortcutPath);
            RemoveQuietly(LegacyDesktopPath);
            LoggingService.Instance.Info("Removed the Desktop shortcut.");
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Warn($"Couldn't remove Desktop shortcut: {ex.Message}");
        }
    }
}
