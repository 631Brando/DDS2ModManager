using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DDS2ModManager.Views;

/// How a mod author makes their mod updateable.
///
/// The steps are BUILT FROM THE CONSTANTS the reader actually uses
/// (ModUpdateSourceResolver.UrlProperty and friends) rather than typed out as prose.
/// A guide that says "call it ModUpdateUrl" while the code looks for something else is worse
/// than no guide: every author who follows it produces a mod that silently never updates, and
/// nothing anywhere reports a problem.
public partial class ModAuthorGuideWindow : Window
{
    /// Kept in step with MODDING.md. An earlier build of this guide spelled the key
    /// "modUpdateUrl"; that is still read (see ModManifest) so nobody's existing manifest breaks,
    /// but new ones should use "updateUrl" so there is one spelling in the documentation.
    private const string ManifestSample = """
{
  "schema": 1,
  "name": "Your Mod",
  "author": "yourname",
  "version": "1.0.0",
  "updateUrl": "https://github.com/yourname/YourMod",
  "asset": "YourMod.zip"
}
""";

    public ModAuthorGuideWindow()
    {
        InitializeComponent();

        AddStep(1, "Pick where the address lives",
            "Logic mods carry it inside the mod itself. Patch mods and lua mods have no ModActor, so they ship a small file next to the mod instead.\n\n" +
            "Either way it is one string: the GitHub repository you publish releases from. All of these are accepted and mean the same thing:\n\n" +
            "    https://github.com/yourname/YourMod\n" +
            "    https://github.com/yourname/YourMod.git\n" +
            "    https://github.com/yourname/YourMod/releases/latest\n" +
            "    https://www.github.com/yourname/YourMod\n" +
            "    github.com/yourname/YourMod\n" +
            "    yourname/YourMod\n\n" +
            "Only the first two path segments are read, so a link to a release, a branch or a file all resolve to the repository. Casing of the scheme and host does not matter.\n\n" +
            "The short yourname/YourMod form is stricter than it looks: it needs exactly one slash and NO dot anywhere, so yourname/My.Mod and yourname/YourMod.git are both refused. If your repository name contains a dot, write the full https URL instead.\n\n" +
            "Not accepted: plain http, the git@github.com:you/mod.git clone string, gists, raw.githubusercontent.com, and any host other than github.com.\n\n" +
            "Also refused: an address ending in a full stop (so don't put one at the end of a sentence), and GitHub's own pages such as github.com/orgs/you/repositories - they have the same shape as a repository URL but are not one. Link the repository itself.");

        AddStep(2, "Logic mods: a variable on your ModActor",
            $"Add a String variable called {ModUpdateSourceResolver.UrlProperty} to your mod's ModActor, and set its DEFAULT VALUE to your repository:\n\n" +
            $"    {ModUpdateSourceResolver.UrlProperty} = https://github.com/yourname/yourmod\n" +
            $"    {ModUpdateSourceResolver.VersionProperty} = 1.0.0\n\n" +
            $"{ModUpdateSourceResolver.VersionProperty} is required in practice. Without it the manager can see that a newer release exists but cannot tell which version the player already has, so it offers nothing at all rather than guessing.\n\n" +
            "Costs no extra files: you ship the same .pak/.ucas/.utoc as before. Remember the value has to be set as the variable's default, and the mod has to be re-cooked before players see it.",
            emphasis: "It must be the variable's DEFAULT value. A value assigned at runtime lives in Blueprint code, not in the cooked asset, and there is nothing to read.");

        AddStep(3, "Everything else: a .dds2mod.json",
            $"Patch mods and lua mods ship a small file alongside the mod:\n\n{ManifestSample}\n\n" +
            $"For a LUA mod, put it anywhere in your mod's folder - any name ending in {ModManifest.FileName} works.\n\n" +
            $"For a PAK mod it must be named after the mod - MyMod{ModManifest.FileName} - because pak mods all share one folder, and without the name match the manager could pick up a neighbouring mod's file and offer updates from someone else's repository.",
            emphasis: null);

        AddStep(4, "Publish releases with the version as the tag",
            "Tag each release with its version - v1.2.0 or 1.2.0, either is fine - and attach the mod as a single .zip, .7z or .rar.\n\n" +
            "The release description is shown to players as the changelog before they agree to update, so it is worth writing.\n\n" +
            "A bare .pak is spotted as a new version but cannot be unpacked for the player - they are told an update exists and given a link to fetch it themselves. Same if a release has no attached file at all.\n\n" +
            "If a release carries several files, name the right one with the \"asset\" field in your manifest. It has to be the exact published file name, so either keep that name stable between releases or leave the field out and publish exactly one archive - a name that matches nothing skips the update entirely.");

        AddStep(5, "If your mod has two halves, name the folders",
            "A mod with both a pak and a lua script goes to two different places. Lay the archive out so each half says where it belongs:\n\n" +
            "    LogicMods\\MyMod\\MyMod.pak\n" +
            "    UE4SSMods\\MyMod\\Scripts\\main.lua\n" +
            "    INSTALL.txt\n\n" +
            "The manager installs both halves in one go. Without those folder names it cannot tell a two-part mod from two alternative versions of one mod, and will ask the player to choose between them.");

        AddStep(6, "What players see",
            "The manager checks your repository at most once every six hours and shows the mod as updateable in their list.\n\n" +
            "Before anything downloads they are shown the release notes and the address it is coming from, because an update from your repository has not been through Nexus's virus scanning. Players are asked every single time - marking your account as a recognised update address changes how much the prompt has to explain, it never removes it.\n\n" +
            "If your mod's update address ever changes, they are warned, no update is offered until they confirm it, and trust in your account does not carry over.",
            emphasis: "The address must point at a github.com repository - any other host, and plain http, are ignored, so players can always read the source of what they are about to run. The full https URL, github.com/you/YourMod, and the short you/YourMod are all accepted and mean the same thing. Whichever you pick, keep it byte-for-byte identical in every release: it is compared as the exact string you wrote, so even reformatting it reads as the address having moved.");

        AddStep(7, "Check it worked",
            "Re-cook and repack the mod, install it here, and look at the Version column. If it shows your version with a \"source\" link beside it, the manager can read it.\n\n" +
            $"If the column is blank, the address was either not found or refused - most often because the variable's default was not set before cooking, the manifest is named without the {ModManifest.FileName} ending, or the address itself was rejected. A rejected address is named in the log, so read that before re-cooking.");
    }

    private void AddStep(int number, string title, string body, string? emphasis = null)
    {
        var card = new Border
        {
            Style = (Style)FindResource("CardBorder"),
            Padding = new Thickness(14),
            Margin = new Thickness(0, 0, 0, 10)
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var disc = new Border
        {
            Width = 26,
            Height = 26,
            CornerRadius = new CornerRadius(13),
            Background = (Brush)FindResource("AccentBrush"),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 0, 12, 0),
            Child = new TextBlock
            {
                Text = number.ToString(),
                FontWeight = FontWeights.Bold,
                FontSize = 13,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        Grid.SetColumn(disc, 0);
        grid.Children.Add(disc);

        var text = new StackPanel();
        text.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.SemiBold,
            FontSize = 14,
            Margin = new Thickness(0, 2, 0, 6)
        });
        text.Children.Add(new TextBlock
        {
            Text = body,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            LineHeight = 18,
            Foreground = (Brush)FindResource("TextPrimaryBrush")
        });

        if (emphasis != null)
        {
            text.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(30, 245, 165, 36)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(120, 245, 165, 36)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 7, 10, 7),
                Margin = new Thickness(0, 9, 0, 0),
                Child = new TextBlock
                {
                    Text = emphasis,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 11,
                    LineHeight = 16,
                    Foreground = (Brush)FindResource("TextPrimaryBrush")
                }
            });
        }

        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        card.Child = grid;
        StepsPanel.Children.Add(card);
    }

    private void CopyManifest_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(ManifestSample);
            LoggingService.Instance.Info($"Copied a {ModManifest.FileName} template to the clipboard.");
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Warn($"Couldn't copy to the clipboard: {ex.Message}");
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
