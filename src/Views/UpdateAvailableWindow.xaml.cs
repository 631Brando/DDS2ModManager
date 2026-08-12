using System.Diagnostics;
using System.Windows;

namespace DDS2ModManager.Views;

public partial class UpdateAvailableWindow : Window
{
    private readonly string _releaseUrl;

    /// <param name="isDowngrade">
    /// True when this moves the version number backwards - switching from an experimental build
    /// to the stable channel. Calling that an "update" would be misleading, so the wording says
    /// what's actually happening.
    /// </param>
    public UpdateAvailableWindow(string newVersion, Version currentVersion, string changelog, string releaseUrl,
        bool isDowngrade = false)
    {
        InitializeComponent();
        _releaseUrl = releaseUrl;

        if (isDowngrade)
        {
            Title = "Switch to the stable channel";
            HeaderText.Text = $"Switch to the stable release {newVersion}";
            VersionText.Text =
                $"You're on v{currentVersion}, an experimental build. The current stable release is {newVersion}, " +
                "so this moves you back a version. Any features only in experimental builds will go away until " +
                "stable catches up.";
            UpdateButton.Content = "Switch";
        }
        else
        {
            HeaderText.Text = $"DDS2 Mod Manager {newVersion} is available";
            VersionText.Text = $"You're currently on v{currentVersion}. Updating downloads the new version and restarts the app.";
        }

        ChangelogText.Text = FormatChangelog(changelog);
    }

    /// GitHub release bodies are markdown. Rendering markdown properly would mean pulling in a
    /// whole renderer for one dialog, so this just strips the handful of markers that would
    /// otherwise show up as literal noise (#, *, -, `) and leaves the text readable as-is.
    private static string FormatChangelog(string body)
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
