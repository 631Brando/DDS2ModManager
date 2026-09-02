using CommunityToolkit.Mvvm.Input;

namespace DDS2ModManager.ViewModels;

/// Switching which game the manager is looking at.
///
/// The dangerous part of this is not the switch itself, it is everything that survives it. The mod
/// list, the multi-select, the undo entry and several fire-and-forget background tasks all hold
/// references to the OUTGOING game's services and ModInfo objects. Left alone, a bulk uninstall or
/// an undo performed after a switch would delete files using the previous game's paths, and a
/// background scan finishing late would write its results into the new game's registry.
///
/// So the order here matters: tear down everything game-specific FIRST, bump the context token, and
/// only then point at the new game.
public partial class MainViewModel
{
    public ObservableCollection<GameTabViewModel> GameTabs { get; } = new();

    /// Incremented on every game change. Background work captures it before it starts and drops its
    /// results if it no longer matches - a slow Nexus fetch for DDS1 must not land in DDS2's list.
    private int _gameContextVersion;

    /// Guards against re-entering a switch from the UI while one is already running.
    private bool _switchingGame;

    public IAsyncRelayCommand<GameTabViewModel> SwitchGameCommand { get; private set; } = null!;

    private void InitializeGameTabs()
    {
        // AllowConcurrentExecutions, with _switchingGame owning the decision instead.
        //
        // By default AsyncRelayCommand reports CanExecute=false for as long as it is running, and
        // both tabs share this one instance - so the entire strip went dead for the whole switch
        // (which mounts and reads every pak, seconds on an 11 GB install) with no disabled
        // appearance. A click in that window did nothing at all, indistinguishable from being
        // ignored. The guard at the top of SwitchGameAsync already refuses re-entry, and now says so.
        SwitchGameCommand = new AsyncRelayCommand<GameTabViewModel>(
            SwitchGameAsync, AsyncRelayCommandOptions.AllowConcurrentExecutions);

        foreach (var profile in GameProfiles.InDisplayOrder)
            GameTabs.Add(new GameTabViewModel { Profile = profile });
    }

    /// Points each tab at whatever install can be found for it, without changing the active game.
    ///
    /// A remembered folder is honoured even when auto-detection missed it, which is what makes a
    /// non-Steam or manually-located install stay put instead of reverting to "not found" on every
    /// launch.
    public void RefreshGameTabs()
    {
        var detected = _gameDetection.DetectAll().ToDictionary(g => g.Profile.Id, StringComparer.OrdinalIgnoreCase);
        var settings = AppSettingsService.Instance.Current;

        foreach (var tab in GameTabs)
        {
            GameInstallation? install = null;

            if (settings.Games.TryGetValue(tab.Profile.Id, out var forGame)
                && !string.IsNullOrWhiteSpace(forGame.GamePathOverride))
            {
                var remembered = new GameInstallation { RootPath = forGame.GamePathOverride, Profile = tab.Profile };
                if (remembered.IsValid) install = remembered;
            }

            install ??= detected.GetValueOrDefault(tab.Profile.Id);

            tab.Install = install;
            tab.IsActive = Game != null && string.Equals(Game.Profile.Id, tab.Profile.Id, StringComparison.OrdinalIgnoreCase);
        }
    }

    private async Task SwitchGameAsync(GameTabViewModel? tab)
    {
        if (tab == null) return;

        // Say something. This used to return silently, so a second click during a switch produced
        // nothing observable at all - no highlight, no log line - which reads as a dead control.
        if (_switchingGame)
        {
            LoggingService.Instance.Info("Already switching games - give it a moment.");
            return;
        }

        // A switch mid-install would leave the installer writing into a game that is no longer the
        // one on screen. Refusing is the only honest option - there is nothing to queue it behind.
        if (IsBusy)
        {
            LoggingService.Instance.Warn("Finish what's running before switching games.");
            return;
        }

        if (tab.IsActive) return;

        // An uninstalled game's tab is a "find it for me" button rather than a dead control.
        if (!tab.IsInstalled)
        {
            await BrowseGameFolderAsync(tab.Profile);
            return;
        }

        _switchingGame = true;
        IsBusy = true;
        try
        {
            ClearPerGameState();

            Game = tab.Install;
            StatusMessage = $"Switching to {tab.Profile.DisplayName}...";
            await SetupForGameAsync(tab.Install!);
            StatusMessage = "Ready.";
        }
        finally
        {
            IsBusy = false;
            _switchingGame = false;
            RefreshGameTabs();
        }
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
