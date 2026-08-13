using System.Collections.Specialized;
using System.ComponentModel;
using System.Reflection;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CUE4Parse.UE4.Versions;
using DDS2ModManager.Views;

namespace DDS2ModManager.ViewModels;

/// Filtering the mod list, and remembering how it was sorted.
///
/// Part of MainViewModel, split across files rather than extracted into separate classes. The
/// logic here is view-model glue - command wiring, status text, dialog plumbing - which gains
/// nothing from indirection. What it does gain is that two people can add features in different
/// areas without landing in the same 2,000-line file: this class produced three of the nine
/// conflicts the last merge had to resolve by hand.
public partial class MainViewModel
{
    // ---- searching the mod list --------------------------------------------------------------

    /// The filtered view the grid actually binds to.
    ///
    /// A view over Mods rather than a second filtered collection: sorting, selection and the
    /// existing conflict highlighting all keep working, and there is still exactly one list of
    /// mods underneath. Rebuilding a parallel collection on every keystroke would break the
    /// DataGrid's own sorting, which is a feature this already ships.
    public ICollectionView ModsView { get; }

    /// Live filter text. Empty shows everything, which is the state the app starts in.
    [ObservableProperty]
    private string modSearch = "";

    partial void OnModSearchChanged(string value)
    {
        ModsView.Refresh();
        UpdateSearchSummary();
    }

    [ObservableProperty] private string modSearchSummary = "";
    [ObservableProperty] private bool isModSearchActive;

    private void UpdateSearchSummary()
    {
        IsModSearchActive = !string.IsNullOrWhiteSpace(ModSearch);

        if (!IsModSearchActive)
        {
            ModSearchSummary = "";
            return;
        }

        var shown = ModsView.Cast<ModInfo>().Count();
        ModSearchSummary = shown == 0
            ? $"No mods match “{ModSearch}”"
            : $"{shown} of {Mods.Count} mods";
    }

    /// Matches the things a person would actually type: the mod's name, its type, its author, and
    /// its Nexus title and description when it has one.
    ///
    /// Deliberately NOT the contained asset paths - a mod contains hundreds, so searching them
    /// would make almost any term match almost every mod. "Which mods touch this asset?" is a
    /// genuinely useful question, but it is a different feature with its own answer.
    private bool MatchesSearch(object item)
    {
        if (item is not ModInfo mod) return false;
        if (string.IsNullOrWhiteSpace(ModSearch)) return true;

        var term = ModSearch.Trim();

        return Contains(mod.Name, term)
            || Contains(mod.Type.ToString(), term)
            || Contains(mod.UpdateAuthor, term)
            || Contains(mod.InstalledVersion, term)
            || Contains(mod.Tags, term)
            || Contains(mod.Notes, term)
            || Contains(mod.NexusInfo?.Name, term)
            || Contains(mod.NexusInfo?.Summary, term)
            || Contains(mod.NexusInfo?.Uploader, term);

        static bool Contains(string? haystack, string needle) =>
            !string.IsNullOrEmpty(haystack) &&
            haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    [RelayCommand]
    private void ClearModSearch() => ModSearch = "";

    // ---- which mods touch a given file --------------------------------------------------------

    /// Answers "which mods touch this asset?".
    ///
    /// The conflict checker already compares mods against EACH OTHER, which finds pairs that
    /// collide. This is the other direction: you have a symptom involving one specific asset -
    /// a broken icon, a wrong price table - and want to know what is touching it, including
    /// mods that touch it without conflicting with anything.
    [RelayCommand]
    private void FindAssetOwners()
    {
        var term = PromptWindow.Ask(
            "Find mods by file",
            "Part of a file or asset path - for example \"CartelDefaults\", \"Scooter\" or \".uasset\":",
            "");

        if (string.IsNullOrWhiteSpace(term)) return;

        var hits = Mods
            .Select(m => new
            {
                Mod = m,
                Paths = m.ContainedAssetPaths
                    .Where(p => p.Contains(term, StringComparison.OrdinalIgnoreCase))
                    .ToList()
            })
            .Where(x => x.Paths.Count > 0)
            .OrderByDescending(x => x.Paths.Count)
            .ToList();

        if (hits.Count == 0)
        {
            System.Windows.MessageBox.Show(
                $"No installed mod contains a path matching \"{term}\".\n\n" +
                "Pak mods only list their contents after a Deep Scan, so if you haven't run one, " +
                "try \"Re-scan Mod Files\" first.",
                "Find mods by file", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            return;
        }

        var report = new System.Text.StringBuilder();
        report.AppendLine($"{hits.Count} mod(s) contain a path matching \"{term}\":");
        report.AppendLine();

        foreach (var hit in hits)
        {
            var state = hit.Mod.IsEnabled ? "on" : "off";
            report.AppendLine($"[{state}] {hit.Mod.Name}  ({hit.Mod.Type})  - {hit.Paths.Count} match(es)");

            foreach (var path in hit.Paths.Take(8)) report.AppendLine($"        {path}");
            if (hit.Paths.Count > 8) report.AppendLine($"        ...and {hit.Paths.Count - 8} more");
            report.AppendLine();
        }

        // More than one ENABLED mod touching the same asset is the interesting case - that is
        // usually the answer to whatever the user is chasing.
        var enabled = hits.Count(h => h.Mod.IsEnabled);
        if (enabled > 1)
            report.AppendLine($"{enabled} of these are enabled at the same time, so they may be overriding each other.");

        LoggingService.Instance.Info(report.ToString());
        System.Windows.MessageBox.Show(report.ToString(), $"Mods containing \"{term}\"",
            System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
    }

    // ---- remembering how the list was sorted -------------------------------------------------

    /// Set while the sort is being applied programmatically, so restoring a saved sort doesn't
    /// immediately fire the save handler and write back what it just read.
    private bool _applyingSort;

    /// Starred first then alphabetical, unless the user chose something else last time.
    private void ApplySavedSort()
    {
        var settings = AppSettingsService.Instance.Current;

        _applyingSort = true;
        try
        {
            ModsView.SortDescriptions.Clear();

            // A saved column name that no longer exists on ModInfo would make the view throw on
            // every refresh, so an unrecognised one falls back rather than being trusted.
            if (!string.IsNullOrWhiteSpace(settings.ModListSortColumn) &&
                typeof(ModInfo).GetProperty(settings.ModListSortColumn) != null)
            {
                ModsView.SortDescriptions.Add(new SortDescription(
                    settings.ModListSortColumn,
                    settings.ModListSortDescending ? ListSortDirection.Descending : ListSortDirection.Ascending));
                return;
            }

            ModsView.SortDescriptions.Add(new SortDescription(nameof(ModInfo.IsFavourite), ListSortDirection.Descending));
            ModsView.SortDescriptions.Add(new SortDescription(nameof(ModInfo.Name), ListSortDirection.Ascending));
        }
        finally { _applyingSort = false; }
    }

    private void SaveSort()
    {
        if (_applyingSort) return;

        var settings = AppSettingsService.Instance.Current;
        var first = ModsView.SortDescriptions.FirstOrDefault();

        // An empty sort means the user cleared it (a third header click); record that as "no
        // preference" so the default comes back next launch rather than sticking on nothing.
        settings.ModListSortColumn = string.IsNullOrEmpty(first.PropertyName) ? null : first.PropertyName;
        settings.ModListSortDescending = first.Direction == ListSortDirection.Descending;
        AppSettingsService.Instance.Save();
    }
}
