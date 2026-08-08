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

    /// Only things the user might have to act on. Pairs that were checked and found compatible are
    /// deliberately not surfaced here - "these two mods are fine" is not information anyone needs
    /// repeated per mod pair, and it drowned out the entries that do matter. They're still
    /// detected, and still noted in the log.
    public ObservableCollection<ModConflictGroup> Conflicts { get; } = new();

    [ObservableProperty] private string compatibilitySummary = "No mods to check yet.";
    [ObservableProperty] private bool hasConflicts;
    public ObservableCollection<LogEntry> LogEntries => LoggingService.Instance.Entries;

    private readonly GameDetectionService _gameDetection = new();
    private readonly UE4SSManagerService _ue4ss = new();
    private readonly CompatibilityCheckerService _compat = new();
    private readonly AppUpdateService _appUpdater = new();
    private readonly UnmanagedModScannerService _unmanagedScanner = new();

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
    public IAsyncRelayCommand RunDeepScanCommand { get; }
    public IAsyncRelayCommand CheckUE4SSUpdateCommand { get; }
    public IAsyncRelayCommand InstallOrUpdateUE4SSCommand { get; }
    public IRelayCommand ToggleLogCommand { get; }
    public IRelayCommand SaveLogCommand { get; }
    public IRelayCommand OpenSettingsCommand { get; }
    public IAsyncRelayCommand CheckForAppUpdateCommand { get; }
    public IAsyncRelayCommand ScanForExistingModsCommand { get; }
    public IRelayCommand OpenGameDataCommand { get; }
    public IRelayCommand ResetGameToVanillaCommand { get; }

    public MainViewModel()
    {
        InitializeCommand = new AsyncRelayCommand(InitializeAsync);
        BrowseGameFolderCommand = new AsyncRelayCommand(BrowseGameFolderAsync);
        InstallModCommand = new AsyncRelayCommand(InstallModAsync);
        EnableModCommand = new RelayCommand<ModInfo>(EnableMod);
        DisableModCommand = new RelayCommand<ModInfo>(DisableMod);
        UninstallModCommand = new RelayCommand<ModInfo>(UninstallMod);
        ViewFilesCommand = new RelayCommand<ModInfo>(ViewFiles);
        RunDeepScanCommand = new AsyncRelayCommand(RunDeepScanAsync);
        CheckUE4SSUpdateCommand = new AsyncRelayCommand(CheckUE4SSUpdateAsync);
        InstallOrUpdateUE4SSCommand = new AsyncRelayCommand(InstallOrUpdateUE4SSAsync);
        ToggleLogCommand = new RelayCommand(() => IsLogVisible = !IsLogVisible);
        SaveLogCommand = new RelayCommand(SaveLog);
        OpenSettingsCommand = new RelayCommand(OpenSettings);
        CheckForAppUpdateCommand = new AsyncRelayCommand(() => CheckForAppUpdateAsync(manual: true));
        ScanForExistingModsCommand = new AsyncRelayCommand(() => ScanForExistingModsAsync(manual: true));
        OpenGameDataCommand = new RelayCommand(OpenGameData);
        ResetGameToVanillaCommand = new RelayCommand(ResetGameToVanilla);
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

        RunCompatibilityCheck();

        // Remember this folder so we don't need to re-detect (or re-prompt) next launch.
        AppSettingsService.Instance.Current.GamePathOverride = game.RootPath;
        AppSettingsService.Instance.Save();

        if (AppSettingsService.Instance.Current.AutoCheckUE4SSUpdatesOnStartup)
            _ = CheckUE4SSUpdateAsync();

        // Awaited rather than fire-and-forget: this one opens a modal dialog, and racing it
        // against the UE4SS update prompt above would stack two dialogs on the user at once.
        await ScanForExistingModsAsync();

        // Anything still missing its DataTable info can't be row-checked by the fast check, which
        // is what made conflicts appear only after manually pressing Deep Scan. Do that refresh
        // here instead of expecting the user to know about it. Runs only when something actually
        // needs it, so the usual startup doesn't pay for a mount it doesn't need.
        if (CompatibilityCheckerService.NeedsDataTableRefresh(Mods))
        {
            LoggingService.Instance.Info("Some mods are missing DataTable info - refreshing them now...");
            await RunDeepScanAsync();
        }
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

    private void RunCompatibilityCheck() => ApplyConflicts(_compat.CheckConflicts(Mods));

    /// Keeps only the entries that need a decision, and produces the one line the user reads first.
    private void ApplyConflicts(IEnumerable<ModConflictGroup> results)
    {
        Conflicts.Clear();
        foreach (var c in results.Where(c => c.Severity != ConflictSeverity.Info))
            Conflicts.Add(c);

        HasConflicts = Conflicts.Count > 0;

        CompatibilitySummary = Mods.Count == 0
            ? "No mods installed."
            : HasConflicts
                ? $"{Conflicts.Count} conflict{(Conflicts.Count == 1 ? "" : "s")} need attention."
                : "No conflicts found.";
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

            ApplyConflicts(results);

            // Persist any refreshed asset-path lists the deep scan produced.
            _registry?.Save();

            StatusMessage = $"Deep scan: {CompatibilitySummary}";
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

            var prompt = new UpdateAvailableWindow(
                release.TagName,
                AppUpdateService.GetCurrentVersion(),
                release.Body,
                AppUpdateService.GetReleaseUrl(release.TagName))
            {
                Owner = System.Windows.Application.Current.MainWindow
            };
            if (prompt.ShowDialog() != true) return;

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

    /// Finds mods already sitting in the game folders that we aren't tracking (i.e. installed by
    /// hand before this manager was used) and offers to adopt them. Runs automatically once per
    /// game setup so users who modded manually aren't left wondering why the list is empty;
    /// manual=true is the Settings/toolbar button, which also reports when it finds nothing.
    private async Task ScanForExistingModsAsync(bool manual = false)
    {
        if (Game == null || _installer == null || _registry == null) return;

        var log = LoggingService.Instance;
        var settings = AppSettingsService.Instance.Current;
        var mappingsPath = !string.IsNullOrWhiteSpace(settings.MappingsOverridePath) && File.Exists(settings.MappingsOverridePath)
            ? settings.MappingsOverridePath!
            : MappingsProviderService.EnsureExtracted();
        var egame = Enum.TryParse<EGame>(settings.EGameVersion, out var parsed) ? parsed : EGame.GAME_UE5_3;

        List<UnmanagedMod> found;
        try
        {
            var game = Game;
            var known = _registry.Mods.ToList();
            // Mounts and reads every pak in the game folder, so keep it off the UI thread.
            found = await Task.Run(() => _unmanagedScanner.Scan(game, known, mappingsPath, egame, settings.AesKeyHex));
        }
        catch (Exception ex)
        {
            log.Error($"Couldn't scan for existing mods: {ex.Message}");
            return;
        }

        if (found.Count == 0)
        {
            if (manual) log.Info("No untracked mods found - everything in your game folders is already being managed.");
            return;
        }

        log.Warn($"Found {found.Count} mod(s) already installed but not tracked by this manager: " +
                 string.Join(", ", found.Select(m => m.Name)));

        var dialog = new ExistingModsWindow(found)
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        if (dialog.ShowDialog() != true) return;

        var selected = dialog.SelectedMods;
        var fixMisplaced = dialog.FixMisplaced;
        var imported = 0;

        foreach (var item in selected)
        {
            var mod = _installer.ImportUnmanaged(item, fixMisplaced);
            if (mod == null) continue;
            Mods.Add(mod);
            imported++;
        }

        if (imported > 0)
        {
            StatusMessage = $"Imported {imported} existing mod(s).";
            RunCompatibilityCheck();
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

        // Compares against whichever build (Standard/Dev) is currently preferred, so switching
        // builds shows up as "update available" the same way a version bump would.
        var preferDev = AppSettingsService.Instance.Current.PreferredUE4SSBuild == "Dev";
        _latestAsset = _ue4ss.FindAsset(_latestRelease, preferDev);
        if (_latestAsset == null)
        {
            LoggingService.Instance.Warn($"Couldn't find the {(preferDev ? "zDEV-UE4SS_*.zip" : "main UE4SS_*.zip")} asset in the latest release.");
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
        if (Game == null) return;

        // Always let the user (re)confirm which build before installing/updating, not just once -
        // switching later should be just as visible and just as clearly explained as the first time.
        var dialog = new UE4SSBuildSelectionWindow(AppSettingsService.Instance.Current.PreferredUE4SSBuild == "Dev")
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        if (dialog.ShowDialog() != true) return;

        AppSettingsService.Instance.Current.PreferredUE4SSBuild = dialog.UseDevBuild ? "Dev" : "Standard";
        AppSettingsService.Instance.Save();

        if (_latestRelease == null)
            await CheckUE4SSUpdateAsync();
        if (_latestRelease == null) return;

        // Re-resolve regardless of what CheckUE4SSUpdateAsync already cached - the user may have
        // just switched builds in the dialog above, and a stale cached asset from before that
        // switch would silently install the wrong one.
        _latestAsset = _ue4ss.FindAsset(_latestRelease, dialog.UseDevBuild);
        if (_latestAsset == null)
        {
            StatusMessage = $"Couldn't find the {(dialog.UseDevBuild ? "zDEV-UE4SS_*.zip" : "main UE4SS_*.zip")} asset in the latest release.";
            return;
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

    private void OpenGameData()
    {
        if (Game == null)
        {
            LoggingService.Instance.Warn("Find the game folder first - saves and config live alongside the game install.");
            return;
        }

        new GameDataWindow(Game) { Owner = System.Windows.Application.Current.MainWindow }.ShowDialog();
    }

    private void ResetGameToVanilla()
    {
        if (Game == null || _installer == null || _registry == null) return;

        var dialog = new ResetGameWindow(_registry.Mods.Count)
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        if (dialog.ShowDialog() != true) return;

        var reset = new GameResetService(Game, _installer, _registry);
        var result = reset.Reset(dialog.Options);

        // The registry drives this list, and Uninstall already emptied it - resync rather than
        // trying to mirror each individual removal.
        Mods.Clear();
        foreach (var m in _registry.Mods) Mods.Add(m);

        Ue4ssStatus = _ue4ss.GetCurrentStatus(Game);
        UpdateAvailable = false;
        RunCompatibilityCheck();

        StatusMessage = result.Failures.Count == 0
            ? "Game reset to vanilla."
            : $"Game reset finished with {result.Failures.Count} problem(s) - see the log.";
    }
}
