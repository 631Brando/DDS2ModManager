using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CUE4Parse.UE4.Versions;
using DDS2ModManager.Views;

namespace DDS2ModManager.ViewModels;

public partial class MainViewModel : ObservableObject
{
    [ObservableProperty] private GameInstallation? game;
    [ObservableProperty] private UE4SSInstallInfo? ue4ssStatus;
    [ObservableProperty] private string statusMessage = "Starting up...";
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private double progressValue;
    [ObservableProperty] private bool isLogVisible = true;
    [ObservableProperty] private bool updateAvailable;
    [ObservableProperty] private string gamePathDisplay = "Not detected";

    public ObservableCollection<ModInfo> Mods { get; } = new();
    public ObservableCollection<ModConflictGroup> Conflicts { get; } = new();
    public ObservableCollection<LogEntry> LogEntries => LoggingService.Instance.Entries;

    private readonly GameDetectionService _gameDetection = new();
    private readonly UE4SSManagerService _ue4ss = new();
    private readonly CompatibilityCheckerService _compat = new();
    private readonly AppUpdateService _appUpdater = new();

    private ModRegistryService? _registry;
    private ModAnalyzerService? _analyzer;
    private ModInstallerService? _installer;

    private GitHubReleaseInfo? _latestRelease;
    private GitHubAsset? _latestAsset;

    public IAsyncRelayCommand InitializeCommand { get; }
    public IAsyncRelayCommand BrowseGameFolderCommand { get; }
    public IAsyncRelayCommand InstallModCommand { get; }
    public IRelayCommand<ModInfo> EnableModCommand { get; }
    public IRelayCommand<ModInfo> DisableModCommand { get; }
    public IRelayCommand<ModInfo> UninstallModCommand { get; }
    public IRelayCommand<ModInfo> ViewFilesCommand { get; }
    public IRelayCommand RunCompatibilityCheckCommand { get; }
    public IAsyncRelayCommand RunDeepScanCommand { get; }
    public IAsyncRelayCommand CheckUE4SSUpdateCommand { get; }
    public IAsyncRelayCommand InstallOrUpdateUE4SSCommand { get; }
    public IRelayCommand CreateLogicModsFolderCommand { get; }
    public IRelayCommand ToggleLogCommand { get; }
    public IRelayCommand SaveLogCommand { get; }
    public IRelayCommand OpenSettingsCommand { get; }
    public IAsyncRelayCommand CheckForAppUpdateCommand { get; }

    public MainViewModel()
    {
        InitializeCommand = new AsyncRelayCommand(InitializeAsync);
        BrowseGameFolderCommand = new AsyncRelayCommand(BrowseGameFolderAsync);
        InstallModCommand = new AsyncRelayCommand(InstallModAsync);
        EnableModCommand = new RelayCommand<ModInfo>(EnableMod);
        DisableModCommand = new RelayCommand<ModInfo>(DisableMod);
        UninstallModCommand = new RelayCommand<ModInfo>(UninstallMod);
        ViewFilesCommand = new RelayCommand<ModInfo>(ViewFiles);
        RunCompatibilityCheckCommand = new RelayCommand(RunCompatibilityCheck);
        RunDeepScanCommand = new AsyncRelayCommand(RunDeepScanAsync);
        CheckUE4SSUpdateCommand = new AsyncRelayCommand(CheckUE4SSUpdateAsync);
        InstallOrUpdateUE4SSCommand = new AsyncRelayCommand(InstallOrUpdateUE4SSAsync);
        CreateLogicModsFolderCommand = new RelayCommand(CreateLogicModsFolder);
        ToggleLogCommand = new RelayCommand(() => IsLogVisible = !IsLogVisible);
        SaveLogCommand = new RelayCommand(SaveLog);
        OpenSettingsCommand = new RelayCommand(OpenSettings);
        CheckForAppUpdateCommand = new AsyncRelayCommand(() => CheckForAppUpdateAsync(manual: true));
    }

    private async Task InitializeAsync()
    {
        // Independent of game detection below - the manager itself may have an update even if
        // the game isn't found yet. Fire-and-forget so a slow/unreachable GitHub never blocks
        // startup; failures are logged and otherwise silent (see AppUpdateService).
        if (AppSettingsService.Instance.Current.CheckForAppUpdatesOnStartup)
            _ = CheckForAppUpdateAsync();

        IsBusy = true;
        StatusMessage = "Detecting game installation...";
        try
        {
            var settings = AppSettingsService.Instance.Current;

            if (!string.IsNullOrWhiteSpace(settings.GamePathOverride))
            {
                var manual = new GameInstallation { RootPath = settings.GamePathOverride };
                if (manual.IsValid)
                {
                    Game = manual;
                    await SetupForGameAsync(manual);
                    StatusMessage = "Ready.";
                    return;
                }
                LoggingService.Instance.Warn("Saved game path override is no longer valid - falling back to auto-detect.");
            }

            Game = _gameDetection.TryAutoDetect();
            if (Game == null)
            {
                StatusMessage = "Game not found automatically - click \"Browse\" or set a path in Settings.";
                return;
            }

            await SetupForGameAsync(Game);
            StatusMessage = "Ready.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SetupForGameAsync(GameInstallation game)
    {
        GamePathDisplay = game.RootPath;

        // May download the Oodle DLL on first run, so keep it off the UI thread.
        await Task.Run(() => OodleHelper.EnsureOodleAvailable(game));

        _analyzer = CreateAnalyzer();
        _registry = new ModRegistryService(game);
        _installer = new ModInstallerService(game, _analyzer, _registry);

        Mods.Clear();
        foreach (var m in _registry.Mods) Mods.Add(m);

        Ue4ssStatus = _ue4ss.GetCurrentStatus(game);
        if (!Ue4ssStatus.IsInstalled)
        {
            LoggingService.Instance.Warn("UE4SS is not installed. Logic mods and lua mods will not load until it is.");
        }
        else if (!Ue4ssStatus.IsManagedByUs)
        {
            LoggingService.Instance.Warn(
                "UE4SS was detected but wasn't installed by this manager, so we can't confirm it's the " +
                "experimental build. If mods don't load, reinstall UE4SS from the button above.");
        }

        if (!_ue4ss.LogicModsFolderExists(game))
        {
            LoggingService.Instance.Info(
                "Content\\Paks\\LogicMods doesn't exist yet (created by the game on first launch after UE4SS is installed).");
        }

        RunCompatibilityCheck();

        // Remember this folder so we don't need to re-detect (or re-prompt) next launch.
        AppSettingsService.Instance.Current.GamePathOverride = game.RootPath;
        AppSettingsService.Instance.Save();

        if (AppSettingsService.Instance.Current.AutoCheckUE4SSUpdatesOnStartup)
            _ = CheckUE4SSUpdateAsync();
    }

    private ModAnalyzerService CreateAnalyzer()
    {
        var settings = AppSettingsService.Instance.Current;

        var mappingsPath = !string.IsNullOrWhiteSpace(settings.MappingsOverridePath) && File.Exists(settings.MappingsOverridePath)
            ? settings.MappingsOverridePath!
            : MappingsProviderService.EnsureExtracted();

        var egame = Enum.TryParse<EGame>(settings.EGameVersion, out var parsed) ? parsed : EGame.GAME_UE5_3;

        // Game is guaranteed set here (CreateAnalyzer is only called from SetupForGameAsync / ReapplySettings after detection).
        return new ModAnalyzerService(Game!, mappingsPath, egame, settings.AesKeyHex);
    }

    /// Called by SettingsWindow after Save so changes (mappings override, EGame, AES key)
    /// take effect immediately without restarting the app.
    public void ReapplySettings()
    {
        _analyzer = CreateAnalyzer();
        if (Game != null && _registry != null)
            _installer = new ModInstallerService(Game, _analyzer, _registry);
        LoggingService.Instance.Info("Settings applied.");
    }

    private void OpenSettings()
    {
        var window = new SettingsWindow(this)
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        window.ShowDialog();
    }

    private async Task BrowseGameFolderAsync()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select the 'Drug Dealer Simulator 2' folder"
        };

        if (dialog.ShowDialog() != true) return;

        var candidate = new GameInstallation { RootPath = dialog.FolderName };
        if (!candidate.IsValid)
        {
            var trimmed = dialog.FolderName.TrimEnd('\\');
            var guesses = new[]
            {
                trimmed,
                Directory.GetParent(trimmed)?.FullName,
                Directory.GetParent(Directory.GetParent(trimmed)?.FullName ?? "")?.FullName,
                Directory.GetParent(Directory.GetParent(Directory.GetParent(trimmed)?.FullName ?? "")?.FullName ?? "")?.FullName
            };

            candidate = guesses.Where(g => g != null)
                .Select(g => new GameInstallation { RootPath = g! })
                .FirstOrDefault(g => g.IsValid) ?? candidate;
        }

        if (!candidate.IsValid)
        {
            StatusMessage = "That doesn't look like a valid DDS2 install (no Binaries\\Win64 found).";
            LoggingService.Instance.Error($"Invalid game folder selected: {dialog.FolderName}");
            return;
        }

        Game = candidate;
        IsBusy = true;
        try
        {
            await SetupForGameAsync(candidate);
            StatusMessage = "Ready.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task InstallModAsync()
    {
        if (_installer == null)
        {
            StatusMessage = "Locate the game folder first.";
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select a mod archive",
            Filter = "Mod archives (*.zip;*.7z;*.rar)|*.zip;*.7z;*.rar|All files (*.*)|*.*"
        };

        if (dialog.ShowDialog() != true) return;
        await InstallFromPathAsync(dialog.FileName);
    }

    public async Task InstallFromPathAsync(string path)
    {
        if (_installer == null)
        {
            StatusMessage = "Locate the game folder first.";
            return;
        }

        IsBusy = true;
        StatusMessage = $"Installing {Path.GetFileName(path)}...";
        try
        {
            // Step 1: extract + detect variants (off the UI thread since it touches disk).
            var prepared = await Task.Run(() => _installer.PrepareInstall(path));

            string chosenRoot;
            if (prepared.VariantCandidates.Count > 1)
            {
                // Step 1b: multiple self-contained versions in one archive - ask the user.
                LoggingService.Instance.Info(
                    $"Detected {prepared.VariantCandidates.Count} versions inside this archive - asking which to install.");

                var dialog = new VariantSelectionWindow(prepared.VariantCandidates)
                {
                    Owner = System.Windows.Application.Current.MainWindow
                };

                if (dialog.ShowDialog() != true || dialog.SelectedPath == null)
                {
                    StatusMessage = "Install cancelled.";
                    LoggingService.Instance.Info("Install cancelled - no version selected.");
                    if (prepared.IsTempExtraction)
                        try { Directory.Delete(prepared.ExtractedRoot, true); } catch { }
                    return;
                }

                chosenRoot = dialog.SelectedPath;
                LoggingService.Instance.Info($"Selected version: {Path.GetFileName(chosenRoot)}");
            }
            else
            {
                chosenRoot = prepared.VariantCandidates.Count == 1
                    ? prepared.VariantCandidates[0]
                    : prepared.ExtractedRoot;
            }

            // Step 2: analyze + install.
            var mod = await _installer.InstallFromRootAsync(path, prepared, chosenRoot);
            if (mod != null)
            {
                Mods.Add(mod);
                RunCompatibilityCheck();
                StatusMessage = $"Installed '{mod.Name}'.";
            }
            else
            {
                StatusMessage = "Install failed or was blocked - see log for details.";
            }
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Error($"Install failed: {ex.Message}");
            StatusMessage = "Install failed - see log for details.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void EnableMod(ModInfo? mod)
    {
        if (mod == null || _installer == null) return;
        _installer.Enable(mod);
        RunCompatibilityCheck();
    }

    private void DisableMod(ModInfo? mod)
    {
        if (mod == null || _installer == null) return;
        _installer.Disable(mod);
        RunCompatibilityCheck();
    }

    private void UninstallMod(ModInfo? mod)
    {
        if (mod == null || _installer == null) return;
        _installer.Uninstall(mod);
        Mods.Remove(mod);
        RunCompatibilityCheck();
    }

    private void ViewFiles(ModInfo? mod)
    {
        if (mod == null) return;
        var window = new ModFilesWindow(mod)
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        window.ShowDialog();
    }

    private void RunCompatibilityCheck()
    {
        Conflicts.Clear();
        foreach (var c in _compat.CheckConflicts(Mods)) Conflicts.Add(c);
    }

    private async Task RunDeepScanAsync()
    {
        if (Game == null)
        {
            StatusMessage = "Locate the game folder first.";
            return;
        }

        IsBusy = true;
        StatusMessage = "Running deep scan (reading installed paks in place)...";
        try
        {
            var settings = AppSettingsService.Instance.Current;
            var mappingsPath = !string.IsNullOrWhiteSpace(settings.MappingsOverridePath) && File.Exists(settings.MappingsOverridePath)
                ? settings.MappingsOverridePath!
                : MappingsProviderService.EnsureExtracted();
            var egame = Enum.TryParse<EGame>(settings.EGameVersion, out var parsed) ? parsed : EGame.GAME_UE5_3;

            var game = Game;
            var mods = Mods.ToList();
            var results = await Task.Run(() => _compat.DeepScan(game, mods, mappingsPath, egame, settings.AesKeyHex));

            Conflicts.Clear();
            foreach (var c in results) Conflicts.Add(c);

            // Persist any refreshed asset-path lists the deep scan produced.
            _registry?.Save();

            StatusMessage = results.Count == 0 ? "Deep scan: no conflicts." : $"Deep scan: {results.Count} conflict(s).";
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Error($"Deep scan failed: {ex.Message}");
            StatusMessage = "Deep scan failed - see log.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void SaveLog()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save log to file",
            Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
            FileName = $"DDS2ModManager_log_{DateTime.Now:yyyyMMdd_HHmmss}.txt"
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            LoggingService.Instance.ExportToFile(dialog.FileName);
            StatusMessage = $"Log saved to {dialog.FileName}";
            LoggingService.Instance.Success($"Log exported to {dialog.FileName}");
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Error($"Failed to save log: {ex.Message}");
        }
    }

    /// Checks GitHub for a newer DDS2ModManager release (not to be confused with
    /// CheckUE4SSUpdateAsync below, which checks UE4SS-RE's release instead). Prompts to update
    /// on launch (manual=false, silent when up to date) or reports either way when triggered
    /// from Settings' "Check for Updates" button (manual=true).
    private async Task CheckForAppUpdateAsync(bool manual = false)
    {
        var log = LoggingService.Instance;
        try
        {
            var check = await _appUpdater.CheckForUpdateAsync();
            if (check.NewerRelease == null)
            {
                // Failure is already logged inside GitHubReleaseService with the specific reason -
                // only report "you're up to date" when we actually got a real answer from GitHub.
                if (manual && check.Succeeded) log.Info($"You're on the latest version (v{AppUpdateService.GetCurrentVersion()}).");
                return;
            }

            var release = check.NewerRelease;
            var asset = _appUpdater.FindAsset(release)!;
            log.Info($"DDS2 Mod Manager {release.TagName} is available (you have v{AppUpdateService.GetCurrentVersion()}).");

            var result = System.Windows.MessageBox.Show(
                $"A new version is available: {release.TagName} (you have v{AppUpdateService.GetCurrentVersion()}).\n\n" +
                "Update now? The app will download it and restart.",
                "Update Available", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Information);
            if (result != System.Windows.MessageBoxResult.Yes) return;

            IsBusy = true;
            StatusMessage = $"Downloading {release.TagName}...";
            await _appUpdater.DownloadAndApplyAsync(asset, new Progress<double>(p => ProgressValue = p));

            // SelfReplaceHelper is already waiting for this process to exit and will relaunch us.
            System.Windows.Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            log.Error($"App update check failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task CheckUE4SSUpdateAsync()
    {
        if (Game == null) return;
        StatusMessage = "Checking for UE4SS updates...";
        _latestRelease = await _ue4ss.GetLatestExperimentalReleaseAsync();
        if (_latestRelease == null)
        {
            StatusMessage = "Couldn't reach GitHub to check for UE4SS updates.";
            return;
        }

        _latestAsset = _ue4ss.FindMainAsset(_latestRelease);
        if (_latestAsset == null)
        {
            LoggingService.Instance.Warn("Couldn't find the main UE4SS_v*.zip asset in the latest release.");
            return;
        }

        var current = Ue4ssStatus;
        UpdateAvailable = !current!.IsInstalled
            || !current.IsManagedByUs
            || !string.Equals(current.InstalledAssetName, _latestAsset.Name, StringComparison.OrdinalIgnoreCase);

        StatusMessage = UpdateAvailable
            ? $"UE4SS update available: {_latestAsset.Name}"
            : "UE4SS is up to date.";
    }

    private async Task InstallOrUpdateUE4SSAsync()
    {
        if (Game == null || _latestRelease == null || _latestAsset == null)
        {
            await CheckUE4SSUpdateAsync();
            if (Game == null || _latestRelease == null || _latestAsset == null) return;
        }

        IsBusy = true;
        var progress = new Progress<double>(p => ProgressValue = p);
        try
        {
            var ok = await _ue4ss.InstallOrUpdateAsync(Game!, _latestRelease!, _latestAsset!, progress);
            if (ok)
            {
                Ue4ssStatus = _ue4ss.GetCurrentStatus(Game!);
                UpdateAvailable = false;
                StatusMessage = "UE4SS installed successfully.";
            }
            else
            {
                StatusMessage = "UE4SS install failed - see log for details.";
            }
        }
        finally
        {
            IsBusy = false;
            ProgressValue = 0;
        }
    }

    private void CreateLogicModsFolder()
    {
        if (Game == null) return;
        _ue4ss.CreateLogicModsFolder(Game);
    }
}
