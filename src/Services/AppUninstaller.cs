namespace DDS2ModManager.Services;

/// Removes this installed copy of the app: shortcuts, the right-click context menu entry, and
/// the "Apps & Features" registry entry, then deletes the install directory itself (via
/// SelfReplaceHelper, since a running exe can't delete its own file). Deliberately leaves
/// %AppData%\DDS2ModManager alone - settings, mod tracking, logs, and any files cached under
/// DisabledMods are real user data / recovery state, not install artifacts, so they survive an
/// uninstall the same way most Windows apps leave user data behind unless asked to wipe it too
/// (see AppSettingsService.ResetAllAppData for that separate, explicit action).
public static class AppUninstaller
{
    private const string UninstallKeyName = "DDS2ModManager";

    public static void Run()
    {
        var log = LoggingService.Instance;

        try { ShellIntegrationService.Unregister(); } catch (Exception ex) { log.Warn($"Couldn't remove context menu entry: {ex.Message}"); }
        try { ShortcutService.Uninstall(); } catch (Exception ex) { log.Warn($"Couldn't remove Start Menu shortcut: {ex.Message}"); }
        try { ShortcutService.UninstallDesktop(); } catch (Exception ex) { log.Warn($"Couldn't remove Desktop shortcut: {ex.Message}"); }

        try
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(
                $@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{UninstallKeyName}", throwOnMissingSubKey: false);
        }
        catch (Exception ex)
        {
            log.Warn($"Couldn't remove the Apps & Features entry: {ex.Message}");
        }

        var installDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
        log.Success($"Uninstalling - {installDir} will be removed once the app closes.");
        SelfReplaceHelper.DeleteDirectoryAfterExit(installDir);
    }
}
