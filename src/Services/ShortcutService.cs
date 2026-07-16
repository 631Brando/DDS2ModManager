using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace DDS2ModManager.Services;

/// Creates/removes a Start Menu shortcut using the classic IShellLink COM interface.
/// No extra NuGet package needed - this is the same low-level API Windows Explorer itself
/// uses to create .lnk files.
public static class ShortcutService
{
    private const string ShortcutName = "DDS2 Mod Manager.lnk";

    private static string ShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", ShortcutName);

    public static bool IsInstalled() => File.Exists(ShortcutPath);

    public static void Install()
    {
        var exePath = ShellIntegrationService.GetExePath();
        var workingDir = Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory;

        var link = (IShellLinkW)new ShellLink();
        link.SetPath(exePath);
        link.SetWorkingDirectory(workingDir);
        link.SetDescription("DDS2 Mod Manager");
        link.SetIconLocation(exePath, 0);

        ((IPersistFile)link).Save(ShortcutPath, false);

        LoggingService.Instance.Success("Added a Start Menu shortcut.");
    }

    public static void Uninstall()
    {
        try
        {
            if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath);
            LoggingService.Instance.Info("Removed the Start Menu shortcut.");
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Warn($"Couldn't remove Start Menu shortcut: {ex.Message}");
        }
    }

    // --- COM interop plumbing for IShellLinkW / IPersistFile ---

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, out IntPtr pfd, int fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
        void Resolve(IntPtr hwnd, int fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }
}
