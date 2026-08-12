using System.Diagnostics;
using System.Windows;

namespace DDS2ModManager.Views;

public partial class UpdateAvailableWindow : Window
{
    private readonly string _releaseUrl;

    public UpdateAvailableWindow(string newVersion, Version currentVersion, string changelog, string releaseUrl)
    {
        InitializeComponent();
        _releaseUrl = releaseUrl;

        HeaderText.Text = $"DDS2 Mod Manager {newVersion} is available";
        VersionText.Text = $"You're currently on v{currentVersion}. Updating downloads the new version and restarts the app.";
        ChangelogText.Text = FormatChangelog(changelog);
    }

    /// GitHub release bodies are markdown. Rendering markdown properly would mean pulling in a
    /// whole renderer for one dialog, so this just strips the handful of markers that would
    /// otherwise show up as literal noise (#, *, -, `) and leaves the text readable as-is.
    ///
    /// Shared with ModUpdateAvailableWindow rather than copied - both show a GitHub release
    /// body, and two formatters would drift the moment one of them learned a new marker.
    internal static string FormatChangelog(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return "No release notes were provided for this version.";

        var lines = body.Replace("\r\n", "\n").Split('\n');
        var output = new List<string>();

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();

            // Headings -> plain bold-ish lines (we have no rich text here, so just drop the #s).
            if (line.TrimStart().StartsWith('#'))
                line = line.TrimStart().TrimStart('#').Trim();

            // Bullets: normalise "* item" / "- item" to a real bullet character.
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("* ") || trimmed.StartsWith("- "))
            {
                var indent = line[..(line.Length - trimmed.Length)];
                line = indent + "• " + trimmed[2..];
            }

            // Inline emphasis/code markers add nothing without rich text.
            line = line.Replace("**", "").Replace("`", "");

            output.Add(line);
        }

        return string.Join(Environment.NewLine, output).Trim();
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
