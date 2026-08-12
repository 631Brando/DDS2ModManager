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

    public bool TrustAuthorChecked => TrustAuthorBox.IsChecked == true;

    public ModUpdatePromptWindow(ModInfo mod, ModUpdateSource source, ModTrustLevel trust)
    {
        InitializeComponent();
        _repositoryUrl = source.RepositoryUrl;

        Title = $"Update {mod.Name}";
        HeaderText.Text = $"{mod.Name} {mod.AvailableUpdateTag} is available";

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
                TrustText.Text = $"You've trusted '{source.Owner}' before.";
                TrustText.Foreground = (System.Windows.Media.Brush)FindResource("SuccessBrush");
                TrustAuthorBox.Visibility = Visibility.Collapsed;
                break;

            default:
                TrustText.Text = $"Unrecognised source — '{source.Owner}' isn't verified, and you haven't trusted them yet.";
                TrustText.Foreground = (System.Windows.Media.Brush)FindResource("WarningBrush");
                TrustAuthorBox.Content = $"Trust updates from '{source.Owner}' in future";
                TrustAuthorBox.Visibility = Visibility.Visible;
                break;
        }

        NotesText.Text = string.IsNullOrWhiteSpace(mod.AvailableUpdateNotes)
            ? "The author didn't write any release notes for this version."
            : mod.AvailableUpdateNotes;
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
