using System.Collections.Specialized;
using System.ComponentModel;
using System.Reflection;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CUE4Parse.UE4.Versions;
using DDS2ModManager.Views;

namespace DDS2ModManager.ViewModels;

/// Acting on mods: two-part grouping, multi-select, and enable / disable / uninstall.
///
/// Part of MainViewModel, split across files rather than extracted into separate classes. The
/// logic here is view-model glue - command wiring, status text, dialog plumbing - which gains
/// nothing from indirection. What it does gain is that two people can add features in different
/// areas without landing in the same 2,000-line file: this class produced three of the nine
/// conflicts the last merge had to resolve by hand.
public partial class MainViewModel
{
    // ---- two-part mods -----------------------------------------------------------------------

    /// Every row belonging to the same mod as this one, including itself.
    ///
    /// Grouped on the installed name with the manager's own packaging suffixes removed, which is
    /// exactly the normalisation NexusModMatcher already does for Nexus titles - "EthanolExtraction"
    /// and "EthanolExtraction_Lua" both reduce to "ethanolextraction". Reusing it means the two
    /// features cannot disagree about what counts as the same mod.
    ///
    /// Requires DIFFERENT types to group. Two rows of the same type sharing a name are not two
    /// halves of one mod - they are a name collision, which CompatibilityCheckerService already
    /// reports as a Critical conflict, and quietly merging them would hide it.
    public IEnumerable<ModInfo> GroupOf(ModInfo mod)
    {
        var key = NexusModMatcher.KeyForInstalled(mod.Name);
        if (key.Length == 0) return new[] { mod };

        var group = Mods
            .Where(m => NexusModMatcher.KeyForInstalled(m.Name) == key)
            .GroupBy(m => m.Type)
            .Select(g => g.First())
            .ToList();

        // Whatever the grouping decided, the mod acted on is always in its own group.
        if (!group.Contains(mod))
        {
            group.RemoveAll(m => m.Type == mod.Type);
            group.Add(mod);
        }

        return group;
    }

    /// Stamps each row with how many parts its mod has, so the grid can show it.
    private void RefreshModGroups()
    {
        foreach (var mod in Mods) mod.LinkedPartCount = GroupOf(mod).Count();
    }

    // ---- acting on several mods at once ------------------------------------------------------

    /// The rows currently selected in the grid.
    ///
    /// Pushed in by the view rather than bound, because DataGrid.SelectedItems is not a bindable
    /// dependency property - this is the standard way round that, and keeps the commands below
    /// testable without a window.
    public ObservableCollection<ModInfo> SelectedMods { get; } = new();

    [ObservableProperty] private string selectionSummary = "";
    [ObservableProperty] private bool hasSelection;

    /// Called by the view whenever the grid's selection changes.
    public void SetSelection(IEnumerable<ModInfo> selected)
    {
        SelectedMods.Clear();
        foreach (var m in selected) SelectedMods.Add(m);

        // One row selected is just "clicking a mod" - the bulk bar only earns its space once it
        // would do something the row's own buttons can't.
        HasSelection = SelectedMods.Count > 1;
        SelectionSummary = HasSelection ? $"{SelectedMods.Count} selected" : "";

        BulkEnableCommand.NotifyCanExecuteChanged();
        BulkDisableCommand.NotifyCanExecuteChanged();
        BulkUninstallCommand.NotifyCanExecuteChanged();
    }

    private bool CanActOnSelection() => SelectedMods.Count > 1;

    /// Enabling and disabling are reversible and touch nothing the user can't put back, so they
    /// run without a confirmation - the same as the per-row buttons.
    [RelayCommand(CanExecute = nameof(CanActOnSelection))]
    private void BulkEnable() => BulkToggle(enable: true);

    [RelayCommand(CanExecute = nameof(CanActOnSelection))]
    private void BulkDisable() => BulkToggle(enable: false);

    private void BulkToggle(bool enable)
    {
        if (_installer == null) return;

        // Copy first: enabling a mod can reorder the view, which would otherwise mutate the
        // selection out from under the loop.
        var targets = SelectedMods.Where(m => m.IsEnabled != enable).ToList();
        if (targets.Count == 0) return;

        var done = 0;
        foreach (var mod in targets)
        {
            try
            {
                if (enable) _installer.Enable(mod); else _installer.Disable(mod);
                _registry?.Upsert(mod);
                done++;
            }
            catch (Exception ex)
            {
                // One failure must not abandon the rest - the user asked for all of them.
                LoggingService.Instance.Error($"Couldn't {(enable ? "enable" : "disable")} '{mod.Name}': {ex.Message}");
            }
        }

        LoggingService.Instance.Success($"{(enable ? "Enabled" : "Disabled")} {done} mod(s).");
        RunCompatibilityCheck();
        ModsView.Refresh();
    }

    /// Uninstalling is the one bulk action that deletes files, so it names every mod first and
    /// asks once. The per-row button asks too; doing it in bulk is exactly when a mis-click is
    /// most expensive.
    [RelayCommand(CanExecute = nameof(CanActOnSelection))]
    private void BulkUninstall()
    {
        if (_installer == null || _registry == null) return;

        var targets = SelectedMods.ToList();
        var names = string.Join("\n  ", targets.Take(15).Select(m => m.Name));
        if (targets.Count > 15) names += $"\n  ...and {targets.Count - 15} more";

        var answer = System.Windows.MessageBox.Show(
            $"Uninstall these {targets.Count} mods?\n\n  {names}\n\n" +
            "Their files are removed from the game. Anything you've disabled stays in the Disabled " +
            "Mods folder as usual.",
            "Uninstall mods", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);

        if (answer != System.Windows.MessageBoxResult.Yes) return;

        var done = 0;
        foreach (var mod in targets)
        {
            try
            {
                _installer.Uninstall(mod);
                _registry.Remove(mod.Id);
                Mods.Remove(mod);
                done++;
            }
            catch (Exception ex)
            {
                LoggingService.Instance.Error($"Couldn't uninstall '{mod.Name}': {ex.Message}");
            }
        }

        LoggingService.Instance.Success($"Uninstalled {done} mod(s).");
        SetSelection(Enumerable.Empty<ModInfo>());
        RunCompatibilityCheck();
    }

    /// Enabling or disabling one half of a two-part mod does the same to the other half.
    ///
    /// Half a mod enabled is the worst outcome available: the lua half runs and calls into a pak
    /// that isn't loaded, or the pak loads with nothing driving it. Neither produces an error the
    /// player can act on - it just doesn't work. Acting on the whole set is what someone means
    /// when they toggle a mod they think of as one thing.
    private void EnableMod(ModInfo? mod)
    {
        if (mod == null || _installer == null) return;

        var changed = GroupOf(mod).Where(p => !p.IsEnabled).ToList();
        foreach (var part in changed)
        {
            _installer.Enable(part);
            _registry?.Upsert(part);
        }

        if (changed.Count > 0) RecordToggleUndo(mod, changed, wasEnabled: false);

        ReportGroupAction(mod, "Enabled");
        RunCompatibilityCheck();
    }

    /// Records how to put a toggle back. Enable/disable move files between the game folder and
    /// the disabled cache, so reversing is simply the opposite call on the same mods.
    private void RecordToggleUndo(ModInfo mod, List<ModInfo> changed, bool wasEnabled)
    {
        var verb = wasEnabled ? "Disabled" : "Enabled";

        UndoService.Instance.Record($"{verb} '{mod.Name}'", () =>
        {
            if (_installer == null) return false;

            foreach (var part in changed)
            {
                // Only reverse parts that are still present and still in the state we left them.
                if (!Mods.Contains(part)) continue;

                if (wasEnabled) _installer.Enable(part);
                else _installer.Disable(part);

                _registry?.Upsert(part);
            }

            return true;
        });
    }

    private void DisableMod(ModInfo? mod)
    {
        if (mod == null || _installer == null) return;

        var changed = GroupOf(mod).Where(p => p.IsEnabled).ToList();
        foreach (var part in changed)
        {
            _installer.Disable(part);
            _registry?.Upsert(part);
        }

        if (changed.Count > 0) RecordToggleUndo(mod, changed, wasEnabled: true);

        ReportGroupAction(mod, "Disabled");
        RunCompatibilityCheck();
    }

    /// Uninstall ASKS before taking the other half, rather than assuming.
    ///
    /// Deliberately different from enable/disable: those are reversible in one click, this deletes
    /// files. Someone removing one half on purpose - to reinstall just the pak, say - would not
    /// thank us for silently removing the rest.
    private void UninstallMod(ModInfo? mod)
    {
        if (mod == null || _installer == null) return;

        var group = GroupOf(mod).ToList();
        var targets = new List<ModInfo> { mod };

        if (group.Count > 1)
        {
            var others = group.Where(p => p != mod).ToList();
            var answer = System.Windows.MessageBox.Show(
                $"'{mod.Name}' is one part of a mod that installs in {group.Count} places:\n\n  " +
                string.Join("\n  ", group.Select(p => $"{p.Name}  ({p.Type})")) +
                "\n\nRemove all of them? Choosing No removes only the part you clicked, which will " +
                "usually leave the mod half-installed and not working.",
                "Uninstall mod", System.Windows.MessageBoxButton.YesNoCancel, System.Windows.MessageBoxImage.Question);

            if (answer == System.Windows.MessageBoxResult.Cancel) return;
            if (answer == System.Windows.MessageBoxResult.Yes) targets.AddRange(others);
        }

        foreach (var part in targets)
        {
            try
            {
                _installer.Uninstall(part);
                _registry?.Remove(part.Id);
                Mods.Remove(part);
            }
            catch (Exception ex)
            {
                LoggingService.Instance.Error($"Couldn't uninstall '{part.Name}': {ex.Message}");
            }
        }

        RunCompatibilityCheck();
    }

    private void ReportGroupAction(ModInfo mod, string verb)
    {
        var count = GroupOf(mod).Count();
        if (count > 1)
            LoggingService.Instance.Info($"{verb} both parts of '{mod.Name}' - it installs in {count} places.");
    }
}
