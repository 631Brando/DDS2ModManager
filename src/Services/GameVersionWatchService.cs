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
    /// The game's own executable, relative to the install root.
    private const string GameExeRelative = @"DrugDealerSimulator2\Binaries\Win64\DrugDealerSimulator2-Win64-Shipping.exe";

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
            var exe = Path.Combine(game.RootPath, GameExeRelative);
            if (!File.Exists(exe)) return null;

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
