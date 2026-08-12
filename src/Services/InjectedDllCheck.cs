namespace DDS2ModManager.Services;

/// Warns when a mod loader's proxy DLL is sitting next to this executable.
///
/// Windows searches an executable's own folder before System32, which is the whole trick UE4SS
/// uses: name yourself dwmapi.dll, sit next to the game, and the game loads you instead of
/// Windows'. Nothing turns that behaviour off - it applies to every process, including this one.
///
/// So unzipping UE4SS into Downloads and then running the manager from that same folder loads
/// UE4SS *into the manager*. UE4SS then can't find UE4SS.dll, and its hooks end up inside a WPF
/// process, which stops the window drawing: the app starts, finds the game, checks for updates,
/// and shows nothing but a blank rectangle. That is close to impossible to diagnose from the
/// outside, and it has already happened to a real user.
///
/// This can't prevent the load - by the time managed code runs, the DLL is in - but it costs one
/// directory listing and turns a blank window into a sentence naming the cause and the fix.
///
/// The warning goes in a message box rather than the log panel, because the log panel is part of
/// the window that isn't drawing. A message box is drawn by Windows itself, not by WPF, so it
/// still appears when the app's own window is a white rectangle - which is exactly how the user
/// who hit this saw UE4SS's own "Failed to load UE4SS.dll" error while seeing nothing else.
public static class InjectedDllCheck
{
    /// Filenames mod loaders impersonate so Windows loads them into a game. UE4SS ships as
    /// dwmapi.dll by default; the others are the common alternatives it and similar tools use.
    private static readonly string[] ProxyDllNames =
    {
        "dwmapi.dll", "d3d11.dll", "d3d12.dll", "d3d9.dll", "dinput8.dll",
        "xinput1_3.dll", "version.dll", "winmm.dll", "dsound.dll", "bink2w64.dll"
    };

    public static void Run()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            var dir = Path.GetDirectoryName(exePath);
            if (string.IsNullOrEmpty(dir)) return;

            var found = ProxyDllNames
                .Where(name => File.Exists(Path.Combine(dir, name)))
                .ToList();
            if (found.Count == 0) return;

            var names = string.Join(", ", found);
            var exeName = Path.GetFileName(exePath) ?? "the mod manager";

            LoggingService.Instance.Warn(
                $"Found {names} in this app's folder. Windows loads DLLs from a program's own folder before its " +
                "own, so these get loaded into the mod manager instead of the game - UE4SS ships as dwmapi.dll, " +
                $"and once it's pulled in here it can stop this window drawing properly. Move {exeName} into a " +
                "folder of its own and run it from there.");

            System.Windows.MessageBox.Show(
                $"{names} was found in the same folder as {exeName}:\n\n{dir}\n\n" +
                "That's a mod loader - UE4SS installs itself as dwmapi.dll. Windows loads DLLs from a program's " +
                "own folder before its own, so it's being loaded into the mod manager instead of into the game. " +
                "When that happens this window usually opens completely blank.\n\n" +
                $"To fix it: move {exeName} into a folder of its own, away from the UE4SS files, and run it from " +
                "there. Nothing needs uninstalling.",
                "DDS2 Mod Manager - wrong folder",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            // A diagnostic must never be the reason startup fails.
            LoggingService.Instance.Warn($"Couldn't check for mod loader DLLs alongside the app: {ex.Message}");
        }
    }
}
