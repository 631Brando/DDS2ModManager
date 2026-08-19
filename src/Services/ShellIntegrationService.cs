using System.Diagnostics;
using Microsoft.Win32;

namespace DDS2ModManager.Services;

/// Adds/removes a right-click "Open with ..." entry for .zip/.7z/.rar files.
/// Writes only to HKEY_CURRENT_USER\Software\Classes, so no admin elevation is required,
/// and it's purely additive - it never changes the default double-click handler for these
/// extensions (whatever that's currently set to, e.g. File Explorer's zip support, 7-Zip, etc).
public static class ShellIntegrationService
{
    /// The registry KEY, deliberately unchanged by the rename. An existing registration lives under
    /// this name; changing it would orphan that entry - leaving a second "Open with..." in the
    /// context menu that nothing in this app can find or remove. Only the label the user reads is
    /// renamed, and Register() rewrites it in place.
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
            shellKey.SetValue("", $"Open with {AppPaths.AppDisplayName}");
            shellKey.SetValue("Icon", $"\"{exePath}\"");

            using var commandKey = shellKey.CreateSubKey("command");
            commandKey.SetValue("", $"\"{exePath}\" \"%1\"");
        }

        LoggingService.Instance.Success(
            $"Added 'Open with {AppPaths.AppDisplayName}' to the right-click menu for .zip/.7z/.rar files.");
    }

    public static void Unregister()
    {
        foreach (var ext in Extensions)
        {
            try { Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{ext}\shell\{VerbName}", throwOnMissingSubKey: false); }
            catch (Exception ex) { LoggingService.Instance.Warn($"Couldn't remove context menu entry for {ext}: {ex.Message}"); }
        }

        LoggingService.Instance.Info($"Removed 'Open with {AppPaths.AppDisplayName}' from the right-click menu.");
    }
}
