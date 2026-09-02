using System.Collections.Specialized;
using System.ComponentModel;
using System.Reflection;
using System.Windows.Data;
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

    /// Follows the active game, so the taskbar and Alt-Tab say which one is open. Keeps the version
    /// in it either way: a screenshot is usually all a bug report gives you to work from.
    [ObservableProperty] private string windowTitle = $"{AppPaths.AppDisplayName}  {AppVersionDisplay}";

    /// The big heading. Names the game being managed rather than the app, so a screenshot of the
    /// window says which game it was taken on - the mod list alone does not.
    [ObservableProperty] private string headerTitle = AppPaths.AppDisplayName;

    /// Shown next to the title, not just in Settings.
    ///
    /// A bug report that names a version is worth several that don't, and nobody thinks to go
    /// digging through Settings for it before pasting a log into Discord. Includes the commit
    /// when the build carries one - "v1.0.6" does not identify which build somebody is on if
    /// several were cut from that version, and the short SHA does.
    public static string AppVersionDisplay
    {
        get
        {
            var version = "v" + AppUpdateService.Describe(AppUpdateService.GetCurrentVersion());

            // AssemblyInformationalVersion is "1.0.6+<sha>" when the build recorded a commit,
            // and just "1.0.6" when it did not. Only the suffix is useful here.
            var informational = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;

            var plus = informational?.IndexOf('+') ?? -1;
            if (plus > 0 && informational!.Length > plus + 1)
            {
                var sha = informational[(plus + 1)..];
                if (sha.Length > 7) sha = sha[..7];
                version += $"  {sha}";
            }

            return version;
        }
    }

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
    private readonly ModUpdateSourceResolver _updateSources = new();
    private readonly ModUpdateService _modUpdater = new();
    private readonly GitHubReleaseService _github = new();
    private readonly NexusFeedService _nexus = new();
    private readonly NexusIndexService _nexusIndex = new();

    /// The active game's Nexus domain. Not a setting: an unrecognised domain silently returns
    /// nothing rather than failing in a way anyone could diagnose, so it comes from the profile.
    /// Falls back to the default profile's before a game has been detected.
    private string NexusGameDomain => (Game?.Profile ?? GameProfiles.Default).NexusDomain;

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

    /// The UE4SS build the last update replaced, when there is one to go back to.
    ///
    /// Null until an update has actually happened, so the button is absent rather than disabled -
    /// there is nothing useful to say to someone who has never updated.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRestoreUE4SS))]
    private PreviousUE4SSBuild? previousUE4SS;

    public bool CanRestoreUE4SS => PreviousUE4SS != null;
    public IRelayCommand ToggleLogCommand { get; }
    public IRelayCommand SaveLogCommand { get; }
    public IRelayCommand OpenSettingsCommand { get; }
    public IAsyncRelayCommand CheckForAppUpdateCommand { get; }
    public IAsyncRelayCommand ScanForExistingModsCommand { get; }
    public IRelayCommand OpenGameDataCommand { get; }
    public IRelayCommand ResetGameToVanillaCommand { get; }
    public IAsyncRelayCommand CheckModUpdatesCommand { get; }
    public IAsyncRelayCommand<ModInfo> UpdateModCommand { get; }
    public IRelayCommand<ModInfo> OpenModSourceCommand { get; }
    public IRelayCommand OpenModAuthorGuideCommand { get; }
    public IRelayCommand DismissNexusFeedCommand { get; }
    public IRelayCommand<NexusModPost> OpenNexusModCommand { get; }
    public IRelayCommand OpenNexusGameCommand { get; }
    public IRelayCommand BrowseTrustedModsCommand { get; }
    public IRelayCommand OpenCreditsCommand { get; }

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
        CheckModUpdatesCommand = new AsyncRelayCommand(() => CheckModUpdatesAsync(manual: true));
        UpdateModCommand = new AsyncRelayCommand<ModInfo>(UpdateModAsync);
        OpenModSourceCommand = new RelayCommand<ModInfo>(OpenModSource);
        OpenModAuthorGuideCommand = new RelayCommand(() =>
            new ModAuthorGuideWindow { Owner = System.Windows.Application.Current.MainWindow }.ShowDialog());
        DismissNexusFeedCommand = new RelayCommand(DismissNexusFeed);
        OpenNexusModCommand = new RelayCommand<NexusModPost>(OpenNexusMod);
        OpenNexusGameCommand = new RelayCommand(() =>
            OpenUrl($"https://www.nexusmods.com/{NexusGameDomain}/mods/?sort=lastcreated"));
        BrowseTrustedModsCommand = new RelayCommand(BrowseTrustedMods);
        OpenCreditsCommand = new RelayCommand(OpenCredits);

        InitializeGameTabs();

        // Trust is stored per GitHub ACCOUNT in ModTrustService, not per mod, so ticking one row
        // changes the answer for every other row by that author. Without this those rows would go
        // on showing the old state until the next full refresh, which reads as the tick not
        // working. The tick itself needs no persistence hook here - ModInfo.TrustedAuthor writes
        // straight through to the service, which saves as it goes.
        ModTrustService.Instance.TrustChanged += RefreshTrustOnAllMods;
        WireUndo();

        // The grid binds to this view, not to Mods directly, so the search box can filter without
        // disturbing the underlying list or the DataGrid's own column sorting.
        ModsView = CollectionViewSource.GetDefaultView(Mods);
        ModsView.Filter = MatchesSearch;

        ApplySavedSort();

        // Persist whatever the user sorts by, so the list comes back the way they left it.
        // SortDescriptions raises this whenever the grid replaces the sort from a header click.
        if (ModsView.SortDescriptions is INotifyCollectionChanged sortChanged)
            sortChanged.CollectionChanged += (_, _) => SaveSort();

        // Keep the "3 of 19" line honest when mods are added, imported or removed while a search
        // is active - the count is about the current list, not the list as it was when typed.
        // Two-part grouping is derived from the list too, so it is recomputed here rather than
        // stored, which is what keeps it from going stale after an install or an uninstall.
        Mods.CollectionChanged += (_, _) =>
        {
            UpdateSearchSummary();
            RefreshModGroups();
        };

        // The star and the notes field are edited directly on the row, so nothing else would ever
        // save them. Watching the collection rather than subscribing at each of the several places
        // a mod gets added means a new one can't be missed later.
        Mods.CollectionChanged += (_, e) =>
        {
            foreach (var added in e.NewItems?.OfType<ModInfo>() ?? Enumerable.Empty<ModInfo>())
                added.PropertyChanged += OnModAnnotationChanged;
            foreach (var removed in e.OldItems?.OfType<ModInfo>() ?? Enumerable.Empty<ModInfo>())
                removed.PropertyChanged -= OnModAnnotationChanged;
        };
    }

    /// Persists the user's own annotations the moment they change.
    ///
    /// Only these four properties. Everything else on a ModInfo is written by the code that owns
    /// it and saved deliberately; Upsert calls Save, which rewrites the WHOLE registry file, so
    /// saving on every property change would rewrite it dozens of times during a scan.
    ///
    /// NexusLink is on this list on the same terms as the other three: the user writes it, through
    /// the link dialog, and nothing else does. Nothing may ever pre-fill it from a name match -
    /// that would both storm this file on startup AND persist a guess as the user's own
    /// declaration, which is the one thing NexusModMatcher exists to refuse.
    private void OnModAnnotationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(ModInfo.IsFavourite) or nameof(ModInfo.Notes)
                                   or nameof(ModInfo.Tags) or nameof(ModInfo.NexusLink)))
            return;
        if (sender is not ModInfo mod) return;

        _registry?.Upsert(mod);

        // Favourites sort to the top, so starring one has to re-order the list immediately rather
        // than at the next refresh.
        if (e.PropertyName == nameof(ModInfo.IsFavourite)) ModsView.Refresh();
    }

    private void RefreshTrustOnAllMods()
    {
        foreach (var mod in Mods) mod.RefreshTrust();
    }

    private void OpenCredits() =>
        new CreditsWindow { Owner = System.Windows.Application.Current.MainWindow }.ShowDialog();

    private void BrowseTrustedMods()
    {
        if (Game == null)
        {
            StatusMessage = "Locate the game folder first.";
            return;
        }

        new TrustedModsWindow(this) { Owner = System.Windows.Application.Current.MainWindow }.ShowDialog();
    }

    /// How many installed mods currently have a newer release waiting. Drives the banner - kept
    /// as a count rather than recomputed in the view so the banner and the grid can never
    /// disagree about how many there are.
    [ObservableProperty] private int modUpdatesAvailable;

    [ObservableProperty] private string modUpdateBannerText = "";

    /// Drives the banner's visibility. A bool rather than binding the count through a
    /// converter: the existing ZeroCountToVisibilityConverter shows its target when the count
    /// IS zero (it's used for empty-state placeholders), so reusing it here would have shown
    /// the banner precisely when there was nothing to say.
    [ObservableProperty] private bool hasModUpdates;

    /// Checks every mod that declares a ModUpdateUrl.
    ///
    /// Results are cached for six hours (see ModUpdateService.CheckInterval) unless the user
    /// asked for this explicitly, because unauthenticated GitHub only allows 60 requests an
    /// hour per IP and a user with thirty mods would burn half of that on one startup.
    private async Task CheckModUpdatesAsync(bool manual = false)
    {
        var log = LoggingService.Instance;
        try
        {
            // Two passes, and they do different jobs.
            //
            // Discovery finds a source for mods that have none - including the LogicMod half,
            // which needs the game mounted to read ModUpdateUrl off the ModActor. That mount is
            // far too expensive for startup, so it only happens here, and only if some LogicMod
            // still has no source.
            if (Game != null)
            {
                var discovered = await Task.Run(() => ResolveUpdateSources(Game));
                if (discovered > 0) log.Info($"Found an update source for {discovered} mod(s).");
            }

            // Re-reading picks up manifests that CHANGED since install, which discovery skips
            // because those mods already have a source. That is what catches a moved update
            // address - see ModInfo.UpdateUrlChanged.
            RefreshManifestDeclarations();

            var result = await _modUpdater.CheckAllAsync(
                Mods,
                force: manual,
                progress: new Progress<string>(s => { if (manual) StatusMessage = s; }));

            RefreshModUpdateBanner();

            // Whatever we learned (latest version, when we last looked) is worth keeping - it is
            // what lets the grid stay useful offline instead of blanking out.
            _registry?.Save();

            if (!manual) return;

            if (!result.Succeeded)
                log.Warn(result.Error ?? "Couldn't check for mod updates.");
            else if (result.Checked == 0 && result.Skipped == 0)
                log.Info("None of your mods publish an update address yet, so there's nothing to check. " +
                         "Authors can add one with a ModUpdateUrl variable on their ModActor, or a " +
                         $"{ModManifest.FileName} file.");
            else if (result.UpdatesFound == 0)
                log.Info($"Checked {result.Checked} mod(s) - everything is up to date.");

            StatusMessage = "Ready";
        }
        catch (Exception ex)
        {
            log.Error($"Mod update check failed: {ex.Message}");
        }
    }

    /// Re-reads .dds2mod.json for installed mods, so a manifest added after install is picked
    /// up. Cheap - a file existence check per mod, no CUE4Parse mount.
    ///
    /// A mod whose URL came from its ModActor is left alone: re-reading that needs a full game
    /// mount (Deep Scan), and a manifest sitting in a shared folder must not be able to
    /// override what the mod's own packaged ModActor said.
    private void RefreshManifestDeclarations()
    {
        foreach (var mod in Mods)
        {
            if (mod.UpdateSource?.Declaration == ModUpdateDeclaration.BlueprintVariable) continue;

            var found = _updateSources.FromManifest(mod);
            if (found is not { IsUsable: true }) continue;

            var previous = mod.ModUpdateUrl;

            // Adopt, not assign: this also records where the mod pointed the first time we saw it,
            // which is the only thing a later move can be detected against.
            mod.AdoptUpdateSource(found);

            // A manifest that now points somewhere else than the one we recorded is worth
            // flagging rather than quietly adopting. A manifest on disk can be rewritten by
            // anything that can write to the mods folder, so this is not a hypothetical.
            if (!string.IsNullOrEmpty(previous) &&
                !string.Equals(previous, found.DeclaredUrl, StringComparison.OrdinalIgnoreCase))
            {
                LoggingService.Instance.Warn(
                    $"'{mod.Name}' update address changed on disk ({previous} -> {found.DeclaredUrl}). " +
                    "No update will be offered for it until you've confirmed that's expected.");
            }
        }
    }

    /// Downloads and installs one mod's update, after the user has seen where it comes from.
    ///
    /// Order matters and is deliberate: the new version is downloaded and verified to exist
    /// BEFORE the old one is removed. Uninstalling first would mean a failed or interrupted
    /// download leaves the user with no mod at all - which is a far worse outcome than an
    /// update that simply didn't happen.
    private async Task UpdateModAsync(ModInfo? mod)
    {
        if (mod == null || _installer == null || _registry == null) return;

        var log = LoggingService.Instance;
        if (mod.UpdateSource is not { IsUsable: true } source)
        {
            log.Warn($"'{mod.Name}' has no usable update address.");
            return;
        }

        var owner = source.Owner;
        var repo = source.Repo;

        try
        {
            IsBusy = true;
            StatusMessage = $"Looking up {mod.Name}...";

            var release = await _github.GetLatestReleaseAsync(owner, repo);
            if (release == null)
            {
                log.Warn($"Couldn't reach the release page for '{mod.Name}'.");
                return;
            }

            // The SAME rule the check used, not a second one. This previously re-resolved the
            // asset inline, which meant an author who named a specific file in their manifest got
            // the update detected via that file and installed from whichever archive sorted first.
            var asset = ModUpdateService.PickAsset(release, source);

            // Detected and installable are different questions. A bare .pak is a real release and
            // worth telling the user about, but ModInstallerService.PrepareInstall throws on
            // anything that isn't a folder or a zip/7z/rar - so the prompt offers the release
            // without offering to install it, rather than failing after they agree.
            var installable = asset != null && ModUpdateService.CanAutoInstall(asset);

            // The prompt is unconditional. Trust changes how much it has to EXPLAIN - it never
            // removes it. This is the only place a user ever sees where an update is coming from,
            // and a mod update is executable content from the author's own repository that has
            // not been through Nexus's virus scanning; a lua mod runs code in the game's process.
            // An account can be compromised and the verified list can go stale, and either of
            // those installing silently would be far worse than one click.
            var prompt = new ModUpdatePromptWindow(
                mod, source, mod.TrustLevel,
                newVersion: ModUpdateService.NormalizeVersion(release.TagName),
                releaseNotes: release.Body,
                releaseUrl: $"https://github.com/{owner}/{repo}/releases/tag/{release.TagName}",
                canAutoInstall: installable,
                urlChanged: mod.UpdateUrlChanged)
            {
                Owner = System.Windows.Application.Current.MainWindow
            };

            var accepted = prompt.ShowDialog() == true;

            // Record the trust decision either way - someone who ticks trust and then decides
            // not to update today still meant to tick it. This writes through to ModTrustService,
            // so it covers every mod by this author, not just this one.
            if (prompt.TrustAuthorChecked && !mod.TrustedAuthor && !mod.UpdateUrlChanged)
                mod.TrustedAuthor = true;

            if (!accepted || asset == null || !installable) return;

            // 1. Download first. Nothing about the installed mod has changed yet, so a failure
            //    here costs the user nothing.
            StatusMessage = $"Downloading {mod.Name} {release.TagName}...";
            // The GUID goes on the DIRECTORY, not the filename. An update is uninstall-then-reinstall,
            // and the reinstall names the mod from what it finds - so prefixing the file itself meant
            // every update renamed the mod to a fresh GUID, taking its profile entry and its history
            // with it. ModUpdateService.DownloadAsync already does it this way.
            var tempDir = Path.Combine(Path.GetTempPath(), $"DDS2MM_modupdate_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            var temp = Path.Combine(tempDir, asset.Name);
            await _github.DownloadAssetAsync(asset.BrowserDownloadUrl, temp,
                new Progress<double>(p => ProgressValue = p));

            if (!File.Exists(temp) || new FileInfo(temp).Length == 0)
            {
                log.Error($"The download for '{mod.Name}' produced no file. Nothing was changed.");
                return;
            }

            // 2. Keep a copy of what is about to be replaced.
            //
            // The download-before-uninstall order above protects against a failed download. This
            // protects against the commoner case it cannot: the update installed fine and the new
            // version is worse. Authors delete old releases, so without this there is no way back.
            _backups?.Capture(mod, $"before updating to {release.TagName}");

            // 3. Only now remove the old version.
            StatusMessage = $"Replacing {mod.Name}...";
            var previousUrl = mod.InstalledUpdateUrl ?? mod.ModUpdateUrl;
            _installer.Uninstall(mod);
            Mods.Remove(mod);

            // 3. Install the downloaded copy through the normal path, so it gets analyzed,
            //    type-checked and conflict-scanned exactly like any other install.
            // The type says which half of a two-part mod this row is, so the replacement row that
            // comes back is the matching one rather than whichever part installed first.
            var installed = await _installer.InstallAsync(temp, mod.Type);
            if (installed == null)
            {
                log.Error($"'{mod.Name}' was removed but the new version could not be installed. " +
                          $"The download is still at {temp} - install it with \"Install Mod\".");
                return;
            }

            // Carry the address this mod was originally installed with onto the new ModInfo, so
            // UpdateUrlChanged still has something to compare against. Deliberately the OLD one:
            // adopting the new version's address here would make a moved update channel
            // undetectable from the very update that moved it.
            installed.InstalledUpdateUrl = previousUrl;

            // The user's own annotations survive an update. ModInfo's own comment promises they do,
            // and this is the only place a ModInfo is REPLACED by a different object: Uninstall
            // removes the old registry row by Id and InstallAsync builds a fresh ModInfo with a
            // fresh Guid, so no merge-on-load could ever have rescued them. Until now all four were
            // silently discarded - a starred mod with a note lost both on every update.
            //
            // Assigned BEFORE Mods.Add below, so the annotation handler is not yet attached and
            // this does not fire four whole-file registry writes in a row.
            installed.IsFavourite = mod.IsFavourite;
            installed.Notes       = mod.Notes;
            installed.Tags        = mod.Tags;
            installed.NexusLink   = mod.NexusLink;

            if (installed.UpdateUrlChanged)
            {
                log.Warn($"'{installed.Name}' now publishes updates at a different address " +
                         $"({previousUrl} -> {installed.ModUpdateUrl ?? "none"}). Worth a look.");
            }

            // Recorded before the notes are cleared below - this is the only place they are ever
            // kept, and "what did that update actually change?" is unanswerable a month later
            // without it.
            _history?.Record(
                installed.Name, "Updated",
                from: mod.InstalledVersion,
                to: ModUpdateService.NormalizeVersion(release.TagName),
                notes: release.Body,
                source: source.RepositoryUrl);

            installed.LatestVersion = ModUpdateService.NormalizeVersion(release.TagName);
            installed.UpdateAvailable = false;
            installed.AvailableUpdateTag = null;
            installed.AvailableUpdateNotes = null;
            installed.AvailableUpdateAssetUrl = null;
            installed.LastUpdateCheck = DateTime.Now;

            // Trust needs no carrying across: it lives in ModTrustService keyed by GitHub account,
            // so the new ModInfo inherits it by asking the same question. And if the address moved,
            // UpdateAuthor is now a different account, which is exactly the case where trust
            // should NOT follow - it doesn't, for free.

            Mods.Add(installed);
            _registry.Upsert(installed);
            ResolveNexusFor(installed);
            ReportModsOnlyPretendingToBeDisabled();

        RunCompatibilityCheck();
            RefreshModUpdateBanner();

            log.Success($"'{installed.Name}' updated to {release.TagName}.");
            try { File.Delete(temp); } catch { /* a leftover temp file is not worth reporting */ }
        }
        catch (Exception ex)
        {
            log.Error($"Updating '{mod.Name}' failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
            ProgressValue = 0;
            StatusMessage = "Ready";
        }
    }

    private async Task InitializeAsync()
    {
        // Before anything reads per-game state: move the old flat %AppData% layout into per-game
        // folders. Runs once, ahead of game detection, because the services built for a game expect
        // to find their files already in the new places.
        LegacyStateMigrationService.RunOnce();

        // Independent of game detection below - the manager itself may have an update even if
        // the game isn't found yet. Fire-and-forget so a slow/unreachable GitHub never blocks
        // startup; failures are logged and otherwise silent (see AppUpdateService).
        if (AppSettingsService.Instance.Current.CheckForAppUpdatesOnStartup)
            _ = CheckForAppUpdateAsync();

        // Kept current in the background. It's only used to label a source as verified, so a slow
        // or unreachable fetch costs nothing - the cached copy stays in use, and no list at all
        // just means nothing shows as verified.
        _ = ModTrustService.Instance.RefreshVerifiedListAsync();

        IsBusy = true;
        StatusMessage = "Detecting game installation...";
        try
        {
            Game = ResolveStartupGame();
            RefreshGameTabs();

            if (Game == null)
            {
                StatusMessage = "No supported game found - pick one from the tabs above, or set a path in Settings.";
                return;
            }

            await SetupForGameAsync(Game);
            RefreshGameTabs();
            StatusMessage = "Ready.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// Which game to open on startup.
    ///
    /// A remembered folder is tried before auto-detection because it can point at an install
    /// detection will never find - a non-Steam copy, or a library Steam has forgotten. The game
    /// that was last open is tried first, so the app reopens where it was left.
    ///
    /// Reads the settings dictionary directly rather than through ForGame(), which would create an
    /// empty section for every supported game as a side effect of merely asking.
    private GameInstallation? ResolveStartupGame()
    {
        var settings = AppSettingsService.Instance.Current;

        var order = GameProfiles.All.OrderByDescending(
            p => string.Equals(p.Id, settings.ActiveGameId, StringComparison.OrdinalIgnoreCase));

        foreach (var profile in order)
        {
            if (!settings.Games.TryGetValue(profile.Id, out var forGame)) continue;
            if (string.IsNullOrWhiteSpace(forGame.GamePathOverride)) continue;

            var candidate = new GameInstallation { RootPath = forGame.GamePathOverride, Profile = profile };
            if (candidate.IsValid) return candidate;

            LoggingService.Instance.Warn(
                $"The saved folder for {profile.DisplayName} is no longer valid - falling back to auto-detect.");
        }

        return _gameDetection.TryAutoDetect();
    }

    private async Task SetupForGameAsync(GameInstallation game)
    {
        // Everything started below belongs to THIS game. Background work compares against this
        // token before writing anything, so a slow fetch that finishes after a switch is dropped
        // rather than landing in the other game's list.
        var context = ++_gameContextVersion;

        GamePathDisplay = game.RootPath;
        WindowTitle = $"{game.Profile.DisplayName} Mod Manager  {AppVersionDisplay}";
        HeaderTitle = $"{game.Profile.DisplayName} Mod Manager";

        // Only for games that can actually contain Oodle-compressed chunks. IoStore titles do; a UE4
        // pak of this era is zlib. Ungated, a DDS1-only user pays a recursive walk of an 11 GB
        // install plus a network download for a native DLL, at every first launch, for a codec their
        // game never uses. May download on first run, so keep it off the UI thread either way.
        if (game.Profile.PakLayout == PakLayout.IoStoreTriple)
            await Task.Run(() => OodleHelper.EnsureOodleAvailable(game));

        _analyzer = CreateAnalyzer();
        _registry = new ModRegistryService(game);
        _installer = new ModInstallerService(game, _analyzer, _registry);

        // Per-install state: tracked mods, history, rollback copies and profiles all belong to one
        // game and must be rebuilt alongside the registry, not resolved from a singleton.
        _history = new ModHistoryService(game);
        _backups = new ModBackupService(game);
        _profiles = new ModProfileService(game);

        DetachModSubscriptions();
        Mods.Clear();
        foreach (var m in _registry.Mods) Mods.Add(m);

        Ue4ssStatus = _ue4ss.GetCurrentStatus(game);
        PreviousUE4SS = UE4SSManagerService.FindPreviousBuild(game);
        ReportLoaderState(game);

        RunCompatibilityCheck();

        // Remember this folder, and that this was the game we were on, so we don't need to
        // re-detect (or re-prompt) next launch. The only writer of both.
        AppSettingsService.Instance.ForGame(game.Profile).GamePathOverride = game.RootPath;
        AppSettingsService.Instance.SetActiveGame(game.Profile);
        AppSettingsService.Instance.Save();

        if (AppSettingsService.Instance.Current.AutoCheckUE4SSUpdatesOnStartup)
            _ = RunForGameAsync(context, CheckUE4SSUpdateAsync);

        // Fire-and-forget, like the app and UE4SS checks above: a slow or unreachable GitHub
        // must never hold up startup. Not forced, so the six-hour cache applies and relaunching
        // repeatedly doesn't burn the unauthenticated rate limit.
        if (AppSettingsService.Instance.Current.CheckForModUpdatesOnStartup)
            _ = RunForGameAsync(context, () => CheckModUpdatesAsync());

        // Same treatment: fire-and-forget, failures logged as warnings. A banner about what's
        // new on Nexus is the last thing that should be allowed to hold up the window.
        _ = RunForGameAsync(context, CheckNexusFeedAsync);

        // Hover-card details, from the locally cached catalogue. Refreshed on a slow cadence, so
        // this is usually a file read and a dictionary build rather than any network work.
        _ = RunForGameAsync(context, () => RefreshNexusDetailsAsync(context));

        // Cheap, local, and worth knowing before anything else is diagnosed today.
        CheckGameVersionChanged();

        // Measures every mod and reports anything whose files changed behind the manager's back.
        // Size and timestamp only, so this is a stat call per file rather than a read.
        RefreshFileState();

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

    /// Says what the game's mod loaders look like, in terms that match what the user can act on.
    ///
    /// Kept separate from the UE4SS status itself because the right thing to say depends on the
    /// game: telling a DDS1 player to "reinstall UE4SS from the button above" would be advice to
    /// press a button that is deliberately not there, for a build that would crash their game.
    private void ReportLoaderState(GameInstallation game)
    {
        var log = LoggingService.Instance;
        var loaders = new ModLoaderService().DetectAll(game);

        foreach (var loader in loaders.Where(l => l.IsInstalled))
        {
            var where = loader.Layout == LoaderLayout.Legacy ? " (older layout, in Binaries\\Win64)" : "";
            log.Info($"Found {loader.DisplayName}{where}{(loader.Version == null ? "" : $" - {loader.Version}")}.");
        }

        // Most DDS1 mods are loose .uasset files, which simply do not load without the unlocker -
        // and the symptom is "the mod does nothing", with no error anywhere to explain it.
        if (game.Profile.SupportsLooseAssets
            && loaders.Any(l => l.Loader == ModLoaders.UnrealModUnlocker && !l.IsInstalled))
        {
            log.Warn(
                "UnrealModUnlocker wasn't found. Mods that ship loose .uasset files - which is most of them for " +
                "this game - won't load without it, and they fail silently rather than reporting anything.");
        }

        var ue4ss = Ue4ssStatus;
        if (ue4ss == null) return;

        if (!ue4ss.IsInstalled)
        {
            log.Warn(ue4ss.CanInstall
                ? "UE4SS is not installed. Logic mods and lua mods will not load until it is."
                : $"UE4SS is not installed. {ue4ss.InstallBlockedReason}");
        }
        else if (ue4ss.Layout == LoaderLayout.Legacy)
        {
            log.Info(
                "UE4SS is installed in its older layout. That's what this game's mods expect - it's read and " +
                "managed as-is, and deliberately never replaced or removed automatically.");
        }
        else if (!ue4ss.IsManagedByUs && ue4ss.CanInstall)
        {
            log.Warn(
                "UE4SS was detected but wasn't installed by this manager, so we can't confirm it's the " +
                "experimental build. If mods don't load, reinstall UE4SS from the button above.");
        }
    }

    /// Runs background work for one game and swallows whatever it produced if the user has since
    /// switched away.
    ///
    /// These tasks are deliberately fire-and-forget so a slow GitHub or Nexus never holds up the
    /// window, which also means any of them can still be running when the active game changes. The
    /// token check is what stops a result arriving for one game and being applied to another.
    private async Task RunForGameAsync(int context, Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Warn($"A background check failed: {ex.Message}");
            return;
        }

        if (IsStaleGameContext(context))
            LoggingService.Instance.Info("Discarded a background result for the game that was open before.");
    }

    /// Names tracked mods that are marked disabled while their files are still inside the game.
    ///
    /// Deliberately a REPORT, not a repair. Fixing it means moving real mod containers out of the
    /// game folder, and doing that automatically across every row on startup - before the user has
    /// asked for anything - is a large, silent, file-moving operation on someone's install. The
    /// honest move is to say exactly what is wrong and let them press Disable, which does the same
    /// thing deliberately and one mod at a time.
    ///
    /// These rows come from adopting a Content\Paks\DisabledMods folder, which never disabled
    /// anything: Unreal enumerates Content\Paks recursively, so the game has been loading them.
    private void ReportModsOnlyPretendingToBeDisabled()
    {
        if (Game == null) return;

        var paks = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Game.PaksPath));

        var pretending = Mods
            .Where(m => !m.IsEnabled && m.Type is ModType.LogicMod or ModType.PatchMod)
            .Where(m => m.InstallFiles.Any(f =>
            {
                try
                {
                    var full = Path.GetFullPath(f);
                    return full.StartsWith(paks + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                           && File.Exists(full);
                }
                catch { return false; }
            }))
            .ToList();

        if (pretending.Count == 0) return;

        LoggingService.Instance.Warn(
            $"{pretending.Count} mod(s) are listed as disabled but their files are still inside the game, so it " +
            $"is still loading them: {string.Join(", ", pretending.Select(m => m.Name))}. Unreal reads everything " +
            "under Content\\Paks, including subfolders like DisabledMods. Press Disable on each to move the files " +
            "out of the game folder, which actually switches them off.");
    }

    private ModAnalyzerService CreateAnalyzer()
    {
        // Game is guaranteed set here (CreateAnalyzer is only called from SetupForGameAsync / ReapplySettings after detection).
        var opts = GameMountService.OptionsFor(Game!);
        return new ModAnalyzerService(Game!, opts.MappingsPath, opts.EGame, opts.AesKeyHex);
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

    private Task BrowseGameFolderAsync() => BrowseGameFolderAsync(null);

    /// Locates a game folder by hand.
    ///
    /// <paramref name="wanted"/> names the game being looked for when the user clicked its tab, so
    /// the prompt says which folder to pick and the result is attributed to that game even if the
    /// folder is named something unexpected. Null means "whatever this folder turns out to be".
    private async Task BrowseGameFolderAsync(GameProfile? wanted)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = $"Select the '{(wanted ?? GameProfiles.Default).SteamFolderName}' folder"
        };

        if (dialog.ShowDialog() != true) return;

        var candidate = new GameInstallation { RootPath = dialog.FolderName };
        if (wanted != null) candidate.Profile = wanted;
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
                .Select(g => wanted == null
                    ? new GameInstallation { RootPath = g! }
                    : new GameInstallation { RootPath = g!, Profile = wanted })
                .FirstOrDefault(g => g.IsValid) ?? candidate;
        }

        if (!candidate.IsValid)
        {
            StatusMessage = $"That doesn't look like a valid {(wanted ?? GameProfiles.Default).DisplayName} " +
                            "install (no Binaries\\Win64 found).";
            LoggingService.Instance.Error($"Invalid game folder selected: {dialog.FolderName}");
            return;
        }

        // Same teardown as a tab switch: whatever was open before must not leak into this game.
        ClearPerGameState();

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
            RefreshGameTabs();
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

            // Step 1c: an archive laid out by destination installs EVERY part, not one. Each
            // part goes through exactly the same analyze-and-install path as a normal mod, so
            // both halves get their own row, their own type and their own ModUpdateUrl.
            if (prepared.DestinationParts.Count > 0)
            {
                var installedParts = 0;
                try
                {
                    // Computed once for the whole set, not per part: only one half carries the
                    // manifest that names the mod.
                    var partSetName = _installer.SharedPartName(prepared.DestinationParts);

                    foreach (var part in prepared.DestinationParts)
                    {
                        // keepExtraction: every part comes out of the same temp folder, so it
                        // has to survive until the last one is done.
                        var partMod = await _installer.InstallFromRootAsync(
                            path, prepared, part, keepExtraction: true, partSetName: partSetName);

                        // Named, not skipped. A part that fails is half a mod on disk, and the
                        // success line below used to scroll straight over the warning saying so.
                        if (partMod == null)
                        {
                            LoggingService.Instance.Warn(
                                $"The '{Path.GetFileName(part)}' part of '{Path.GetFileName(path)}' did not install.");
                            continue;
                        }

                        Mods.Add(partMod);

                        // Nothing installed mid-session got a card until a restart, because the
                        // Nexus pass runs once per game load. Network-free, so safe in a loop.
                        ResolveNexusFor(partMod);
                        installedParts++;
                    }
                }
                finally
                {
                    if (prepared.IsTempExtraction)
                        try { Directory.Delete(prepared.ExtractedRoot, true); } catch { }
                }

                var wanted = prepared.DestinationParts.Count;

                if (installedParts == wanted)
                {
                    RunCompatibilityCheck();
                    RefreshModUpdateBanner();
                    LoggingService.Instance.Success(
                        $"Installed {installedParts} part(s) from '{Path.GetFileName(path)}'.");
                }
                else if (installedParts > 0)
                {
                    // Deliberately NOT rolled back. On a reinstall the already-present half fails
                    // the same-type clash check precisely BECAUSE it is correctly installed, while
                    // the other half succeeds as a genuine update - undoing that would delete the
                    // freshly updated half.
                    RunCompatibilityCheck();
                    RefreshModUpdateBanner();
                    LoggingService.Instance.Warn(
                        $"Installed {installedParts} of {wanted} part(s) from '{Path.GetFileName(path)}' - " +
                        "this mod is incomplete, see the lines above.");
                }
                else
                {
                    LoggingService.Instance.Warn($"Nothing from '{Path.GetFileName(path)}' could be installed.");
                }

                StatusMessage = installedParts == wanted ? "Ready"
                    : installedParts > 0 ? "Installed incomplete." : "Install failed.";
                return;
            }

            // Step 2: analyze + install.
            var mod = await _installer.InstallFromRootAsync(path, prepared, chosenRoot);
            if (mod != null)
            {
                Mods.Add(mod);
                ResolveNexusFor(mod);
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
            var opts = GameMountService.OptionsFor(Game!);

            var game = Game;
            var mods = Mods.ToList();
            var results = await Task.Run(() => _compat.DeepScan(game, mods, opts.MappingsPath, opts.EGame, opts.AesKeyHex));

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

    /// Reads each mod's declared update source. Returns how many were newly found.
    private int ResolveUpdateSources(GameInstallation game)
    {
        var found = 0;

        // Backfill first. A mod that already HAS a source is skipped by the discovery loops below,
        // so anything that acquired one without pinning an address would stay unpinned forever -
        // and UpdateUrlChanged, which compares against the pin, could never fire for it.
        //
        // Two ways to end up here: a registry migrated from a build that stored no pin, and the
        // brief window where one discovery path assigned a source without pinning. Both are
        // indistinguishable now, and both are repaired by taking the current address as the
        // baseline. That is weaker than pinning at install time - if the address ALREADY moved
        // before this ran, the move is now the baseline - but it is the strongest honest claim
        // available for a mod that was found on disk, and it beats leaving the check disarmed.
        foreach (var mod in Mods.Where(m => m.UpdateSource is { IsUsable: true }
                                         && string.IsNullOrWhiteSpace(m.InstalledUpdateUrl)))
        {
            mod.InstalledUpdateUrl = mod.UpdateSource!.DeclaredUrl;
            LoggingService.Instance.Info(
                $"Recorded the update address for '{mod.Name}' as {mod.ModUpdateUrl}. A later change to it will be flagged.");
        }

        foreach (var mod in Mods.Where(m => m.UpdateSource == null))
        {
            var source = _updateSources.FromManifest(mod);
            if (source == null) continue;
            mod.AdoptUpdateSource(source);
            found++;
        }

        // Only mount if a LogicMod might still be hiding a ModUpdateUrl in its ModActor.
        var needMount = Mods.Any(m => m.UpdateSource == null && m.HasModActor);
        if (!needMount) return found;

        var opts = GameMountService.OptionsFor(game);

        try
        {
            using var provider = GameMountService.Mount(opts);
            DataTableAppendScanner.EnableScriptReading(provider);

            foreach (var mod in Mods.Where(m => m.UpdateSource == null && m.HasModActor))
            {
                var source = _updateSources.FromModActor(provider, mod);
                if (source == null) continue;
                mod.AdoptUpdateSource(source);
                found++;
            }
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Warn($"Couldn't read update info from installed paks: {ex.Message}");
        }

        return found;
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
            var channel = UpdateChannels.Normalize(AppSettingsService.Instance.Current.UpdateChannel);
            var check = await _appUpdater.CheckForUpdateAsync(channel);
            if (check.NewerRelease == null)
            {
                // Failure is already logged inside GitHubReleaseService with the specific reason -
                // only report "you're up to date" when we actually got a real answer from GitHub.
                if (manual && check.Succeeded)
                {
                    log.Info($"You're on the latest {channel.ToLowerInvariant()} version (v{AppUpdateService.Describe(AppUpdateService.GetCurrentVersion())}).");

                    // On experimental, "nothing newer" has two very different causes, and the user
                    // can only act on one of them. Say which it is.
                    if (check.Channels is { ExperimentalIsBehindStable: true } channels)
                        log.Info($"The experimental channel is currently behind stable: its newest build is "
                                 + $"{channels.LatestExperimental!.TagName}, which stable release {channels.LatestStable!.TagName} "
                                 + "has already superseded. There's nothing experimental to move to until the next preview "
                                 + "is published.");
                }
                return;
            }

            var release = check.NewerRelease;
            var asset = _appUpdater.FindAsset(release)!;

            // The version number and the code don't always move the same way, so all three cases
            // get said out loud rather than being flattened into "update available".
            log.Info(check.Change switch
            {
                AppUpdateService.VersionChange.Rollback =>
                    $"Switching to the stable channel means moving from v{AppUpdateService.Describe(AppUpdateService.GetCurrentVersion())} back to {release.TagName}.",
                AppUpdateService.VersionChange.SupersedingPreview =>
                    $"{release.TagName} is the finished release your experimental build "
                    + $"(v{AppUpdateService.Describe(AppUpdateService.GetCurrentVersion())}) was previewing. Moving to it goes forwards, even "
                    + "though the version number gets shorter.",
                _ =>
                    $"{AppPaths.AppDisplayName} {release.TagName} is available (you have v{AppUpdateService.Describe(AppUpdateService.GetCurrentVersion())})."
            });

            var prompt = new UpdateAvailableWindow(
                release.TagName,
                AppUpdateService.GetCurrentVersion(),
                release.Body,
                AppUpdateService.GetReleaseUrl(release.TagName),
                check.Change)
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
        var opts = GameMountService.OptionsFor(Game);

        List<UnmanagedMod> found;
        try
        {
            var game = Game;
            var known = _registry.Mods.ToList();
            // Mounts and reads every pak in the game folder, so keep it off the UI thread.
            found = await Task.Run(() => _unmanagedScanner.Scan(game, known, opts.MappingsPath, opts.EGame, opts.AesKeyHex));
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

        // Nothing to check when installing is not allowed for this game. Reporting "an update is
        // available" for a build that crashes it would be an invitation to break the install.
        if (Ue4ssStatus is { CanInstall: false })
        {
            UpdateAvailable = false;
            return;
        }

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

        // Enforced here as well as in the UI. The button is hidden for a game we must not install
        // into, but a hidden button is a presentation detail and this is the one that would break a
        // working game - the only UE4SS build we can fetch crashes on some engine versions.
        if (Ue4ssStatus is { CanInstall: false } blocked)
        {
            LoggingService.Instance.Warn(blocked.InstallBlockedReason ??
                "Installing UE4SS isn't supported for this game.");
            return;
        }

        // Always let the user (re)confirm which build before installing/updating, not just once -
        // switching later should be just as visible and just as clearly explained as the first time.
        //
        // Pre-selected from what is ACTUALLY INSTALLED, falling back to the stored preference only
        // when we did not install it and cannot tell. The setting defaults to Standard, so someone
        // running the Dev build who had never opened this dialog was offered an "update" that came
        // pre-selected on Standard - and accepting it silently took away their live console while
        // the mod still loaded and its actor still spawned. That is indistinguishable from a mod
        // regression from the outside, and the build SHAs do not mention it.
        var installedIsDev = Ue4ssStatus is { IsManagedByUs: true, InstalledAssetName: { } asset }
            ? asset.StartsWith("zDEV-", StringComparison.OrdinalIgnoreCase)
            : AppSettingsService.Instance.Current.PreferredUE4SSBuild == "Dev";

        var dialog = new UE4SSBuildSelectionWindow(installedIsDev)
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
                PreviousUE4SS = UE4SSManagerService.FindPreviousBuild(Game!);
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
        DetachModSubscriptions();
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
