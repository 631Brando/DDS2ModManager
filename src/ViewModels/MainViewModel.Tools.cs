using System.Collections.Specialized;
using System.ComponentModel;
using System.Reflection;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CUE4Parse.UE4.Versions;
using DDS2ModManager.Views;

namespace DDS2ModManager.ViewModels;

/// Diagnostics, profiles, history, and the shortcuts that get you somewhere fast.
///
/// Part of MainViewModel, split across files rather than extracted into separate classes. The
/// logic here is view-model glue - command wiring, status text, dialog plumbing - which gains
/// nothing from indirection. What it does gain is that two people can add features in different
/// areas without landing in the same 2,000-line file: this class produced three of the nine
/// conflicts the last merge had to resolve by hand.
public partial class MainViewModel
{
    // ---- diagnostics, profiles, and getting to things quickly --------------------------------

    private readonly ModProfileService _profiles = new();

    /// Writes one zip with everything a bug report needs.
    ///
    /// Deliberately excludes saves and the game's config files - the shipped ini carries the
    /// developers' BugSplat credentials, and a diagnostics bundle is a file people post publicly.
    [RelayCommand]
    private void ExportDiagnostics()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save diagnostics",
            Filter = "Zip archive (*.zip)|*.zip",
            FileName = $"DDS2ModManager_diagnostics_{DateTime.Now:yyyyMMdd_HHmmss}.zip"
        };

        if (dialog.ShowDialog() != true) return;

        var path = new DiagnosticsBundleService().Create(
            new DiagnosticsBundleService.BundleRequest(Game, Mods.ToList(), Conflicts.ToList(), Ue4ssStatus, AppVersionDisplay),
            dialog.FileName);

        if (path == null) return;

        StatusMessage = $"Diagnostics saved to {path}";
        RevealInExplorer(path);
    }

    /// Saves the current on/off state under a name.
    [RelayCommand]
    private void SaveProfile()
    {
        var name = PromptWindow.Ask(
            "Save mod profile",
            "Name this set of enabled mods, so you can come back to it later:",
            $"Profile {DateTime.Now:d MMM yyyy}");

        if (string.IsNullOrWhiteSpace(name)) return;

        var gameVersion = Game != null ? GameVersionWatchService.Read(Game)?.Display ?? "unknown" : "no game";
        var profile = _profiles.Capture(name.Trim(), Mods, AppVersionDisplay, gameVersion);

        if (!_profiles.Save(profile)) return;

        LoggingService.Instance.Success($"Saved profile '{profile.Name}' - {profile.Summary}.");
        StatusMessage = $"Profile '{profile.Name}' saved.";
    }

    /// Opens the profile list, where a profile can be applied, exported or deleted.
    [RelayCommand]
    private void OpenProfiles() =>
        new ProfilesWindow(this, _profiles) { Owner = System.Windows.Application.Current.MainWindow }.ShowDialog();

    /// Applies a saved profile, after showing exactly what it will change.
    ///
    /// Only toggles mods that are already installed. Nothing is downloaded, installed or deleted -
    /// a profile naming mods you don't have reports them and moves on.
    public void ApplyProfile(ModProfile profile)
    {
        if (_installer == null) return;

        var plan = _profiles.Plan(profile, Mods);

        if (!plan.ChangesAnything)
        {
            System.Windows.MessageBox.Show(
                $"'{profile.Name}' already matches what you have enabled." +
                (plan.Missing.Count > 0 ? $"\n\n{plan.Missing.Count} mod(s) in the profile aren't installed." : ""),
                "Apply profile", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            return;
        }

        var message = $"Applying '{profile.Name}' will:\n";
        if (plan.ToEnable.Count > 0) message += $"\n  Enable {plan.ToEnable.Count}:  {Names(plan.ToEnable)}";
        if (plan.ToDisable.Count > 0) message += $"\n  Disable {plan.ToDisable.Count}:  {Names(plan.ToDisable)}";
        if (plan.Missing.Count > 0) message += $"\n\n  Not installed, so skipped: {string.Join(", ", plan.Missing.Take(8))}";
        if (plan.Extra.Count > 0) message += $"\n\n  Installed but not in the profile, so left alone: {plan.Extra.Count}";
        message += "\n\nNothing is downloaded or deleted - only enabled and disabled.";

        if (System.Windows.MessageBox.Show(message, "Apply profile",
                System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Question)
            != System.Windows.MessageBoxResult.OK) return;

        foreach (var mod in plan.ToEnable) EnableMod(mod);
        foreach (var mod in plan.ToDisable) DisableMod(mod);

        LoggingService.Instance.Success(
            $"Applied '{profile.Name}': enabled {plan.ToEnable.Count}, disabled {plan.ToDisable.Count}.");
        ModsView.Refresh();

        static string Names(List<ModInfo> mods) =>
            string.Join(", ", mods.Take(6).Select(m => m.Name)) + (mods.Count > 6 ? $" and {mods.Count - 6} more" : "");
    }

    /// The current mod list as shareable text, straight to the clipboard.
    [RelayCommand]
    private void CopyModList()
    {
        var gameVersion = Game != null ? GameVersionWatchService.Read(Game)?.Display ?? "unknown" : "no game";
        var text = ModProfileService.ToShareableText(
            _profiles.Capture("current", Mods, AppVersionDisplay, gameVersion));

        try
        {
            System.Windows.Clipboard.SetText(text);
            LoggingService.Instance.Success($"Copied a list of {Mods.Count} mod(s) to the clipboard.");
            StatusMessage = "Mod list copied - paste it into a bug report or Discord.";
        }
        catch (Exception ex) { LoggingService.Instance.Error($"Couldn't copy the mod list: {ex.Message}"); }
    }

    /// What changed, and when.
    [RelayCommand]
    private void OpenHistory() =>
        new ModHistoryWindow { Owner = System.Windows.Application.Current.MainWindow }.ShowDialog();

    /// Starts the game through Steam, so it launches the same way it normally would.
    [RelayCommand]
    private void LaunchGame()
    {
        var blocking = Conflicts.Count(c => c.Severity == ConflictSeverity.Critical);
        if (blocking > 0 &&
            System.Windows.MessageBox.Show(
                $"There {(blocking == 1 ? "is" : "are")} {blocking} critical conflict(s) between your enabled mods. " +
                "Launching anyway is fine, but if the game misbehaves that is the first thing to look at.\n\nLaunch?",
                "Launch game", System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Warning)
            != System.Windows.MessageBoxResult.OK) return;

        // steam:// rather than the exe: Steam has to be running for the game to authenticate, and
        // launching the exe directly is what produces "please start via Steam".
        OpenUrl("steam://rungameid/1708850");
        StatusMessage = "Launching through Steam...";
    }

    /// Opens a mod's Nexus page. Only ever enabled for a mod that matched one.
    [RelayCommand]
    private void OpenNexusPage(ModInfo? mod)
    {
        if (mod?.NexusInfo == null) return;
        OpenUrl(mod.NexusInfo.Url);
    }

    private static void RevealInExplorer(string path)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")); }
        catch (Exception ex) { LoggingService.Instance.Warn($"Couldn't open Explorer: {ex.Message}"); }
    }
}
