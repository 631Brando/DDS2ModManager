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

            var driftDetected = false;

            if (previous is { Files.Count: > 0 })
            {
                var drift = ModFileStateService.Compare(mod);
                mod.DriftSummary = drift.Any ? drift.Summary : null;
                driftDetected = drift.Any;
                if (driftDetected) drifted.Add(mod);
            }

            // Arm the check for a mod that has never been fingerprinted - but do NOT re-arm on the
            // pass that just found drift.
            //
            // Overwriting it here was the bug: the fingerprint would match the file on disk again
            // while ContainedAssetPaths and DataTableAppends still described the PREVIOUS build, so
            // the manager reported the mod as unchanged and fully scanned while conflict checking
            // used stale data. Measured on a real install: a mod whose fingerprint matched to the
            // microsecond had 13 recorded DataTable appends against 22 actually present.
            //
            // Leaving the old fingerprint in place keeps reporting drift until a Deep Scan re-reads
            // the mod and updates the fingerprint alongside the analysis it describes.
            if (!driftDetected)
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
