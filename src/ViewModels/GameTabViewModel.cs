using CommunityToolkit.Mvvm.ComponentModel;

namespace DDS2ModManager.ViewModels;

/// One game's tab in the strip at the top of the window.
///
/// Every supported game gets one, installed or not. A strip that hides itself when only one game is
/// present would be invisible to most users, which is the opposite of the "clear discriminator"
/// this is for - and a game that isn't installed is exactly the case where a user needs to be told
/// so, rather than left wondering whether the manager supports it at all.
public partial class GameTabViewModel : ObservableObject
{
    public required GameProfile Profile { get; init; }

    /// The detected install, or null when this game isn't installed (or hasn't been found yet).
    [ObservableProperty] private GameInstallation? install;

    /// Whether this is the game currently being managed.
    [ObservableProperty] private bool isActive;

    public bool IsInstalled => Install != null;

    public string ShortName => Profile.ShortName;
    public string DisplayName => Profile.DisplayName;

    /// The second line of the tab. Says what the tab will DO when it isn't a plain switch, so a
    /// missing game reads as an action rather than as a dead control.
    public string StateLabel => IsInstalled ? Profile.DisplayName : "Not found - click to locate";

    partial void OnInstallChanged(GameInstallation? value)
    {
        OnPropertyChanged(nameof(IsInstalled));
        OnPropertyChanged(nameof(StateLabel));
    }
}
