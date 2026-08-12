using System.Diagnostics;
using System.Windows;

namespace DDS2ModManager.Views;

/// Shown before a mod update is downloaded.
///
/// The point of this dialog is informed consent. A mod update comes from the author's own
/// GitHub repo, not from Nexus, which means it has NOT been through Nexus's virus scanning -
/// the one safety net Nexus actually provides. So the source URL is displayed as a first-class
/// element rather than tucked behind a link, and the default action is always reversible:
/// nothing downloads until this dialog returns true.
public partial class ModUpdateAvailableWindow : Window
{
    private readonly string _releaseUrl;

    public ModUpdateAvailableWindow(
        string modName,
        string installedVersion,
        string newVersion,
        string changelog,
        string sourceUrl,
        string releaseUrl,
        bool canAutoInstall,
        bool urlChanged)
    {
        InitializeComponent();
        _releaseUrl = releaseUrl;

        HeaderText.Text = $"{modName} {newVersion} is available";
        VersionText.Text = string.IsNullOrWhiteSpace(installedVersion)
            ? "This mod doesn't report which version you have installed."
            : $"You have {installedVersion}.";

        SourceUrlText.Text = sourceUrl;

        // The wording changes with the risk. A mod whose update URL has moved since it was
        // installed is the exact shape of a hijacked update channel, and that deserves more
        // than the standard note.
        TrustNoteText.Text = urlChanged
            ? "WARNING: this mod's update address has CHANGED since you installed it. That can simply mean " +
              "the author moved their repository - or that someone else is now publishing updates for it. " +
              "Check the page above before continuing, and if you did not expect this, don't."
            : "Updates come from the mod author's repository, not from Nexus, so this file has not been " +
              "scanned by Nexus. Open the page above if you want to see the source first.";

        ChangelogText.Text = UpdateAvailableWindow.FormatChangelog(changelog);

        // No downloadable file on the release means there is nothing to install automatically;
        // offering a button that cannot work is worse than not offering one.
        if (!canAutoInstall)
        {
            UpdateButton.IsEnabled = false;
            UpdateButton.Content = "No downloadable file";
            UpdateButton.ToolTip =
                "This release has no attached file to download. Use \"Open the release page\" and install it manually.";
        }
    }

    private void ViewOnGitHub_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(_releaseUrl) { UseShellExecute = true }); }
        catch (Exception ex) { LoggingService.Instance.Warn($"Couldn't open the release page: {ex.Message}"); }
    }

    private void Update_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Later_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
