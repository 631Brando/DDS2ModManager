using System.Diagnostics;
using System.Windows;
using DDS2ModManager.ViewModels;

namespace DDS2ModManager.Views;

/// Lists saved mod profiles and applies, exports or deletes them.
public partial class ProfilesWindow : Window
{
    private readonly MainViewModel _mainViewModel;
    private readonly ModProfileService _profiles;

    public ProfilesWindow(MainViewModel mainViewModel, ModProfileService profiles)
    {
        InitializeComponent();
        _mainViewModel = mainViewModel;
        _profiles = profiles;
        Reload();
    }

    private void Reload()
    {
        var all = _profiles.All();
        ProfileList.ItemsSource = all;
        EmptyText.Visibility = all.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static ModProfile? From(object sender) => (sender as FrameworkElement)?.Tag as ModProfile;

    /// Closes first: applying shows its own confirmation and then rebuilds the list behind this
    /// window, so leaving it open on top would be showing stale state.
    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (From(sender) is not { } profile) return;

        Close();
        _mainViewModel.ApplyProfile(profile);
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (From(sender) is not { } profile) return;

        try
        {
            Clipboard.SetText(ModProfileService.ToShareableText(profile));
            LoggingService.Instance.Success($"Copied '{profile.Name}' to the clipboard.");
        }
        catch (Exception ex) { LoggingService.Instance.Error($"Couldn't copy the profile: {ex.Message}"); }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (From(sender) is not { } profile) return;

        if (MessageBox.Show(
                $"Delete the profile '{profile.Name}'?\n\nThis only forgets the saved list. No mod is touched.",
                "Delete profile", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        _profiles.Delete(profile.Name);
        Reload();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(_profiles.Folder) { UseShellExecute = true }); }
        catch (Exception ex) { LoggingService.Instance.Warn($"Couldn't open the profiles folder: {ex.Message}"); }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
