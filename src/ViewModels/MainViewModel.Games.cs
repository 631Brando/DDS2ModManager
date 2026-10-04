using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DDS2ModManager.ViewModels;

/// Which game the manager is looking at, and every way of changing it.
///
/// The dangerous part is not the switch itself, it is everything that survives it. The mod list, the
/// multi-select, the undo entry and several fire-and-forget tasks all hold references to the OUTGOING
/// game's services and ModInfo objects. Left alone, a bulk uninstall or an undo performed after a
/// switch would delete files using the previous game's paths, and a background scan finishing late
/// would write its results into the new game's registry.
///
/// So every route - startup, picking a game, adding a folder - goes through ONE gated transition:
/// tear down everything game-specific first, bump the context token, and only then point at the new
/// game. And every route gets its game from ONE resolver (GameCatalogService / GenericGameProfiles),
/// so a folder can't become DDS2 because DDS2 was clicked before it was picked.
public partial class MainViewModel
{
    /// Every game in the picker: installed games, leftovers, and the built-in games even when absent.
    public ObservableCollection<GameEntryViewModel> GameEntries { get; } = new();

    /// The entry for the game currently open, for the header. Null with no game open.
    [ObservableProperty] private GameEntryViewModel? activeEntry;

    [ObservableProperty] private bool isScanningGames;

    private readonly GameCatalogService _catalog = new();

    /// Incremented on every game change. Background work captures it before it starts and drops its
    /// results if it no longer matches - a slow Nexus fetch for DDS1 must not land in DDS2's list.
    private int _gameContextVersion;

    /// The one gate every game transition takes: startup's setup, a pick from the selector, an added
    /// folder. It replaced a bool that only the tab switch checked - startup never set it, Browse
    /// never checked it, and the app-update check cleared IsBusy underneath it, so a click during
    /// startup could run a second setup concurrently with the first.
    private readonly SemaphoreSlim _transitionGate = new(1, 1);

    public bool IsGameTransitionInProgress => _transitionGate.CurrentCount == 0;

    public IAsyncRelayCommand<GameEntryViewModel> SwitchGameCommand { get; private set; } = null!;
    public IAsyncRelayCommand AddGameFolderCommand { get; private set; } = null!;
    public IAsyncRelayCommand RescanGamesCommand { get; private set; } = null!;
    public IRelayCommand<GameEntryViewModel> ForgetGameCommand { get; private set; } = null!;
    public IRelayCommand<GameEntryViewModel> OpenGameFolderCommand { get; private set; } = null!;

    /// Shows the game picker. The window itself is the view's business; the view model only asks.
    public IRelayCommand OpenGamePickerCommand { get; private set; } = null!;

    /// Raised when the window should show the game picker - at startup with no game to open, so the
    /// first thing a user without DDS1 or DDS2 sees is the list of games they CAN manage.
    public event Action? GamePickerRequested;

    private void InitializeGameCatalog()
    {
        // AllowConcurrentExecutions, with the transition gate owning the decision instead. By default
        // an AsyncRelayCommand reports CanExecute=false while it runs, and every entry shares one
        // instance - so the whole picker went dead for the length of a switch (which mounts and reads
        // every pak, seconds on a large install) with no disabled appearance. A click then did nothing
        // at all, indistinguishable from being ignored. The gate refuses re-entry, and says so.
        SwitchGameCommand = new AsyncRelayCommand<GameEntryViewModel>(
            SwitchGameAsync, AsyncRelayCommandOptions.AllowConcurrentExecutions);
        AddGameFolderCommand = new AsyncRelayCommand(() => AddGameFolderAsync(null),
            AsyncRelayCommandOptions.AllowConcurrentExecutions);
        RescanGamesCommand = new AsyncRelayCommand(RefreshCatalogAsync);
        ForgetGameCommand = new RelayCommand<GameEntryViewModel>(ForgetGame);
        OpenGamePickerCommand = new RelayCommand(() => GamePickerRequested?.Invoke());
        OpenGameFolderCommand = new RelayCommand<GameEntryViewModel>(e =>
        {
            if (!string.IsNullOrWhiteSpace(e?.RootPath)) OpenFolderInExplorer(e!.RootPath);
        });

        // Seeded with the built-in games before anything has been scanned, so the picker is never
        // empty - and never claims DDS1 or DDS2 is unsupported - while the real scan runs.
        foreach (var profile in GameProfiles.InDisplayOrder)
            GameEntries.Add(new GameEntryViewModel(new DetectedGame
            {
                RootPath = "",
                Profile = profile,
                State = CatalogState.NotFound
            }));
    }

    /// Re-reads every launcher and library folder for Unreal games, off the UI thread, and updates the
    /// picker in place. Existing entries keep their loaded pictures, so a rescan doesn't flash every
    /// card back to a placeholder.
    public async Task RefreshCatalogAsync()
    {
        if (IsScanningGames) return;
        IsScanningGames = true;
        try
        {
            var found = await Task.Run(() => _catalog.Scan());
            MergeCatalog(found);

            var installed = found.Count(g => g.State == CatalogState.Installed);
            LoggingService.Instance.Info(
                $"Found {installed} installed Unreal Engine game{(installed == 1 ? "" : "s")}" +
                $"{(found.Any(g => g.State == CatalogState.Leftover) ? $" (and leftover files from {found.Count(g => g.State == CatalogState.Leftover)} uninstalled)" : "")}.");
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Warn($"Couldn't scan for games: {ex.Message}");
        }
        finally
        {
            IsScanningGames = false;
        }
    }

    /// Brings GameEntries in line with a scan: updates entries for installs already listed, adds new
    /// ones, removes ones that are gone - keyed by install, never by game id, because two copies of
    /// one game share an id and a ToDictionary over ids threw on exactly that.
    private void MergeCatalog(IReadOnlyList<DetectedGame> found)
    {
        string KeyOf(DetectedGame g) => g.State == CatalogState.NotFound ? "builtin:" + g.Profile.Id : g.Key;
        string KeyOfEntry(GameEntryViewModel e) => KeyOf(e.Game);

        var wanted = found
            .GroupBy(KeyOf, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
        var wantedKeys = wanted.Select(KeyOf).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // The open game stays listed even if this scan missed it (a folder added by hand that the
        // remembered-folder pass would normally pick up, mid-edit). Removing the active row would
        // leave the header pointing at an entry the picker no longer shows.
        foreach (var stale in GameEntries.Where(e => !wantedKeys.Contains(KeyOfEntry(e)) && !e.IsActive).ToList())
            GameEntries.Remove(stale);

        for (var i = 0; i < wanted.Count; i++)
        {
            var game = wanted[i];
            var existing = GameEntries.FirstOrDefault(e => string.Equals(KeyOfEntry(e), KeyOf(game), StringComparison.OrdinalIgnoreCase));

            if (existing == null)
            {
                existing = new GameEntryViewModel(game);
                GameEntries.Insert(Math.Min(i, GameEntries.Count), existing);
            }
            else
            {
                existing.Update(game);
                var at = GameEntries.IndexOf(existing);
                if (at != i && i < GameEntries.Count) GameEntries.Move(at, i);
            }
        }

        MarkActiveEntry();

        foreach (var entry in GameEntries.Where(e => !e.IsNotFound || e.IsFullSupport))
            _ = entry.LoadArtAsync(includeHero: entry.IsActive);
    }

    /// Flags the open game's entry, and points the header at it. Matched by install key: matching by
    /// profile id would mark BOTH copies of a game installed twice as open.
    private void MarkActiveEntry()
    {
        var openKey = Game == null ? null : AppPaths.GameKey(GameStoreIndex.NormalizeFolder(Game.RootPath));

        GameEntryViewModel? active = null;
        foreach (var entry in GameEntries)
        {
            entry.IsActive = openKey != null && !entry.IsNotFound
                             && string.Equals(entry.Key, openKey, StringComparison.OrdinalIgnoreCase);
            if (entry.IsActive) active = entry;
        }

        ActiveEntry = active;
        if (active != null) _ = active.LoadArtAsync(includeHero: true);
    }

    /// Puts the open game into the picker if the scan hasn't seen it yet (startup opens the game
    /// before the full scan finishes), so the header has an entry to show straight away.
    private void EnsureEntryFor(DetectedGame game)
    {
        if (GameEntries.Any(e => !e.IsNotFound && string.Equals(e.Key, game.Key, StringComparison.OrdinalIgnoreCase)))
            return;

        // Replaces the built-in's "not found" placeholder rather than sitting beside it.
        var placeholder = GameEntries.FirstOrDefault(e => e.IsNotFound && e.Profile.Id == game.Profile.Id);
        var entry = new GameEntryViewModel(game);
        if (placeholder != null) GameEntries[GameEntries.IndexOf(placeholder)] = entry;
        else GameEntries.Add(entry);
    }

    private async Task SwitchGameAsync(GameEntryViewModel? entry)
    {
        if (entry == null) return;
        if (entry.IsActive) return;

        // A built-in game that isn't here is a "find it for me" action rather than a dead control.
        if (entry.IsNotFound)
        {
            await AddGameFolderAsync(entry.Profile);
            return;
        }

        if (entry.IsLeftover)
        {
            LoggingService.Instance.Warn(
                $"{entry.DisplayName} isn't installed any more - {entry.RootPath} only holds files left behind " +
                "after uninstalling (mod loader files and mods). Reinstall the game to manage it again.");
            return;
        }

        // Re-checked at the moment of opening: the scan may be minutes old, and the game may have
        // been uninstalled since.
        var fresh = _catalog.Inspect(entry.RootPath, entry.Game.AddedByHand);
        if (fresh is not { State: CatalogState.Installed })
        {
            LoggingService.Instance.Warn($"{entry.DisplayName} is no longer installed at {entry.RootPath}.");
            _ = RefreshCatalogAsync();
            return;
        }

        await OpenGameAsync(fresh);
    }

    /// The single gated transition. Every route that changes the open game ends here.
    private async Task<bool> OpenGameAsync(DetectedGame game)
    {
        // Say something. A refused switch used to return silently - no highlight, no log line -
        // which reads as a dead control.
        if (!await _transitionGate.WaitAsync(0))
        {
            LoggingService.Instance.Info("Already switching games - give it a moment.");
            return false;
        }

        try
        {
            // A switch mid-install would leave the installer writing into a game that is no longer
            // the one on screen. Refusing is the only honest option - there is nothing to queue it behind.
            if (IsBusy)
            {
                LoggingService.Instance.Warn("Finish what's running before switching games.");
                return false;
            }

            IsBusy = true;
            ClearPerGameState();

            EnsureEntryFor(game);
            Game = game.ToInstallation();
            MarkActiveEntry();

            StatusMessage = $"Opening {game.Profile.DisplayName}...";
            await SetupForGameAsync(Game);
            StatusMessage = "Ready.";
            return true;
        }
        finally
        {
            IsBusy = false;
            _transitionGate.Release();
            MarkActiveEntry();
        }
    }

    /// Adds a game by picking its folder - for installs no launcher knows about, or a built-in game
    /// detection missed.
    ///
    /// <paramref name="wanted"/> is the game the user was looking for when they clicked its row, used
    /// only to word the prompt. It is never forced onto the folder: whatever the folder resolves to
    /// is what it is. Forcing it is how another game's folder used to end up saved as DDS2, offered
    /// DDS2's UE4SS and read with DDS2's mappings.
    public async Task AddGameFolderAsync(GameProfile? wanted)
    {
        if (IsGameTransitionInProgress || IsBusy)
        {
            LoggingService.Instance.Warn("Finish what's running first, then add the game.");
            return;
        }

        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = wanted != null
                ? $"Select the '{wanted.SteamFolderName}' folder ({wanted.DisplayName})"
                : "Select an Unreal Engine game's folder"
        };
        if (dialog.ShowDialog() != true) return;

        var picked = ResolvePickedFolder(dialog.FolderName);
        if (picked == null)
        {
            StatusMessage = "That folder doesn't contain an Unreal Engine game.";
            LoggingService.Instance.Error(
                $"No Unreal Engine game found in {dialog.FolderName}. Pick the game's own folder - the one that " +
                "holds the game's project folder and Engine folder.");
            return;
        }

        if (picked.State != CatalogState.Installed)
        {
            StatusMessage = "That folder only holds files left over from an uninstalled game.";
            LoggingService.Instance.Error(
                $"{picked.RootPath} has the shape of {picked.Profile.DisplayName} but no executable or game paks - " +
                "it looks like files left behind after the game was uninstalled. Reinstall it to manage it here.");
            return;
        }

        if (wanted != null && !string.Equals(wanted.Id, picked.Profile.Id, StringComparison.OrdinalIgnoreCase))
            LoggingService.Instance.Info(
                $"That folder is {picked.Profile.DisplayName}, not {wanted.DisplayName} - opening it as {picked.Profile.DisplayName}.");

        if (await OpenGameAsync(picked))
            _ = RefreshCatalogAsync();
    }

    /// The picked folder, or the nearest parent of it that is an Unreal game - people often pick the
    /// project folder or Binaries\Win64 rather than the game's root. Each candidate goes through the
    /// catalog's own classification, so a parent is only accepted for the same reasons a scan would.
    private DetectedGame? ResolvePickedFolder(string picked)
    {
        var dir = picked.TrimEnd('\\', '/');
        DetectedGame? leftover = null;

        for (var depth = 0; depth < 4 && !string.IsNullOrEmpty(dir); depth++)
        {
            var entry = _catalog.Inspect(dir, addedByHand: true);
            if (entry is { State: CatalogState.Installed }) return entry;
            leftover ??= entry;
            dir = Path.GetDirectoryName(dir) ?? "";
        }

        return leftover;
    }

    /// Removes a game added by hand from the picker. Only for those - a game a launcher lists would
    /// simply come back on the next scan, so offering to forget it would be a button that lies.
    private void ForgetGame(GameEntryViewModel? entry)
    {
        if (entry == null || !entry.Game.AddedByHand) return;

        if (entry.IsActive)
        {
            StatusMessage = "Switch to another game before removing this one from the list.";
            return;
        }

        var settings = AppSettingsService.Instance.Current;
        if (settings.Games.TryGetValue(entry.Profile.Id, out var forGame))
        {
            if (entry.Profile.IsBuiltIn) forGame.GamePathOverride = null;
            else settings.Games.Remove(entry.Profile.Id);
            AppSettingsService.Instance.Save();
        }

        GameEntries.Remove(entry);
        LoggingService.Instance.Info($"Removed {entry.DisplayName} from the list. Nothing in its folder was touched.");

        // A built-in game is always listed - as "not installed" once its folder is forgotten - so it
        // comes straight back as that, rather than vanishing until the next scan.
        if (entry.Profile.IsBuiltIn) _ = RefreshCatalogAsync();
    }

    private static void OpenFolderInExplorer(string path)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{path}\"")); }
        catch (Exception ex) { LoggingService.Instance.Warn($"Couldn't open Explorer: {ex.Message}"); }
    }

    /// Drops everything that belongs to the game being switched away from.
    ///
    /// Each of these holds a reference the new game must not inherit:
    ///  - Mods: the ModInfo objects carry the OLD game's absolute file paths.
    ///  - SelectedMods: a surviving multi-select would let Bulk Uninstall delete files that belong
    ///    to a game that is no longer open, through an installer built for it.
    ///  - the undo entry: its closure captures the outgoing installer and the files it moved.
    ///  - the banners and conflicts: about the other game's mods, and simply wrong here.
    private void ClearPerGameState()
    {
        // Anything still in flight for the previous game now has a stale token.
        _gameContextVersion++;

        DetachModSubscriptions();
        Mods.Clear();
        SelectedMods.Clear();
        Conflicts.Clear();

        // Both halves of the banner. Clearing only the list left HasNexusNewMods true and the text
        // untouched, so switching from DDS2 to DDS1 kept showing "1 new DDS2 mod on Nexus" above a
        // DDS1 mod list - stale, and about the wrong game.
        NexusNewMods.Clear();

        // Per GAME, not per install: a DDS1 mod resolved against DDS2's keys is the wrong card, and
        // mod 79 exists on both domains as two unrelated mods.
        _nexusCatalogue = null;
        HasNexusNewMods = false;
        NexusBannerText = "";

        UndoService.Instance.Invalidate();

        HasSelection = false;
        SelectionSummary = "";
        HasConflicts = false;
        UpdateAvailable = false;
        Ue4ssStatus = null;
        PreviousUE4SS = null;
        CompatibilitySummary = "No mods to check yet.";
    }

    /// Detaches the annotation handler from every mod currently listed.
    ///
    /// ObservableCollection.Clear() raises a Reset whose OldItems is null, so the unsubscribe in the
    /// CollectionChanged handler never runs for a Clear - every ModInfo the user has ever loaded
    /// stays subscribed. That was harmless while the list was cleared once at startup. With a game
    /// switch it is not: each of those objects calls _registry.Upsert when its star or notes change,
    /// which would write the previous game's mods into the new game's registry file.
    private void DetachModSubscriptions()
    {
        foreach (var mod in Mods) mod.PropertyChanged -= OnModAnnotationChanged;
    }

    /// True when the active game changed since <paramref name="token"/> was taken, meaning whatever
    /// produced it belongs to a game that is no longer open.
    private bool IsStaleGameContext(int token) => token != _gameContextVersion;
}
