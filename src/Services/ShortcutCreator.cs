using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace DDS2ModManager.Services;

/// Pure .lnk creation via the classic IShellLinkW COM interface - the same low-level API
/// Windows Explorer itself uses. Deliberately has zero dependencies on the rest of this app
/// (no LoggingService, no path conventions) so this single file can be linked directly into
/// the separate Setup project without dragging in the main app's dependency graph.
public static class ShortcutCreator
{
    public static void Create(string shortcutPath, string targetExePath, string? description = null, string? workingDirectory = null)
    {
        var link = (IShellLinkW)new ShellLink();
        link.SetPath(targetExePath);
        link.SetWorkingDirectory(workingDirectory ?? Path.GetDirectoryName(targetExePath) ?? "");
        if (description != null) link.SetDescription(description);
        link.SetIconLocation(targetExePath, 0);

        ((IPersistFile)link).Save(shortcutPath, false);
    }

    public static bool Exists(string shortcutPath) => File.Exists(shortcutPath);

    public static void Delete(string shortcutPath)
    {
        if (File.Exists(shortcutPath)) File.Delete(shortcutPath);
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
