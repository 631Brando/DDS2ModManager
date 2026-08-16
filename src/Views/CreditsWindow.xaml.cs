using System.Diagnostics;
using System.Windows;
using DDS2ModManager.ViewModels;

namespace DDS2ModManager.Views;

/// Who made this, what it's built on, and what it works alongside.
///
/// Kept as a real page rather than a line in a readme because most of what this app does is only
/// possible because of other people's work - CUE4Parse reads the pak files, UE4SS loads the mods,
/// Nexus hosts them. A tool that quietly absorbs all of that and presents itself as self-contained
/// is misrepresenting itself.
public partial class CreditsWindow : Window
{
    public CreditsWindow()
    {
        InitializeComponent();

        // The same string the title bar shows, including the commit, so a screenshot of this page
        // identifies the exact build.
        VersionText.Text = $"{MainViewModel.AppVersionDisplay}  ·  a free, open-source mod manager for Drug Dealer Simulator 2";
    }

    private void Link_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string url || string.IsNullOrWhiteSpace(url)) return;

        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { LoggingService.Instance.Warn($"Couldn't open '{url}': {ex.Message}"); }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
