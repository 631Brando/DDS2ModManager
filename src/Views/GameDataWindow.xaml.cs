using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace DDS2ModManager.Views;

public partial class GameDataWindow : Window
{
    private readonly SaveGameService _saves;
    private readonly GameConfigService _configs;
    private GameConfigFile? _currentConfig;

    public GameDataWindow(GameInstallation game)
    {
        InitializeComponent();
        _saves = new SaveGameService(game);
        _configs = new GameConfigService(game);

        SavesPathText.Text = _saves.SaveFolderExists
            ? $"Saves: {_saves.SaveGamesPath}"
            : $"No save folder found at {_saves.SaveGamesPath} - launch the game once to create it.";

        RefreshSaves();
        RefreshConfigs();
    }

    // ===== Saves =====

    private void RefreshSaves()
    {
        var selectedName = (SavesGrid.SelectedItem as SaveEntry)?.Name;
        SavesGrid.ItemsSource = _saves.GetSaves();
        if (selectedName != null)
            SavesGrid.SelectedItem = SavesGrid.Items.Cast<SaveEntry>().FirstOrDefault(s => s.Name == selectedName);
    }

    private SaveEntry? SelectedSave()
    {
        if (SavesGrid.SelectedItem is SaveEntry s) return s;
        MessageBox.Show("Select a save first.", "Saves", MessageBoxButton.OK, MessageBoxImage.Information);
        return null;
    }

    private void RefreshSaves_Click(object sender, RoutedEventArgs e) => RefreshSaves();

    private void InspectSave_Click(object sender, RoutedEventArgs e)
    {
        var save = SelectedSave();
        if (save == null) return;

        new SaveInspectorWindow(save) { Owner = this }.ShowDialog();
    }

    private void CloneSave_Click(object sender, RoutedEventArgs e)
    {
        var save = SelectedSave();
        if (save == null) return;

        var dialog = new TextPromptWindow("Clone save", $"Name for the copy of '{save.Name}':", save.Name + "_copy")
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true) return;

        if (_saves.Clone(save, dialog.EnteredText) != null) RefreshSaves();
    }

    private void DisableSave_Click(object sender, RoutedEventArgs e)
    {
        var save = SelectedSave();
        if (save == null) return;

        if (!save.IsEnabled)
        {
            MessageBox.Show($"'{save.Name}' is already disabled.", "Saves", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_saves.SetEnabled(save, false)) RefreshSaves();
    }

    private void EnableSave_Click(object sender, RoutedEventArgs e)
    {
        var save = SelectedSave();
        if (save == null) return;

        if (save.IsEnabled)
        {
            MessageBox.Show($"'{save.Name}' is already enabled.", "Saves", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_saves.SetEnabled(save, true)) RefreshSaves();
    }

    private void DeleteSave_Click(object sender, RoutedEventArgs e)
    {
        var save = SelectedSave();
        if (save == null) return;

        var result = MessageBox.Show(
            $"Permanently delete the save '{save.Name}'?\n\n" +
            $"{save.KindDisplay}, {save.SizeDisplay}, last modified {save.LastModifiedDisplay}.\n\n" +
            "This cannot be undone. If you only want to hide it from the game, use Disable instead.",
            "Delete save", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        if (_saves.Delete(save)) RefreshSaves();
    }

    private void BackupSave_Click(object sender, RoutedEventArgs e)
    {
        var save = SelectedSave();
        if (save == null) return;
        _saves.Backup(save);
    }

    private void OpenSavesFolder_Click(object sender, RoutedEventArgs e) => OpenFolder(_saves.SaveGamesPath);

    private void OpenBackupsFolder_Click(object sender, RoutedEventArgs e)
    {
        // Created on demand: opening it before any backup exists should still land somewhere real
        // rather than telling the user the folder doesn't exist.
        var path = System.IO.Path.GetFullPath(_saves.BackupsPath);
        Directory.CreateDirectory(path);
        OpenFolder(path);
    }

    // ===== Config =====

    private void RefreshConfigs()
    {
        ConfigList.ItemsSource = _configs.GetConfigFiles();
    }

    private void ConfigList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _currentConfig = ConfigList.SelectedItem as GameConfigFile;
        if (_currentConfig == null)
        {
            ConfigEditor.Text = "";
            ConfigEditor.IsEnabled = false;
            SaveConfigButton.IsEnabled = false;
            RestoreConfigButton.IsEnabled = false;
            return;
        }

        try
        {
            ConfigEditor.Text = _configs.ReadText(_currentConfig);
            ConfigEditor.IsEnabled = true;
            SaveConfigButton.IsEnabled = true;
            RestoreConfigButton.IsEnabled = _currentConfig.HasBackup;
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Error($"Couldn't read {_currentConfig.Name}: {ex.Message}");
            ConfigEditor.Text = "";
            ConfigEditor.IsEnabled = false;
        }
    }

    private void SaveConfig_Click(object sender, RoutedEventArgs e)
    {
        if (_currentConfig == null) return;
        if (_configs.Save(_currentConfig, ConfigEditor.Text))
        {
            RestoreConfigButton.IsEnabled = true;
            RefreshConfigs();
        }
    }

    private void RestoreConfig_Click(object sender, RoutedEventArgs e)
    {
        if (_currentConfig == null) return;

        var result = MessageBox.Show(
            $"Restore {_currentConfig.Name} to the version from before you first edited it?\n\nYour current changes to this file will be lost.",
            "Restore config", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        if (_configs.RestoreBackup(_currentConfig))
            ConfigEditor.Text = _configs.ReadText(_currentConfig);
    }

    private void OpenConfigFolder_Click(object sender, RoutedEventArgs e) => OpenFolder(_configs.ConfigPath);

    private static void OpenFolder(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                MessageBox.Show($"That folder doesn't exist yet:\n{path}", "Open folder", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Warn($"Couldn't open '{path}': {ex.Message}");
        }
    }
}
