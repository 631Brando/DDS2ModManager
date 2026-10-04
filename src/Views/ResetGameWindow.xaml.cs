using System.Windows;

namespace DDS2ModManager.Views;

public partial class ResetGameWindow : Window
{
    public VanillaResetOptions Options { get; private set; } = new();

    /// <param name="isGenericGame">
    /// True for an Unreal game with no hand-written profile. Its base files can't be told apart from
    /// mods by name, so removing untracked files is limited to the conventional mod folders - and
    /// the option says exactly that, and starts unticked, rather than repeating a promise ("base game
    /// files are never touched") that only holds because of that narrower scope.
    /// </param>
    public ResetGameWindow(int trackedModCount, bool isGenericGame = false)
    {
        InitializeComponent();
        TrackedCountText.Text = trackedModCount == 0
            ? "Nothing is currently tracked."
            : $"{trackedModCount} mod(s) currently tracked. Their files are deleted from the game.";

        if (isGenericGame)
        {
            UntrackedBox.IsChecked = false;
            UntrackedDetail.Text =
                "On this game only the mod folders (Content\\Paks\\LogicMods, Mods and ~mods) are cleared. Nothing " +
                "directly in Content\\Paks is touched, because this manager doesn't know this game's own file names.";
        }
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        Options = new VanillaResetOptions
        {
            RemoveTrackedMods = TrackedBox.IsChecked == true,
            RemoveUntrackedMods = UntrackedBox.IsChecked == true,
            RemoveUE4SS = UE4SSBox.IsChecked == true,
            ResetConfigs = ConfigBox.IsChecked == true
        };

        if (!Options.RemoveTrackedMods && !Options.RemoveUntrackedMods &&
            !Options.RemoveUE4SS && !Options.ResetConfigs)
        {
            MessageBox.Show("Nothing is selected to remove.", "Reset game", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Spell the consequences back out rather than relying on the checkbox labels alone - this
        // deletes files from the game folder and there's no undo.
        var parts = new List<string>();
        if (Options.RemoveTrackedMods) parts.Add("• all mods installed by this manager");
        if (Options.RemoveUntrackedMods) parts.Add("• mod files it isn't tracking");
        if (Options.RemoveUE4SS) parts.Add("• UE4SS itself");
        if (Options.ResetConfigs) parts.Add("• the game's config files (graphics, audio, keybinds)");

        var confirm = MessageBox.Show(
            "This will permanently delete:\n\n" + string.Join("\n", parts) +
            "\n\nYour saves are not affected. This cannot be undone.\n\nContinue?",
            "Reset game to vanilla", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
