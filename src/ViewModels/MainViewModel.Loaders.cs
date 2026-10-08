using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DDS2ModManager.ViewModels;

/// UnrealModLoader: the loader UE4 games' Blueprint mod scenes - DDS1's among them - are built on.
/// UE4SS has its own card and flow in MainViewModel.cs; this is the second loader beside it.
public partial class MainViewModel
{
    /// Null between a game switch and that game's setup, like Ue4ssStatus - and the card's bindings
    /// fall back to hidden, so a stale status can't show one game's loader on another.
    [ObservableProperty] private UnrealModLoaderStatus? umlStatus;

    private readonly UnrealModLoaderService _uml = new();

    private void RefreshUmlStatus()
    {
        UmlStatus = Game == null ? null : UnrealModLoaderService.GetStatus(Game);
    }

    /// After UE4SS changes layout - stable's Binaries\Win64\Mods to experimental's ue4ss\Mods, or the
    /// reverse for mods waiting in an empty ue4ss\ - the lua mod folders moved and their rows must
    /// follow. Also run at setup, so an install interrupted between the move and this still heals.
    private void RelinkMovedLuaMods(GameInstallation game)
    {
        if (_registry == null) return;

        var relinked = _registry.RelinkMovedLuaMods(game.UE4SSModsPath);
        if (relinked > 0)
            LoggingService.Instance.Info($"Re-linked {relinked} lua mod(s) to where UE4SS now keeps them: {game.UE4SSModsPath}.");
    }

    [RelayCommand]
    private async Task InstallUmlAsync()
    {
        if (Game == null) return;
        var game = Game;
        var log = LoggingService.Instance;

        // Like a mod install: never alongside a game switch or another operation, whose IsBusy this
        // would otherwise clear in its finally while that one is still running.
        if (IsGameTransitionInProgress || IsBusy)
        {
            log.Warn("Not installing UnrealModLoader yet - finish what's running first, then try again.");
            return;
        }

        if (UnrealModLoaderService.WhyNot(game) is { } refusal)
        {
            log.Warn($"Can't install UnrealModLoader: {refusal}");
            return;
        }

        var status = UmlStatus;
        var question =
            (status is { IsManagedByUs: true }
                ? "Reinstall UnrealModLoader with its latest release?\n\n"
                : "Install UnrealModLoader?\n\n") +
            "It goes into Binaries\\Win64 and loads itself through xinput1_3.dll when the game starts - nothing to launch " +
            "separately. Logic mods go flat in Content\\Paks\\LogicMods, and DLL core mods in Content\\CoreMods." +
            (status?.Warning is { } warning ? $"\n\n{warning}" : "");

        if (System.Windows.MessageBox.Show(question, "UnrealModLoader", System.Windows.MessageBoxButton.OKCancel,
                System.Windows.MessageBoxImage.Question) != System.Windows.MessageBoxResult.OK)
            return;

        if (!ConfirmLoaderOnAntiCheat(game, "UnrealModLoader")) return;

        IsBusy = true;
        StatusMessage = "Fetching UnrealModLoader...";
        var progress = new Progress<double>(p => ProgressValue = p);

        try
        {
            var release = await _uml.GetLatestReleaseAsync();
            if (Game != game) return;

            var asset = release == null ? null : UnrealModLoaderService.FindAsset(release);
            if (release == null || asset == null)
            {
                StatusMessage = "Couldn't fetch UnrealModLoader from GitHub - see log for details.";
                log.Warn("UnrealModLoader's latest release couldn't be read, or carried no archive to install.");
                return;
            }

            var ok = await _uml.InstallAsync(game, release, asset, progress);
            if (Game != game) return;

            RefreshUmlStatus();
            ReportPreExistingNestedLogicMods(game);
            StatusMessage = ok ? $"UnrealModLoader {release.TagName} installed." : "UnrealModLoader install failed - see log for details.";
        }
        finally
        {
            IsBusy = false;
            ProgressValue = 0;
        }
    }

    [RelayCommand]
    private void RemoveUml()
    {
        if (Game == null) return;

        if (IsGameTransitionInProgress || IsBusy)
        {
            LoggingService.Instance.Warn("Not removing UnrealModLoader yet - finish what's running first, then try again.");
            return;
        }

        if (System.Windows.MessageBox.Show(
                "Remove UnrealModLoader?\n\nOnly the loader's own files are removed. Your logic mods and core mods stay " +
                "where they are, and load again if you reinstall it.",
                "Remove UnrealModLoader", System.Windows.MessageBoxButton.OKCancel,
                System.Windows.MessageBoxImage.Question) != System.Windows.MessageBoxResult.OK)
            return;

        var ok = UnrealModLoaderService.Remove(Game);
        RefreshUmlStatus();
        StatusMessage = ok ? "UnrealModLoader removed." : "UnrealModLoader couldn't be fully removed - see log for details.";
    }

    /// UML scans LogicMods flat, so a logic mod installed into a subfolder before UML was here (the
    /// UE4SS layout a generic UE4 game starts with) mounts and then never runs. Said, not moved: the
    /// fix is Disable then Enable, which puts each one where the loader now present reads it.
    private void ReportPreExistingNestedLogicMods(GameInstallation game)
    {
        var nested = Mods.Where(m => m.Type == ModType.LogicMod && m.IsEnabled
                                     && !string.IsNullOrWhiteSpace(m.InstallPath)
                                     && !string.Equals(Path.GetFullPath(m.InstallPath).TrimEnd('\\'),
                                         Path.GetFullPath(game.LogicModsPath).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            .Select(m => m.Name)
            .ToList();

        if (nested.Count == 0) return;

        LoggingService.Instance.Warn(
            $"{nested.Count} logic mod(s) sit in their own subfolder of LogicMods, which UnrealModLoader doesn't scan: " +
            $"{string.Join(", ", nested.Take(8))}{(nested.Count > 8 ? ", ..." : "")}. Disable and re-enable each one to move " +
            "it where UnrealModLoader reads it.");
    }
}
