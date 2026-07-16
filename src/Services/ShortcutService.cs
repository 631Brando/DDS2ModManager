namespace DDS2ModManager.Services;

/// Creates/removes Start Menu and Desktop shortcuts for this app. The actual .lnk creation is
/// in ShortcutCreator (kept dependency-free so the Setup project can share it); this class just
/// supplies this app's own paths/name and hooks up logging.
public static class ShortcutService
{
    private const string ShortcutName = "DDS2 Mod Manager.lnk";

    private static string StartMenuShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", ShortcutName);

    private static string DesktopShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ShortcutName);

    public static bool IsInstalled() => ShortcutCreator.Exists(StartMenuShortcutPath);
    public static bool IsDesktopInstalled() => ShortcutCreator.Exists(DesktopShortcutPath);

    public static void Install()
    {
        ShortcutCreator.Create(StartMenuShortcutPath, ShellIntegrationService.GetExePath(), "DDS2 Mod Manager");
        LoggingService.Instance.Success("Added a Start Menu shortcut.");
    }

    public static void Uninstall()
    {
        try
        {
            ShortcutCreator.Delete(StartMenuShortcutPath);
            LoggingService.Instance.Info("Removed the Start Menu shortcut.");
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Warn($"Couldn't remove Start Menu shortcut: {ex.Message}");
        }
    }

    public static void InstallDesktop()
    {
        ShortcutCreator.Create(DesktopShortcutPath, ShellIntegrationService.GetExePath(), "DDS2 Mod Manager");
        LoggingService.Instance.Success("Added a Desktop shortcut.");
    }

    public static void UninstallDesktop()
    {
        try
        {
            ShortcutCreator.Delete(DesktopShortcutPath);
            LoggingService.Instance.Info("Removed the Desktop shortcut.");
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Warn($"Couldn't remove Desktop shortcut: {ex.Message}");
        }
    }
}
