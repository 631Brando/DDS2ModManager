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

    public string SizeDisplay => SizeBytes switch
    {
        < 1024 => $"{SizeBytes} B",
        < 1024 * 1024 => $"{SizeBytes / 1024.0:F1} KB",
        _ => $"{SizeBytes / 1024.0 / 1024.0:F1} MB"
    };

    public string LastModifiedDisplay => LastModified.ToString("yyyy-MM-dd HH:mm");

    public string KindDisplay => IsFolder ? $"{FileCount} file(s)" : "single file";

    public string GroupDisplay => string.IsNullOrEmpty(GroupName) ? "" : GroupName;
}
