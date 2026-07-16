using System.Diagnostics;
using System.Windows;
using DDS2ModManager.ViewModels;

namespace DDS2ModManager.Views;

public partial class SettingsWindow : Window
{
    private readonly MainViewModel _mainViewModel;

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
        LogsPathText.Text = "Logs: " + AppSettingsService.Instance.GetLogsFolder();

        // Reflect the actual on-disk state (registry entry / .lnk existence), not a stored flag,
        // so the checkboxes stay honest even if the user changed things outside the app.
        ContextMenuBox.IsChecked = ShellIntegrationService.IsRegistered();
        StartMenuBox.IsChecked = ShortcutService.IsInstalled();
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
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var settings = AppSettingsService.Instance.Current;
        settings.GamePathOverride = string.IsNullOrWhiteSpace(GamePathBox.Text) ? null : GamePathBox.Text.Trim();
        settings.EGameVersion = string.IsNullOrWhiteSpace(EGameCombo.Text) ? "GAME_UE5_3" : EGameCombo.Text.Trim();
        settings.MappingsOverridePath = string.IsNullOrWhiteSpace(MappingsPathBox.Text) ? null : MappingsPathBox.Text.Trim();
        settings.AesKeyHex = string.IsNullOrWhiteSpace(AesKeyBox.Text) ? null : AesKeyBox.Text.Trim();
        settings.AutoCheckUE4SSUpdatesOnStartup = AutoCheckBox.IsChecked ?? true;

        AppSettingsService.Instance.Save();
        _mainViewModel.ReapplySettings();
        Close();
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
}
