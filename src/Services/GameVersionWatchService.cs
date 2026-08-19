using System.Diagnostics;
using System.IO;

namespace DDS2ModManager.Services;

/// Notices that the game itself has been updated since the manager last ran.
///
/// This matters more for DDS2 than it would for a game with a stable modding API. Pak mods
/// replace cooked assets, and a patch that recooks those assets can leave a mod loading against
/// content that no longer matches - which shows up as odd behaviour or a crash, with nothing
/// pointing at the game update as the cause. The mods look fine, because nothing about them
/// changed. What changed was underneath them.
///
/// This does not touch or disable anything. It says the ground moved, once, and leaves the
/// decision to the user - the alternative would be disabling working mods on a guess.
public class GameVersionWatchService
{
    /// The game's shipping executable.
    ///
    /// Derived from the detected project folder rather than hardcoded. It used to be the literal
    /// DDS2 path, which failed in the worst possible way on any other game: Read() returned null,
    /// the caller returned early, and the "the game was patched, check your mods" warning - the
    /// single most useful diagnostic when a mod suddenly breaks - simply never fired, with nothing
    /// anywhere reporting that it had been switched off.
    private static string? FindShippingExe(GameInstallation game)
    {
        if (!Directory.Exists(game.Win64Path)) return null;

        var expected = Path.Combine(game.Win64Path, $"{game.ProjectName}-Win64-Shipping.exe");
        if (File.Exists(expected)) return expected;

        // Fall back to whatever shipping exe is present: the executable is not obliged to be named
        // after the project folder, and finding the wrong-named one still beats finding none.
        return Directory.EnumerateFiles(game.Win64Path, "*-Win64-Shipping.exe").FirstOrDefault();
    }

    /// What the game looked like last time. A version string where one exists, otherwise the
    /// exe's size and write time - plenty of Unreal shipping builds carry no file version at all,
    /// and a missing one must not read as "unchanged forever".
    public record GameStamp(string Version, long Size, DateTime WrittenUtc)
    {
        public bool LooksLike(GameStamp other) =>
            Version == other.Version && Size == other.Size && WrittenUtc == other.WrittenUtc;

        public string Display => string.IsNullOrWhiteSpace(Version) || Version == "0.0.0.0"
            ? WrittenUtc.ToLocalTime().ToString("d MMM yyyy")
            : Version;
    }

    /// Reads the current stamp, or null if the exe isn't where it should be - which happens when
    /// the game folder is wrong, and is not something to report as a game update.
    public static GameStamp? Read(GameInstallation game)
    {
        try
        {
            var exe = FindShippingExe(game);
            if (exe == null) return null;

            var info = new FileInfo(exe);
            var version = FileVersionInfo.GetVersionInfo(exe).FileVersion ?? "";

            return new GameStamp(version, info.Length, info.LastWriteTimeUtc);
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Warn($"Couldn't read the game's version: {ex.Message}");
            return null;
        }
    }
}
