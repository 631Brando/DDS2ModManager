using System.Diagnostics;
using Microsoft.Win32;

namespace DDS2ModManager.Services;

/// Adds/removes a right-click "Open with DDS2 Mod Manager" entry for .zip/.7z/.rar files.
/// Writes only to HKEY_CURRENT_USER\Software\Classes, so no admin elevation is required,
/// and it's purely additive - it never changes the default double-click handler for these
/// extensions (whatever that's currently set to, e.g. File Explorer's zip support, 7-Zip, etc).
public static class ShellIntegrationService
{
    private const string VerbName = "DDS2ModManager";
    private static readonly string[] Extensions = { ".zip", ".7z", ".rar" };

    public static string GetExePath() =>
        Process.GetCurrentProcess().MainModule?.FileName
        ?? Path.Combine(AppContext.BaseDirectory, "DDS2ModManager.exe");

    public static bool IsRegistered()
    {
        foreach (var ext in Extensions)
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{ext}\shell\{VerbName}");
            if (key == null) return false;
        }
        return true;
    }

    public static void Register()
    {
        var exePath = GetExePath();

        foreach (var ext in Extensions)
        {
            using var shellKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ext}\shell\{VerbName}");
            shellKey.SetValue("", "Open with DDS2 Mod Manager");
            shellKey.SetValue("Icon", $"\"{exePath}\"");

            using var commandKey = shellKey.CreateSubKey("command");
            commandKey.SetValue("", $"\"{exePath}\" \"%1\"");
        }

        LoggingService.Instance.Success("Added 'Open with DDS2 Mod Manager' to the right-click menu for .zip/.7z/.rar files.");
    }

    public static void Unregister()
    {
        foreach (var ext in Extensions)
        {
            try { Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{ext}\shell\{VerbName}", throwOnMissingSubKey: false); }
            catch (Exception ex) { LoggingService.Instance.Warn($"Couldn't remove context menu entry for {ext}: {ex.Message}"); }
        }

        LoggingService.Instance.Info("Removed 'Open with DDS2 Mod Manager' from the right-click menu.");
    }
}
