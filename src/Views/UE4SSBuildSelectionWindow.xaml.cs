using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DDS2ModManager.Views;

/// Shown before every UE4SS install or update: which release line, and which build of it.
///
/// Both choices are preselected from what is INSTALLED rather than from a default, for the reason
/// the build choice always was: accepting an update must never quietly move someone from Dev to
/// Standard, or from one release line to the other. A fresh install starts on experimental, the
/// recommended line, and stable is never preselected unless it is what's already there.
public partial class UE4SSBuildSelectionWindow : Window
{
    private readonly GameProfile _profile;

    public bool UseDevBuild { get; private set; }
    public UE4SSChannel Channel { get; private set; } = UE4SSChannel.Experimental;

    /// <param name="olderLayoutInstalled">A v3.0.1 install in Binaries\Win64 that this manager made.
    /// Either line moves its mods into ue4ss\Mods, so that is said before the choice, not after.</param>
    public UE4SSBuildSelectionWindow(GameProfile profile, bool preferDev, UE4SSChannel installedChannel,
        bool olderLayoutInstalled = false)
    {
        InitializeComponent();
        _profile = profile;

        StableTitle.Text = $"Stable {LoaderCompatibility.StableVersion}";

        var experimental = LoaderCompatibility.ForUE4SS(profile, UE4SSChannel.Experimental);
        var stable = LoaderCompatibility.ForUE4SS(profile, UE4SSChannel.Stable);

        // Both lines now ship the ue4ss\ layout, so moving between them is an ordinary install. Only
        // the old v3.0.1 layout changes shape on the way, and the player's mods move with it.
        if (olderLayoutInstalled)
        {
            const string moves = "Your UE4SS mods and settings will move from Binaries\\Win64 into ue4ss\\, where " +
                                 "this version keeps them.";
            experimental = WithNote(experimental, moves);
            stable = WithNote(stable, moves);
        }
        Describe(ExperimentalCard, ExperimentalOption, ExperimentalNote, experimental);
        Describe(StableCard, StableOption, StableNote, stable);

        var pickStable = (installedChannel == UE4SSChannel.Stable && stable.Available) || !experimental.Available;
        (pickStable ? StableOption : ExperimentalOption).IsChecked = true;

        if (preferDev) DevOption.IsChecked = true;
        else StandardOption.IsChecked = true;
    }

    private static LoaderCompatibility.Choice WithNote(LoaderCompatibility.Choice choice, string note) =>
        choice.Available
            ? choice with { Warning = string.IsNullOrWhiteSpace(choice.Warning) ? note : $"{choice.Warning} {note}" }
            : choice;

    /// An unavailable line stays visible with its reason, so the choice is explained rather than missing.
    private void Describe(Border card, RadioButton option, TextBlock note, LoaderCompatibility.Choice choice)
    {
        var text = choice.Available ? choice.Warning : choice.BlockedReason;
        if (!string.IsNullOrWhiteSpace(text))
        {
            note.Text = (choice.Available ? "⚠ " : "") + text;
            note.Foreground = (Brush)FindResource(choice.Available ? "WarningBrush" : "TextMutedBrush");
            note.Visibility = Visibility.Visible;
        }

        if (choice.Available) return;
        option.IsEnabled = false;
        card.IsEnabled = false;
        card.Opacity = 0.55;
        card.Cursor = null;
    }

    private void ExperimentalOption_Click(object sender, MouseButtonEventArgs e)
    {
        if (ExperimentalOption.IsEnabled) ExperimentalOption.IsChecked = true;
    }

    private void StableOption_Click(object sender, MouseButtonEventArgs e)
    {
        if (StableOption.IsEnabled) StableOption.IsChecked = true;
    }

    private void StandardOption_Click(object sender, MouseButtonEventArgs e) => StandardOption.IsChecked = true;
    private void DevOption_Click(object sender, MouseButtonEventArgs e) => DevOption.IsChecked = true;

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        Channel = StableOption.IsChecked == true ? UE4SSChannel.Stable : UE4SSChannel.Experimental;
        UseDevBuild = DevOption.IsChecked == true;

        // A game-specific caveat is a known break, not general advice, so it is asked about outright.
        if (Channel == UE4SSChannel.Stable && !string.IsNullOrWhiteSpace(_profile.UE4SSStableCaveat)
            && MessageBox.Show(this, $"{_profile.UE4SSStableCaveat}\n\nInstall stable anyway?", "Stable UE4SS",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
