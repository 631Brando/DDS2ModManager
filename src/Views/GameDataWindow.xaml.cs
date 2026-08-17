using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DDS2ModManager.Views;

public partial class GameDataWindow : Window
{
    private readonly SaveGameService _saves;
    private readonly GameConfigService _configs;
    private readonly SteamCloudStatus _cloud;
    private GameConfigFile? _currentConfig;

    public GameDataWindow(GameInstallation game)
    {
        InitializeComponent();
        _saves = new SaveGameService(game);
        _configs = new GameConfigService(game);

        _cloud = new SteamCloudService(game).GetStatus();
        ShowCloudWarning();

        SavesPathText.Text = _saves.SaveFolderExists
            ? $"Saves: {_saves.SaveGamesPath}"
            : $"No save folder found at {_saves.SaveGamesPath} - launch the game once to create it.";

        RefreshSaves();
        RefreshConfigs();
    }

    // ===== Steam Cloud =====

    private void ShowCloudWarning()
    {
        if (!_cloud.IsSyncingSaves) return;

        CloudHeadline.Text = _cloud.Headline;
        CloudDetail.Text = _cloud.Detail;
        CloudHow.Text = _cloud.HowToDisable;
        CloudBanner.Visibility = Visibility.Visible;

        LoggingService.Instance.Info(
            $"Steam Cloud is syncing this game's saves ({_cloud.SyncedFileCount} files, app {_cloud.AppId}).");
    }

    // ===== Saves =====

    private void RefreshSaves()
    {
        // Re-select by name so a bulk action doesn't clear the selection out from under the user
        // mid-way through working on a group of saves.
        var selected = SelectedSaves().Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        SavesGrid.ItemsSource = _saves.GetSaves();

        if (selected.Count == 0) { UpdateSelectionSummary(); return; }

        SavesGrid.SelectedItems.Clear();
        foreach (var item in SavesGrid.Items.Cast<SaveEntry>().Where(s => selected.Contains(s.Name)))
            SavesGrid.SelectedItems.Add(item);

        UpdateSelectionSummary();
    }

    /// Everything currently selected, snapshotted - the grid's own SelectedItems is live, so
    /// iterating it while refreshing the list would throw.
    private List<SaveEntry> SelectedSaves() => SavesGrid.SelectedItems.OfType<SaveEntry>().ToList();

    /// For actions that only make sense on one save at a time (clone needs a name, inspect opens
    /// a window). Says which it is rather than silently acting on whichever came first.
    private SaveEntry? SingleSelectedSave(string action)
    {
        var selected = SelectedSaves();
        switch (selected.Count)
        {
            case 1:
                return selected[0];
            case 0:
                MessageBox.Show("Select a save first.", "Saves", MessageBoxButton.OK, MessageBoxImage.Information);
                return null;
            default:
                MessageBox.Show($"{action} works on one save at a time - {selected.Count} are selected.",
                    "Saves", MessageBoxButton.OK, MessageBoxImage.Information);
                return null;
        }
    }

    private void SavesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelectionSummary();

    private void UpdateSelectionSummary()
    {
        UpdateSelectionDependentButtons();

        var selected = SelectedSaves();
        if (selected.Count <= 1)
        {
            SelectionSummary.Text = "";
            return;
        }

        var bytes = selected.Sum(s => s.SizeBytes);
        var size = bytes < 1024L * 1024
            ? $"{bytes / 1024.0:F0} KB"
            : $"{bytes / 1024.0 / 1024.0:F1} MB";
        SelectionSummary.Text = $"{selected.Count} saves selected  ({size})";
    }

    private void RefreshSaves_Click(object sender, RoutedEventArgs e) => RefreshSaves();

    /// Runs a bulk action and reports it once at the end, rather than one dialog per save.
    private void ForEachSelected(string verb, Func<SaveEntry, bool> action, Func<SaveEntry, string?>? skip = null)
    {
        var selected = SelectedSaves();
        if (selected.Count == 0)
        {
            MessageBox.Show("Select a save first.", "Saves", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        int done = 0, skipped = 0;
        var failed = new List<string>();

        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            foreach (var save in selected)
            {
                if (skip?.Invoke(save) != null) { skipped++; continue; }
                if (action(save)) done++;
                else failed.Add(save.Name);
            }
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }

        RefreshSaves();

        if (failed.Count > 0)
        {
            MessageBox.Show(
                $"{verb} {done} of {selected.Count} saves.\n\nThese failed:\n  " +
                string.Join("\n  ", failed.Take(10)) +
                (failed.Count > 10 ? $"\n  ...and {failed.Count - 10} more" : "") +
                "\n\nThe log has the reason for each.",
                "Saves", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        else if (selected.Count > 1)
        {
            LoggingService.Instance.Success(
                $"{verb} {done} saves" + (skipped > 0 ? $" ({skipped} already in that state)." : "."));
        }
    }

    private void InspectSave_Click(object sender, RoutedEventArgs e)
    {
        var save = SingleSelectedSave("Inspect");
        if (save == null) return;

        new SaveInspectorWindow(save) { Owner = this }.ShowDialog();
    }

    private void CloneSave_Click(object sender, RoutedEventArgs e)
    {
        var save = SingleSelectedSave("Clone");
        if (save == null) return;

        var dialog = new TextPromptWindow(
            "Clone save",
            $"Name for the copy of '{save.Name}':",
            save.Name + "_copy",
            // Cloning writes a new folder into the synced tree, so Steam gets a say in whether it
            // survives. Better said here than discovered when the copy vanishes.
            _cloud.IsSyncingSaves
                ? $"{_cloud.ShortWarning} The copy may be removed again when the game next launches."
                : null)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true) return;

        if (_saves.Clone(save, dialog.EnteredText) != null) RefreshSaves();
    }

    /// Enable and Disable share a button. The action is "Enable" only when everything selected is
    /// already disabled - otherwise it's "Disable". That way the common cases (a group of live
    /// saves you want out of the way, or a group of hidden ones you want back) each take one
    /// click, and a mixed selection resolves to the action that changes something rather than
    /// silently doing nothing.
    private bool EnableIsTheAction()
    {
        var selected = SelectedSaves();
        return selected.Count > 0 && selected.All(s => !s.IsEnabled);
    }

    /// Keeps the selection-dependent buttons in step with what's actually selected: greyed out
    /// when nothing is, and the toggle labelled with the direction it will go.
    private void UpdateSelectionDependentButtons()
    {
        var count = SelectedSaves().Count;

        InspectButton.IsEnabled = count > 0;
        CloneButton.IsEnabled = count > 0;
        BackUpButton.IsEnabled = count > 0;
        DeleteButton.IsEnabled = count > 0;
        ToggleEnabledButton.IsEnabled = count > 0;

        // Single word, so it always fits the shared button width and the row can't shuffle.
        ToggleEnabledButton.Content = EnableIsTheAction() ? "Enable" : "Disable";
    }

    private void ToggleEnabled_Click(object sender, RoutedEventArgs e)
    {
        var enable = EnableIsTheAction();

        // Disabling moves the save out of the synced folder, which Steam reads as a deletion - so
        // it can disappear from the cloud and from other machines. That's a bigger consequence
        // than "hidden from the game", so it gets confirmed rather than just done.
        if (!enable && _cloud.IsSyncingSaves)
        {
            var count = SelectedSaves().Count;
            var answer = MessageBox.Show(
                $"Disabling moves {(count == 1 ? "this save" : $"these {count} saves")} out of the game's save folder, " +
                "and Steam Cloud is syncing that folder.\n\n" +
                "Steam may treat the save as deleted and remove it from the cloud, which would also remove it from " +
                "your other machines. Re-enabling puts it back locally and Steam should upload it again, but if the " +
                "save matters, use Back Up first - backups are kept outside the synced folder.\n\nContinue?",
                "Steam Cloud", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;
        }

        ForEachSelected(enable ? "Enabled" : "Disabled",
            save => _saves.SetEnabled(save, enable),
            // Saves already in the target state are skipped rather than counted as failures, so a
            // mixed selection just brings the rest into line.
            save => save.IsEnabled == enable ? $"already {(enable ? "enabled" : "disabled")}" : null);
    }

    private void BackupSave_Click(object sender, RoutedEventArgs e) =>
        ForEachSelected("Backed up", save => _saves.Backup(save));

    private void DeleteSave_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedSaves();
        if (selected.Count == 0)
        {
            MessageBox.Show("Select a save first.", "Saves", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // One confirmation for the whole batch, but it has to name what's going, because deleting
        // the wrong save is unrecoverable and a bare count is easy to misread.
        string message;
        if (selected.Count == 1)
        {
            var save = selected[0];
            message = $"Permanently delete the save '{save.Name}'?\n\n" +
                      $"{save.KindDisplay}, {save.SizeDisplay}, last modified {save.LastModifiedDisplay}.";
        }
        else
        {
            var totalBytes = selected.Sum(s => s.SizeBytes);
            var total = totalBytes < 1024L * 1024
                ? $"{totalBytes / 1024.0:F0} KB"
                : $"{totalBytes / 1024.0 / 1024.0:F1} MB";

            message = $"Permanently delete these {selected.Count} saves ({total})?\n\n  " +
                      string.Join("\n  ", selected.Take(15).Select(s => $"{s.Name}  ({s.SizeDisplay})")) +
                      (selected.Count > 15 ? $"\n  ...and {selected.Count - 15} more" : "");
        }

        message += "\n\nThis cannot be undone. To only hide them from the game, use Disable instead.";

        // Worth repeating here even though the banner says it: this is the moment it matters, and
        // "I deleted it and it came back" is a confusing way to find out.
        if (_cloud.IsSyncingSaves)
            message += $"\n\n{_cloud.ShortWarning} A deleted save can be restored from the cloud, " +
                       "so it may reappear.";

        var result = MessageBox.Show(
            message,
            selected.Count == 1 ? "Delete save" : $"Delete {selected.Count} saves",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        ForEachSelected("Deleted", save => _saves.Delete(save));
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
        // Grouped by kind. GetConfigFiles returns the game's files first and the mod loader's
        // last, and ListCollectionView keeps that order, so "Game config" stays the heading you
        // land on rather than the list opening on UE4SS.
        var view = new System.Windows.Data.ListCollectionView(_configs.GetConfigFiles());
        view.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(GameConfigFile.Category)));
        ConfigList.ItemsSource = view;
    }

    private void ConfigList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _currentConfig = ConfigList.SelectedItem as GameConfigFile;
        UpdateModLoaderNotice();

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

    /// Spells out what the selected file actually is, at the point the user is about to edit it.
    ///
    /// Named as "the mod loader's" rather than "UE4SS's" first, because someone who installed
    /// UE4SS through this app has never had to care what it is called - and a warning that only
    /// makes sense if you already know the jargon isn't a warning.
    private void UpdateModLoaderNotice()
    {
        if (_currentConfig == null || _currentConfig.IsGameConfig)
        {
            ModLoaderConfigNotice.Visibility = Visibility.Collapsed;
            return;
        }

        ModLoaderConfigNoticeText.Text =
            $"{_currentConfig.Name} configures UE4SS — the mod loader that runs your Lua mods — not Drug Dealer "
            + "Simulator 2. Changing it won't alter anything in the game itself; it's where things like the "
            + "debug console and UE4SS's own keybinds live.\n\n"
            + $"It sits in the mod loader's folder ({_currentConfig.Folder}), not with the game's config files. "
            + "Reinstalling or updating UE4SS replaces it — though once you've edited it here, your version is "
            + "kept and the update leaves it alone.";

        ModLoaderConfigNotice.Visibility = Visibility.Visible;
    }

    /// Follows the selection: the game's config files and the mod loader's are in different places,
    /// so a single fixed folder would open the wrong one half the time.
    private void OpenConfigFolder_Click(object sender, RoutedEventArgs e) =>
        OpenFolder(_currentConfig is { } c && Directory.Exists(c.Folder) ? c.Folder : _configs.ConfigPath);

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
