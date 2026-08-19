using CommunityToolkit.Mvvm.ComponentModel;

namespace DDS2ModManager.Models;

/// One save slot. Unreal games store these either as a folder per save (DDS2 does this - each
/// cartel gets its own folder under SaveGames\Cartels) or as a single loose .sav file, so this
/// covers both rather than assuming one shape.
public partial class SaveEntry : ObservableObject
{
    [ObservableProperty] private string name = "";
    [ObservableProperty] private bool isEnabled = true;

    /// Full path to the save's folder, or to the file itself for single-file saves.
    public string Path { get; set; } = "";

    public bool IsFolder { get; set; }
    public long SizeBytes { get; set; }
    public DateTime LastModified { get; set; }
    public int FileCount { get; set; }

    /// For folder saves nested under a container (e.g. "Cartels"), the container's name - shown
    /// in the UI so two saves with the same name in different containers stay distinguishable.
    public string? GroupName { get; set; }

    /// The Saved-relative folder this save lives in, when it is NOT the game's primary save root.
    ///
    /// Empty for the ordinary case, which keeps every save written before multi-root support
    /// resolving exactly as it did. It is non-empty only for a game that keeps saves in more than
    /// one place - DDS1 puts a slot index in Saved\SaveGames but the actual playthroughs in
    /// Saved\Serialized - and it is what makes re-enabling put a save back where it came from
    /// rather than into the primary root, where the game would never look for it.
    public string RootName { get; set; } = "";

    public string SizeDisplay => SizeBytes switch
    {
        < 1024 => $"{SizeBytes} B",
        < 1024 * 1024 => $"{SizeBytes / 1024.0:F1} KB",
        _ => $"{SizeBytes / 1024.0 / 1024.0:F1} MB"
    };

    public string LastModifiedDisplay => LastModified.ToString("yyyy-MM-dd HH:mm");

    public string KindDisplay => IsFolder ? $"{FileCount} file(s)" : "single file";

    /// Falls back to the root for a save that has no container, so DDS1's two save roots are
    /// distinguishable in the list - "Serialized" holds the playthroughs and "SaveGames" only a
    /// slot index, and a user needs to see which is which before deleting one.
    public string GroupDisplay =>
        !string.IsNullOrEmpty(GroupName) ? GroupName : RootName;
}
