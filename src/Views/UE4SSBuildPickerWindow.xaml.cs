using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DDS2ModManager.Views;

/// Lets someone install one specific UE4SS build, rather than only whatever is newest.
///
/// Exists because of a real report: an update moved a user from build 1093 to 1111, every one of
/// their mods stopped working, and there was no way back. Every experimental build is permanently
/// archived, so "put me back on the one that worked" is answerable - it just was not reachable
/// from this app.
///
/// Every build in the 3.0.1 line reports itself as "v3.0.1 Beta", so the version is useless for
/// telling them apart and the list is built around the build number and commit instead.
public partial class UE4SSBuildPickerWindow : Window
{
    public class Row
    {
        public required UE4SSBuild Build { get; init; }

        /// Marked so going "back" to the build already installed is not a download for nothing.
        public bool IsInstalled { get; init; }
    }

    private readonly IReadOnlyList<Row> _all;
    private readonly ObservableCollection<Row> _shown = new();

    /// Null unless the user committed.
    public UE4SSBuild? SelectedBuild { get; private set; }

    public UE4SSBuildPickerWindow(IReadOnlyList<UE4SSBuild> builds, string? installedAssetName, bool preferDev)
    {
        InitializeComponent();

        _all = builds
            .Select(b => new Row
            {
                Build = b,
                IsInstalled = string.Equals(b.AssetName, installedAssetName, StringComparison.OrdinalIgnoreCase)
            })
            .ToList();

        // Opens on the channel they are already on. Someone who runs the console build is almost
        // never looking for a non-console one, and the difference is not visible in the version.
        ConsoleOnlyBox.IsChecked = preferDev;

        BuildList.ItemsSource = _shown;
        ApplyFilter();

        Loaded += (_, _) => FilterBox.Focus();
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();
    private void Filter_Changed(object sender, RoutedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        if (_shown == null) return;

        var term = FilterBox.Text?.Trim() ?? "";
        var consoleOnly = ConsoleOnlyBox.IsChecked == true;

        var matches = _all
            .Where(r => !consoleOnly || r.Build.IsDevBuild)
            .Where(r => term.Length == 0
                        || r.Build.AssetName.Contains(term, StringComparison.OrdinalIgnoreCase)
                        || r.Build.Build.ToString().Contains(term, StringComparison.Ordinal))
            .ToList();

        _shown.Clear();
        foreach (var r in matches) _shown.Add(r);

        CountText.Text = $"{matches.Count} build(s)";

        EmptyText.Text = $"Nothing matches \"{term}\".";
        EmptyText.Visibility = matches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // Selection does not survive the list being rebuilt, so the button has to follow it back down.
        InstallButton.IsEnabled = false;
        WarningText.Visibility = Visibility.Collapsed;
    }

    private void BuildList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var row = BuildList.SelectedItem as Row;
        InstallButton.IsEnabled = row != null;

        if (row == null)
        {
            WarningText.Visibility = Visibility.Collapsed;
            return;
        }

        // Said before they commit, not after. Switching channel is the change people do not notice
        // they made, because the version is identical either way.
        var installedIsDev = _all.FirstOrDefault(r => r.IsInstalled)?.Build.IsDevBuild;

        if (row.IsInstalled)
        {
            WarningText.Text = "This is the build you already have. Installing it again is harmless, "
                             + "but it won't change anything.";
            WarningText.Visibility = Visibility.Visible;
        }
        else if (installedIsDev is { } wasDev && wasDev != row.Build.IsDevBuild)
        {
            WarningText.Text = row.Build.IsDevBuild
                ? "This is a console build - a window with live UE4SS logs will open with the game."
                : "This build has no console window. If you rely on watching UE4SS logs while the "
                  + "game runs, pick a console build instead.";
            WarningText.Visibility = Visibility.Visible;
        }
        else
        {
            WarningText.Visibility = Visibility.Collapsed;
        }
    }

    private void BuildList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (BuildList.SelectedItem is Row) Install_Click(sender, e);
    }

    private void Install_Click(object sender, RoutedEventArgs e)
    {
        if (BuildList.SelectedItem is not Row row) return;

        SelectedBuild = row.Build;
        DialogResult = true;
        Close();
    }
}
