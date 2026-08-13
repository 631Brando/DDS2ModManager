using System.Collections.Specialized;
using System.ComponentModel;
using System.Reflection;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CUE4Parse.UE4.Versions;
using DDS2ModManager.Views;

namespace DDS2ModManager.ViewModels;

/// How much room mods take, whether their files changed behind our back, and undo.
///
/// Part of MainViewModel, split across files rather than extracted into separate classes. The
/// logic here is view-model glue - command wiring, status text, dialog plumbing - which gains
/// nothing from indirection. What it does gain is that two people can add features in different
/// areas without landing in the same 2,000-line file: this class produced three of the nine
/// conflicts the last merge had to resolve by hand.
public partial class MainViewModel
{
    // ---- file state: size, drift, undo --------------------------------------------------------

    [ObservableProperty] private string modsSizeDisplay = "";
    [ObservableProperty] private bool canUndo;
    [ObservableProperty] private string undoDescription = "";

    /// Measures every mod, records what it looks like now, and reports anything that has changed
    /// behind the manager's back.
    ///
    /// Cheap: size and timestamp per file, no hashing. See ModFileStateService for why.
    private void RefreshFileState(bool reportDrift = true)
    {
        long total = 0;
        var drifted = new List<ModInfo>();

        foreach (var mod in Mods)
        {
            var previous = mod.Fingerprint;

            if (previous is { Files.Count: > 0 })
            {
                var drift = ModFileStateService.Compare(mod);
                mod.DriftSummary = drift.Any ? drift.Summary : null;
                if (drift.Any) drifted.Add(mod);
            }

            // Record the current state either way: a mod installed before this existed gets its
            // first fingerprint here, which is what arms the check from now on.
            mod.Fingerprint = ModFileStateService.Capture(mod);
            total += mod.SizeBytes;
        }

        ModsSizeDisplay = total > 0 ? $"{ModFileStateService.FormatSize(total)} on disk" : "";
        _registry?.Save();

        if (!reportDrift || drifted.Count == 0) return;

        foreach (var mod in drifted)
            LoggingService.Instance.Warn($"'{mod.Name}' files changed outside the manager - {mod.DriftSummary}.");

        LoggingService.Instance.Warn(
            $"{drifted.Count} mod(s) have files that don't match what was installed. That is expected if you edited " +
            "or replaced them yourself; if not, reinstalling those mods puts them back to a known state.");
    }

    private void WireUndo()
    {
        UndoService.Instance.Changed += () =>
        {
            CanUndo = UndoService.Instance.CanUndo;
            UndoDescription = UndoService.Instance.Description ?? "";
            UndoLastCommand.NotifyCanExecuteChanged();
        };
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void UndoLast()
    {
        if (!UndoService.Instance.Undo()) return;

        RunCompatibilityCheck();
        ModsView.Refresh();
    }
}
