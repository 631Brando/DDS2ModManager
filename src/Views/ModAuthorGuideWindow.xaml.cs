using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DDS2ModManager.Views;

/// How a mod author makes their mod updateable.
///
/// The steps are BUILT FROM THE CONSTANTS the reader actually uses
/// (ModUpdateSourceReader.ModActorUrlProperty and friends) rather than typed out as prose.
/// A guide that says "call it ModUpdateUrl" while the code looks for something else is worse
/// than no guide: every author who follows it produces a mod that silently never updates, and
/// nothing anywhere reports a problem.
public partial class ModAuthorGuideWindow : Window
{
    private const string ManifestSample = """
{
  "modUpdateUrl": "https://github.com/yourname/yourmod",
  "version": "1.0.0"
}
""";

    public ModAuthorGuideWindow()
    {
        InitializeComponent();

        AddStep(1, "Pick where the address lives",
            $"Logic mods carry it inside the mod itself. Patch mods and lua mods have no ModActor, so they ship a small file next to the mod instead.\n\n" +
            $"Either way it is one string: the GitHub repository you publish releases from.");

        AddStep(2, "Logic mods: a variable on your ModActor",
            $"Add a String variable called {ModUpdateSourceReader.ModActorUrlProperty} to your mod's ModActor, and set its DEFAULT VALUE to your repository:\n\n" +
            $"    {ModUpdateSourceReader.ModActorUrlProperty} = https://github.com/yourname/yourmod\n" +
            $"    {ModUpdateSourceReader.ModActorVersionProperty} = 1.0.0\n\n" +
            $"{ModUpdateSourceReader.ModActorVersionProperty} is optional but worth adding - without it the manager can see that a newer release exists but cannot tell which version the player already has, so it says nothing rather than guessing.\n\n" +
            "Costs no extra files: you ship the same .pak/.ucas/.utoc as before. Remember the value has to be set as the variable's default, and the mod has to be re-cooked before players see it.",
            emphasis: "It must be the variable's DEFAULT value. A value assigned at runtime lives in Blueprint code, not in the cooked asset, and there is nothing to read.");

        AddStep(3, "Everything else: a .dds2mod.json",
            $"Patch mods and lua mods ship a small file alongside the mod:\n\n{ManifestSample}\n\n" +
            $"For a LUA mod, put it anywhere in your mod's folder - any name ending in {ModUpdateSourceReader.ManifestSuffix} works.\n\n" +
            $"For a PAK mod it must be named after the mod - MyMod{ModUpdateSourceReader.ManifestSuffix} - because pak mods all share one folder, and without the name match the manager could pick up a neighbouring mod's file and offer updates from someone else's repository.",
            emphasis: null);

        AddStep(4, "Publish releases with the version as the tag",
            "Tag each release with its version - v1.2.0 or 1.2.0, either is fine - and attach the mod as a .zip, .7z or .rar.\n\n" +
            "The release description is shown to players as the changelog before they agree to update, so it is worth writing.\n\n" +
            "If a release has no attached file, players still get told there is a new version and see what changed; they just download it themselves.");

        AddStep(5, "If your mod has two halves, name the folders",
            "A mod with both a pak and a lua script goes to two different places. Lay the archive out so each half says where it belongs:\n\n" +
            "    LogicMods\\MyMod\\MyMod.pak\n" +
            "    UE4SSMods\\MyMod\\Scripts\\main.lua\n" +
            "    INSTALL.txt\n\n" +
            "The manager installs both halves in one go. Without those folder names it cannot tell a two-part mod from two alternative versions of one mod, and will ask the player to choose between them.");

        AddStep(6, "What players see",
            "The manager checks your repository at most once every six hours and shows the mod as updateable in their list.\n\n" +
            "Before anything downloads they are shown the release notes and the address it is coming from, because an update from your repository has not been through Nexus's virus scanning. Players can mark you as a trusted author to skip that prompt - and if your mod's update address ever changes, they are warned and trust is revoked automatically.",
            emphasis: "Only https://github.com addresses are accepted. Anything else is ignored, so that players can always read the source of what they are about to run.");

        AddStep(7, "Check it worked",
            "Re-cook and repack the mod, install it here, and look at the Version column. If it shows your version with a \"source\" link beside it, the manager can read it.\n\n" +
            "If the column is blank, the address was not found - most often because the variable's default was not set before cooking, or the manifest is named without the .dds2mod.json ending.");
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
            LoggingService.Instance.Info($"Copied a {ModUpdateSourceReader.ManifestSuffix} template to the clipboard.");
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Warn($"Couldn't copy to the clipboard: {ex.Message}");
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
