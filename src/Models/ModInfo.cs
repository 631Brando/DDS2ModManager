using CommunityToolkit.Mvvm.ComponentModel;

namespace DDS2ModManager.Models;

/// ObservableObject (not a plain POCO) is required here: the DataGrid's Enable/Disable
/// buttons and the Status column are bound directly to properties on this class. Without
/// INotifyPropertyChanged, mutating mod.IsEnabled after install does nothing visible -
/// the UI has no way to know the value changed.
public partial class ModInfo : ObservableObject
{
    [ObservableProperty] private string id = Guid.NewGuid().ToString("N");
    [ObservableProperty] private string name = "";
    [ObservableProperty] private ModType type;
    [ObservableProperty] private bool isEnabled = true;
    [ObservableProperty] private bool isInstalled;

    /// Where the mod originally came from (archive path or source folder) - kept for reference.
    [ObservableProperty] private string sourcePath = "";

    /// Folder the mod's files currently live in (active game folder OR the disabled-cache folder).
    [ObservableProperty] private string installPath = "";

    /// Absolute paths of the actual .pak/.ucas/.utoc (or lua folder) files currently on disk for this mod.
    /// Used so Disable/Enable/Uninstall know exactly what to move or delete.
    [ObservableProperty] private List<string> installFiles = new();

    /// Virtual asset paths found inside the mod's pak (via CUE4Parse) - used for conflict detection
    /// and the "View Files" tree. For lua mods this is the list of real relative file paths instead.
    [ObservableProperty] private List<string> containedAssetPaths = new();

    [ObservableProperty] private bool hasModActor;
    [ObservableProperty] private DateTime installedAt = DateTime.Now;
}
