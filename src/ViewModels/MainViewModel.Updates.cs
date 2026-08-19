using System.Collections.Specialized;
using System.ComponentModel;
using System.Reflection;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CUE4Parse.UE4.Versions;
using DDS2ModManager.Views;

namespace DDS2ModManager.ViewModels;

/// Noticing the game was patched, applying every waiting mod update, re-checking one.
///
/// Part of MainViewModel, split across files rather than extracted into separate classes. The
/// logic here is view-model glue - command wiring, status text, dialog plumbing - which gains
/// nothing from indirection. What it does gain is that two people can add features in different
/// areas without landing in the same 2,000-line file: this class produced three of the nine
/// conflicts the last merge had to resolve by hand.
public partial class MainViewModel
{
    // ---- noticing that the game itself was patched -------------------------------------------

    /// Compares the game exe against what it looked like last run, and says so once if it moved.
    ///
    /// Nothing is disabled and nothing is changed. A patch does not necessarily break anything -
    /// it just means "if something is broken today, this is why", which is exactly the connection
    /// nobody makes on their own.
    private void CheckGameVersionChanged()
    {
        if (Game == null) return;

        var current = GameVersionWatchService.Read(Game);
        if (current == null) return;

        // Per game: the two games are patched independently, and a shared stamp would report every
        // game switch as "the game was updated".
        var settings = AppSettingsService.Instance.ForGame(Game.Profile);
        var previous = settings.LastSeenGameWrittenUtc is { } written
            ? new GameVersionWatchService.GameStamp(settings.LastSeenGameVersion ?? "", settings.LastSeenGameSize, written)
            : null;

        // Record first, so a crash between here and the next launch can't produce the warning
        // twice for the same patch.
        settings.LastSeenGameVersion = current.Version;
        settings.LastSeenGameSize = current.Size;
        settings.LastSeenGameWrittenUtc = current.WrittenUtc;
        AppSettingsService.Instance.Save();

        // First run has nothing to compare against. Recording it silently is right - announcing
        // "the game changed" to someone who just installed the manager would be nonsense.
        if (previous == null || previous.LooksLike(current)) return;

        var installed = Mods.Count(m => m.IsEnabled);
        LoggingService.Instance.Warn(
            $"The game has been updated since you last used the manager ({previous.Display} → {current.Display}). " +
            $"Your {installed} enabled mod(s) are untouched, but a game patch can recook the content a pak mod " +
            "replaces - so if something misbehaves today, check for mod updates before anything else.");
    }

    // ---- updating several mods, and re-checking one ------------------------------------------

    [ObservableProperty] private bool hasUpdatesToApply;

    private void RefreshUpdateCommandState()
    {
        HasUpdatesToApply = Mods.Any(m => m.UpdateAvailable && m.AvailableUpdateAssetUrl != null);
        UpdateAllModsCommand.NotifyCanExecuteChanged();
    }

    private bool CanUpdateAll() => HasUpdatesToApply;

    /// Applies every waiting update, one at a time, still asking per mod.
    ///
    /// The prompt is NOT skipped in bulk. Each update is a different author's code from a
    /// different repository, and "update all" is a request to be taken through them in turn
    /// rather than permission to install ten strangers' code unseen. Anyone who declines one
    /// simply moves on to the next.
    ///
    /// Snapshotted first because installing replaces a ModInfo - the list mutates underneath.
    [RelayCommand(CanExecute = nameof(CanUpdateAll))]
    private async Task UpdateAllModsAsync()
    {
        var pending = Mods.Where(m => m.UpdateAvailable && m.AvailableUpdateAssetUrl != null).ToList();
        if (pending.Count == 0) return;

        LoggingService.Instance.Info($"{pending.Count} mod(s) have updates. You'll be asked about each one.");

        var applied = 0;
        foreach (var mod in pending)
        {
            // It may have been updated as the other half of a two-part mod on a previous pass.
            if (!Mods.Contains(mod) || !mod.UpdateAvailable) continue;

            var before = mod.InstalledVersion;
            await UpdateModAsync(mod);
            if (!Mods.Contains(mod) || mod.InstalledVersion != before) applied++;
        }

        LoggingService.Instance.Success(applied == 0
            ? "No updates were applied."
            : $"Updated {applied} of {pending.Count} mod(s).");

        RefreshUpdateCommandState();
    }

    /// Re-checks one mod immediately, ignoring the six-hour cache.
    ///
    /// For the author who just published a release and wants to see it appear, rather than
    /// waiting out a cache designed to protect a rate limit they aren't near.
    [RelayCommand]
    private async Task RecheckModAsync(ModInfo? mod)
    {
        if (mod is not { HasUpdateSource: true }) return;

        StatusMessage = $"Checking {mod.Name}...";
        try
        {
            var ok = await _modUpdater.CheckOneAsync(mod);
            _registry?.Upsert(mod);

            LoggingService.Instance.Info(!ok
                ? $"Couldn't reach the release page for '{mod.Name}'."
                : mod.UpdateAvailable
                    ? $"'{mod.Name}' {mod.InstalledVersion} -> {mod.LatestVersion} is available."
                    : $"'{mod.Name}' is up to date.");

            RefreshModUpdateBanner();
            RefreshUpdateCommandState();
        }
        finally { StatusMessage = "Ready"; }
    }
}
