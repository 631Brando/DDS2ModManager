using System.Diagnostics;
using System.Windows;
using DDS2ModManager.ViewModels;

namespace DDS2ModManager.Views;

public partial class SettingsWindow : Window
{
    private readonly MainViewModel _mainViewModel;

    /// Where the channels stand, once GitHub has answered. Null until then, and on failure - the
    /// hints fall back to their offline wording rather than guessing.
    private AppUpdateService.ChannelStatus? _channels;

    public SettingsWindow(MainViewModel mainViewModel)
    {
        InitializeComponent();
        _mainViewModel = mainViewModel;

        var current = AppSettingsService.Instance.Current;
        GamePathBox.Text = current.GamePathOverride;
        EGameCombo.Text = current.EGameVersion;
        MappingsPathBox.Text = current.MappingsOverridePath;
        AesKeyBox.Text = current.AesKeyHex;
        AutoCheckBox.IsChecked = current.AutoCheckUE4SSUpdatesOnStartup;
        AutoCheckAppUpdateBox.IsChecked = current.CheckForAppUpdatesOnStartup;
        AutoCheckModUpdateBox.IsChecked = current.CheckForModUpdatesOnStartup;
        var running = AppUpdateService.Describe(AppUpdateService.GetCurrentVersion());
        AppVersionText.Text = AppUpdateService.IsRunningExperimentalBuild()
            ? $"Current version: v{running} (experimental build)"
            : $"Current version: v{running}";
        SelectChannel(UpdateChannels.Normalize(current.UpdateChannel));
        LogsPathText.Text = "Logs: " + AppSettingsService.Instance.GetLogsFolder();

        // Reflect the actual on-disk state (registry entry / .lnk existence), not a stored flag,
        // so the checkboxes stay honest even if the user changed things outside the app.
        ContextMenuBox.IsChecked = ShellIntegrationService.IsRegistered();
        StartMenuBox.IsChecked = ShortcutService.IsInstalled();
        DesktopShortcutBox.IsChecked = ShortcutService.IsDesktopInstalled();

        // Not awaited: the window opens immediately and the channel line fills itself in when
        // GitHub answers, rather than holding Settings shut for the length of an HTTP request.
        Loaded += async (_, _) => await LoadChannelStatusAsync();
    }

    private void BrowseGamePath_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Select the game folder" };
        if (dialog.ShowDialog() == true) GamePathBox.Text = dialog.FolderName;
    }

    private void BrowseMappings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select a mappings .usmap file",
            Filter = "Unreal mappings (*.usmap)|*.usmap|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog() == true) MappingsPathBox.Text = dialog.FileName;
    }

    private void ClearMappingsOverride_Click(object sender, RoutedEventArgs e) => MappingsPathBox.Text = "";

    private async void CheckForAppUpdate_Click(object sender, RoutedEventArgs e) =>
        await _mainViewModel.CheckForAppUpdateCommand.ExecuteAsync(null);

    private string SelectedChannel() =>
        UpdateChannels.Normalize((UpdateChannelBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString());

    private void SelectChannel(string channel)
    {
        UpdateChannelBox.SelectedIndex = UpdateChannels.IsExperimental(channel) ? 1 : 0;
        UpdateChannelHint();
    }

    private void UpdateChannel_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        UpdateChannelHint();

    /// Spells out what changing the channel will actually do, since the consequence isn't
    /// symmetrical: going to experimental moves you forward at the next check, while going back
    /// to stable means downgrading to an older build.
    ///
    /// "Moves you forward" is only true while experimental is actually ahead, which is not always -
    /// after a stable release ships, the newest preview is one the stable build has already
    /// absorbed. Promising new features there would send people to older code, so the promise is
    /// withdrawn once _channels says otherwise.
    private void UpdateChannelHint()
    {
        if (ChannelHintText == null) return;

        var wanted = SelectedChannel();
        var saved = UpdateChannels.Normalize(AppSettingsService.Instance.Current.UpdateChannel);
        var behind = _channels?.ExperimentalIsBehindStable == true;

        if (wanted == saved)
        {
            // Already on experimental and it's behind: there's nothing to warn about switching,
            // but the user still needs to know why no update ever arrives.
            if (UpdateChannels.IsExperimental(wanted) && behind)
            {
                ChannelHintText.Text = "You're on the experimental channel, and it's currently behind stable. "
                                       + "Nothing new will be offered here until the next experimental build is published. "
                                       + "Switch to Stable to move onto the newest release.";
                ChannelHintText.Visibility = Visibility.Visible;
                return;
            }

            ChannelHintText.Visibility = Visibility.Collapsed;
            return;
        }

        ChannelHintText.Text = UpdateChannels.IsExperimental(wanted)
            ? behind
                ? "Experimental is currently behind stable, so switching would not get you anything newer — "
                  + "its newest build is older code than the stable release. You'd stay where you are until the "
                  + "next experimental build is published."
                : "After saving, the next update check will offer the newest experimental build."
            : AppUpdateService.IsRunningExperimentalBuild()
                ? "You're running an experimental build. After saving, you'll be offered the current stable release. "
                  + "Its version number is lower than yours, because the build you're on is a preview of a release "
                  + "that has since shipped — it's newer code despite the smaller number."
                : "After saving, updates will come from stable releases only.";
        ChannelHintText.Visibility = Visibility.Visible;
    }

    /// Asks GitHub where the two channels stand, once per window, and fills in the status line.
    ///
    /// Deliberately silent on failure: this is extra context on a settings page, and someone
    /// offline should see the page behave normally rather than get an error about a line they
    /// never asked for. GitHubReleaseService has already logged the reason.
    private async Task LoadChannelStatusAsync()
    {
        try
        {
            _channels = await new AppUpdateService().GetChannelStatusAsync();
        }
        catch
        {
            return;
        }

        if (_channels == null || !IsLoaded) return;

        var stable = _channels.LatestStable?.TagName;
        var experimental = _channels.LatestExperimental?.TagName;
        if (stable == null && experimental == null) return;

        ChannelStatusText.Text = _channels.ExperimentalIsBehindStable
            ? $"Right now: stable is {stable}, experimental is {experimental} — experimental is BEHIND, "
              + "because stable has caught up with it and no newer preview has been published since."
            : $"Right now: stable is {stable ?? "none"}, experimental is {experimental ?? "none"}.";

        ChannelStatusText.Foreground = _channels.ExperimentalIsBehindStable
            ? (System.Windows.Media.Brush)FindResource("WarningBrush")
            : (System.Windows.Media.Brush)FindResource("TextMutedBrush");

        ChannelStatusText.Visibility = Visibility.Visible;
        UpdateChannelHint();
    }

    private void OpenLogsFolder_Click(object sender, RoutedEventArgs e)
    {
        var path = AppSettingsService.Instance.GetLogsFolder();
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private void OpenDisabledModsFolder_Click(object sender, RoutedEventArgs e)
    {
        var path = AppSettingsService.Instance.GetDisabledModsFolder();
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private void ResetDefaults_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "Reset all settings to their defaults? This won't uninstall any mods.",
            "Reset Settings", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        var defaults = new AppSettings();
        GamePathBox.Text = defaults.GamePathOverride;
        EGameCombo.Text = defaults.EGameVersion;
        MappingsPathBox.Text = defaults.MappingsOverridePath;
        AesKeyBox.Text = defaults.AesKeyHex;
        AutoCheckBox.IsChecked = defaults.AutoCheckUE4SSUpdatesOnStartup;
        AutoCheckAppUpdateBox.IsChecked = defaults.CheckForAppUpdatesOnStartup;
        AutoCheckModUpdateBox.IsChecked = defaults.CheckForModUpdatesOnStartup;
        SelectChannel(defaults.UpdateChannel);
    }

    private void ResetAppData_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "This clears all saved settings and forgets every mod this manager has tracked for every game folder " +
            "you've used it with, and clears the cached mappings file (it gets re-extracted automatically next launch).\n\n" +
            "It does NOT delete any mod files - not the ones currently installed in the game, and not the ones sitting " +
            "in the Disabled Mods folder. Those are untouched and stay right where they are. Enabled mods will keep " +
            "loading in-game exactly as before; the app just won't have them in its list anymore, so you'll need to " +
            "run \"Install Mod...\" on them again to get them tracked (which is safe - it just overwrites the same files).\n\n" +
            "The app will restart. Continue?",
            "Reset App Data", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        try
        {
            AppSettingsService.ResetAllAppData();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't fully reset app data: {ex.Message}", "Reset App Data", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var exePath = Process.GetCurrentProcess().MainModule?.FileName;
        if (!string.IsNullOrEmpty(exePath))
            Process.Start(exePath);
        Application.Current.Shutdown();
    }

    private void ResetGame_Click(object sender, RoutedEventArgs e)
    {
        // Close Settings first: the reset dialog is owned by the main window and rebuilds the mod
        // list behind us, so leaving this window open on top would just be showing stale state.
        Close();
        _mainViewModel.ResetGameToVanillaCommand.Execute(null);
    }

    private void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "Uninstall DDS2 Mod Manager?\n\nThis removes the installed program and its Desktop/Start Menu shortcuts, the " +
            "right-click context menu entry, and the Windows \"Apps & Features\" entry.\n\n" +
            "It does NOT touch any mods - not the ones currently installed in the game, and not the ones in the Disabled " +
            "Mods folder. Your settings and mod tracking also stay in %AppData%\\DDS2ModManager in case you reinstall " +
            "later (use Reset App Data first if you want those gone too).\n\n" +
            "The app will close immediately after.",
            "Uninstall DDS2 Mod Manager", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        AppUninstaller.Run();
        Application.Current.Shutdown();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var settings = AppSettingsService.Instance.Current;
        settings.GamePathOverride = string.IsNullOrWhiteSpace(GamePathBox.Text) ? null : GamePathBox.Text.Trim();
        settings.EGameVersion = string.IsNullOrWhiteSpace(EGameCombo.Text) ? "GAME_UE5_3" : EGameCombo.Text.Trim();
        settings.MappingsOverridePath = string.IsNullOrWhiteSpace(MappingsPathBox.Text) ? null : MappingsPathBox.Text.Trim();
        settings.AesKeyHex = string.IsNullOrWhiteSpace(AesKeyBox.Text) ? null : AesKeyBox.Text.Trim();
        settings.AutoCheckUE4SSUpdatesOnStartup = AutoCheckBox.IsChecked ?? true;
        settings.CheckForAppUpdatesOnStartup = AutoCheckAppUpdateBox.IsChecked ?? true;
        settings.CheckForModUpdatesOnStartup = AutoCheckModUpdateBox.IsChecked ?? true;

        var channelChanged = !string.Equals(UpdateChannels.Normalize(settings.UpdateChannel), SelectedChannel(),
            StringComparison.Ordinal);
        settings.UpdateChannel = SelectedChannel();

        AppSettingsService.Instance.Save();
        _mainViewModel.ReapplySettings();
        Close();

        // Check straight away rather than waiting for the next launch - the user just asked to be
        // on a different channel, and doing nothing visible looks like the setting didn't take.
        if (channelChanged)
        {
            LoggingService.Instance.Info($"Update channel set to {settings.UpdateChannel}. Checking for a matching build...");
            _ = _mainViewModel.CheckForAppUpdateCommand.ExecuteAsync(null);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void ContextMenu_Toggled(object sender, RoutedEventArgs e)
    {
        try
        {
            if (ContextMenuBox.IsChecked == true) ShellIntegrationService.Register();
            else ShellIntegrationService.Unregister();
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Error($"Context menu update failed: {ex.Message}");
            ContextMenuBox.IsChecked = ShellIntegrationService.IsRegistered();
        }
    }

    private void StartMenu_Toggled(object sender, RoutedEventArgs e)
    {
        try
        {
            if (StartMenuBox.IsChecked == true) ShortcutService.Install();
            else ShortcutService.Uninstall();
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Error($"Start Menu shortcut update failed: {ex.Message}");
            StartMenuBox.IsChecked = ShortcutService.IsInstalled();
        }
    }

    private void DesktopShortcut_Toggled(object sender, RoutedEventArgs e)
    {
        try
        {
            if (DesktopShortcutBox.IsChecked == true) ShortcutService.InstallDesktop();
            else ShortcutService.UninstallDesktop();
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Error($"Desktop shortcut update failed: {ex.Message}");
            DesktopShortcutBox.IsChecked = ShortcutService.IsDesktopInstalled();
        }
    }
}
