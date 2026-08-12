using System.Diagnostics;
using System.Windows;

namespace DDS2ModManager.Views;

/// Asks before a mod update is downloaded and installed.
///
/// This prompt is the whole safety model for mod auto-updating, so it leads with the thing that
/// matters rather than burying it: which GitHub repository the file is coming from, and whether
/// anyone has vouched for it. The URL was declared by the mod itself, so the user is the only one
/// who can judge whether it belongs to the author they think it does.
///
/// It appears whatever the trust level. Trusting an author only changes how much the prompt has to
/// explain - it never removes the prompt, because an account can be compromised and a curated list
/// can go stale, and either of those silently installing code would be far worse than one click.
public partial class ModUpdatePromptWindow : Window
{
    private readonly string _repositoryUrl;
    private readonly string _releaseUrl;

    public bool TrustAuthorChecked => TrustAuthorBox.IsChecked == true;

    /// <param name="urlChanged">
    /// True when the mod's declared update address differs from the one it was installed with.
    /// That is the exact shape of a hijacked update channel, so it overrides everything else the
    /// dialog would otherwise say: the trust tick is withdrawn and the warning leads.
    /// </param>
    /// <param name="canAutoInstall">
    /// False when the release has no file this manager can install. The dialog then becomes a
    /// notification with a link out, rather than offering a button that would do nothing.
    /// </param>
    public ModUpdatePromptWindow(
        ModInfo mod,
        ModUpdateSource source,
        ModTrustLevel trust,
        string newVersion,
        string? releaseNotes,
        string releaseUrl,
        bool canAutoInstall,
        bool urlChanged)
    {
        InitializeComponent();
        _repositoryUrl = source.RepositoryUrl;
        _releaseUrl = releaseUrl;

        Title = $"Update {mod.Name}";
        HeaderText.Text = $"{mod.Name} {newVersion} is available";

        VersionText.Text = source.Version.Length > 0
            ? $"You have version {source.Version}. This will download the new version and reinstall the mod."
            : "This will download the new version and reinstall the mod, replacing the files it installed before.";

        SourceText.Text = $"Downloading from {source.RepositoryUrl}\n"
                          + $"Declared by the mod itself, via {DescribeDeclaration(source.Declaration)}.";

        switch (trust)
        {
            case ModTrustLevel.Verified:
                TrustText.Text = "Verified source — the maintainers have checked this author.";
                TrustText.Foreground = (System.Windows.Media.Brush)FindResource("SuccessBrush");
                TrustAuthorBox.Visibility = Visibility.Collapsed;
                break;

            case ModTrustLevel.TrustedByUser:
                TrustText.Text = $"You've trusted '{source.Owner}' before. You're still asked each time.";
                TrustText.Foreground = (System.Windows.Media.Brush)FindResource("SuccessBrush");
                TrustAuthorBox.Visibility = Visibility.Collapsed;
                break;

            default:
                TrustText.Text = $"Unrecognised source — '{source.Owner}' isn't verified, and you haven't trusted them yet.";
                TrustText.Foreground = (System.Windows.Media.Brush)FindResource("WarningBrush");
                TrustAuthorBox.Content = $"Trust updates from '{source.Owner}' in future (covers all of their mods)";
                TrustAuthorBox.Visibility = Visibility.Visible;
                break;
        }

        // A moved update address outranks any amount of trust. Whoever controls the new address
        // never earned the trust granted to the old one, so don't offer to extend it there.
        if (urlChanged)
        {
            TrustText.Text = "This mod's update address has CHANGED since you installed it. That is what a hijacked "
                             + "update channel looks like. Only continue if you know the author moved their repository.";
            TrustText.Foreground = (System.Windows.Media.Brush)FindResource("ErrorBrush");
            TrustAuthorBox.IsChecked = false;
            TrustAuthorBox.IsEnabled = false;
            TrustAuthorBox.Visibility = Visibility.Visible;
            TrustAuthorBox.Content = "Trust is unavailable while the update address is in dispute";
        }

        // Nothing installable in the release: don't offer a button that can't do anything.
        if (!canAutoInstall)
        {
            ConfirmButton.IsEnabled = false;
            ConfirmButton.ToolTip = "This release has no file this manager knows how to install. "
                                    + "Use \"View release\" and install it manually.";
        }

        NotesText.Text = string.IsNullOrWhiteSpace(releaseNotes)
            ? "The author didn't write any release notes for this version."
            : releaseNotes;
    }

    private static string DescribeDeclaration(ModUpdateDeclaration declaration) => declaration switch
    {
        ModUpdateDeclaration.BlueprintVariable => "a ModUpdateUrl variable in its ModActor",
        ModUpdateDeclaration.Manifest => "its .dds2mod.json file",
        _ => "an unknown source"
    };

    private void ViewSource_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(_repositoryUrl) { UseShellExecute = true }); }
        catch (Exception ex) { LoggingService.Instance.Warn($"Couldn't open the repository page: {ex.Message}"); }
    }

    private void ViewRelease_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(_releaseUrl) { UseShellExecute = true }); }
        catch (Exception ex) { LoggingService.Instance.Warn($"Couldn't open the release page: {ex.Message}"); }
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
