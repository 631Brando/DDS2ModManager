namespace DDS2ModManager.Services;

/// Save handling that only makes sense for Drug Dealer Simulator 2.
///
/// Everything else in SaveGameService is deliberately game-agnostic - it moves whole saves around
/// and, when cloning, updates strings that literally spell out the save's old name. That generic
/// rule can't know which field a game shows in its load menu, so it leaves it alone.
///
/// This class is where knowledge of DDS2's specific save layout lives, kept in one place and
/// applied only when the game really is DDS2. Point the manager at another Unreal title and none
/// of it runs, so a differently-shaped save can't be edited on a wrong assumption.
public static class Dds2SaveRules
{
    /// The Unreal project folder name, which is what identifies the game on disk.
    private const string Dds2ProjectName = "DrugDealerSimulator2";

    /// The cartel's display name - what the game's load menu shows.
    private const string DisplayNameProperty = "CartelCustomName";

    /// The name the game uses to find "&lt;name&gt;_Progress.save" inside the cartel's folder.
    private const string SaveNameProperty = "CartelSaveName";

    /// The per-cartel file holding both the display name and the name used to find the save data.
    private const string CartelDefaultsFile = "CartelDefaults.sav";

    /// True only when the install really is DDS2.
    ///
    /// Both halves matter. GameInstallation.ProjectName falls back to DDS2's name when it can't
    /// detect anything, so the name alone would also match an unrecognised install of some other
    /// game; requiring a valid install means the name was actually read off disk.
    public static bool Applies(GameInstallation? game) =>
        game is { IsValid: true } &&
        string.Equals(game.ProjectName, Dds2ProjectName, StringComparison.OrdinalIgnoreCase);

    /// Gives a freshly cloned cartel its own name in the game's load menu.
    ///
    /// DDS2 stores two names per cartel: CartelSaveName, which locates the progress file and is
    /// already handled by the generic self-reference pass, and CartelCustomName, which is the
    /// label you see in game. The two are usually identical, but not always - the game strips
    /// characters that aren't legal in a folder name, so a cartel called "8/1/26 Run" is stored
    /// as "8126Run". When they differ, the generic pass has nothing to match and the copy shows
    /// up under the original's name, which makes the two impossible to tell apart.
    ///
    /// The clone gets the name the user typed, which is what they just asked for.
    public static void OnSaveCloned(GameInstallation? game, string clonedFolder, string newName)
    {
        if (!Applies(game)) return;

        var defaults = Path.Combine(clonedFolder, CartelDefaultsFile);
        if (!File.Exists(defaults) || !GvasNameRewriter.IsGvasSave(defaults)) return;

        if (GvasNameRewriter.SetStringProperty(defaults, DisplayNameProperty, newName))
            LoggingService.Instance.Info($"Set the copy's in-game name to '{newName}'.");
    }

    /// Returns why a cartel folder wouldn't load, or null if it looks right.
    ///
    /// Every working cartel satisfies the same rule: the folder name, the CartelSaveName recorded
    /// inside CartelDefaults.sav, and the "&lt;name&gt;_Progress.save" file all agree. Break any part of
    /// it and the game finds nothing and quietly skips the cartel with no error - which is exactly
    /// how the old clone bug went unnoticed.
    public static string? DescribeCloneProblem(string folder, string expectedName)
    {
        var defaults = Path.Combine(folder, CartelDefaultsFile);
        if (!File.Exists(defaults)) return null;   // not a shape we know; nothing to claim

        var recorded = GvasNameRewriter.ReadStringProperty(defaults, SaveNameProperty);
        if (recorded == null) return null;

        if (!string.Equals(recorded, expectedName, StringComparison.Ordinal))
            return $"it still calls itself '{recorded}' inside {CartelDefaultsFile}.";

        var progress = Path.Combine(folder, $"{recorded}_Progress.save");
        if (File.Exists(progress)) return null;

        // A cartel with no progress file at all is simply one that was created and never played -
        // the game writes the folder up front and the progress file on the first save. Only
        // complain when progress data is there but under a name the game won't look for.
        var stray = Directory.GetFiles(folder, "*_Progress.save").FirstOrDefault();
        if (stray == null) return null;

        return $"its progress file is {Path.GetFileName(stray)}, but it will look for {Path.GetFileName(progress)}.";
    }
}
