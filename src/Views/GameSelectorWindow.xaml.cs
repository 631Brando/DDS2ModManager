using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using DDS2ModManager.ViewModels;

namespace DDS2ModManager.Views;

/// The game picker: every Unreal game the catalog found, with its box art.
///
/// It only CHOOSES. The chosen entry (or a request to add a folder) is handed back to the main
/// window, which runs it through the view model's gated transition - so a game is never opened from
/// two places with two sets of rules, and the picker can't start a switch while another is running.
public partial class GameSelectorWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly ListCollectionView _full;
    private readonly ListCollectionView _other;
    private readonly ListCollectionView _leftovers;
    private bool _leftoversShown;

    /// The game the user picked, read by the caller once the window has closed.
    public GameEntryViewModel? ChosenEntry { get; private set; }

    /// The user asked to add a game by folder. Done by the caller after this window closes, so the
    /// folder dialog isn't stacked on top of the picker.
    public bool AddFolderRequested { get; private set; }

    public GameSelectorWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;

        // Three independent views over the one catalog collection. Not GetDefaultView, which is
        // shared - filtering it would filter every other list bound to GameEntries.
        _full = SectionView(e => e.IsFullSupport);
        _other = SectionView(e => !e.IsFullSupport && e.IsInstalled);
        _leftovers = SectionView(e => !e.IsFullSupport && e.IsLeftover);

        FullList.ItemsSource = _full;
        OtherList.ItemsSource = _other;
        LeftoverList.ItemsSource = _leftovers;

        _vm.GameEntries.CollectionChanged += OnEntriesChanged;
        _vm.PropertyChanged += OnViewModelChanged;
        Closed += (_, _) =>
        {
            _vm.GameEntries.CollectionChanged -= OnEntriesChanged;
            _vm.PropertyChanged -= OnViewModelChanged;
        };

        Loaded += (_, _) =>
        {
            SearchBox.Focus();
            foreach (var entry in _vm.GameEntries) _ = entry.LoadArtAsync(includeHero: entry.IsActive);
        };

        Refresh();
    }

    private ListCollectionView SectionView(Func<GameEntryViewModel, bool> inSection) =>
        new(_vm.GameEntries) { Filter = o => o is GameEntryViewModel e && inSection(e) && MatchesSearch(e) };

    private bool MatchesSearch(GameEntryViewModel entry)
    {
        var text = SearchBox?.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return true;

        // Every word has to appear somewhere, in any order - "dealer 2" finds Drug Dealer Simulator 2.
        return text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .All(word => entry.SearchText.Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e) => Refresh();

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        // A scan finishing changes which section an entry belongs in (installed vs leftover) via a
        // property change, which a filtered view doesn't notice on its own.
        if (e.PropertyName is nameof(MainViewModel.IsScanningGames)) Refresh();
    }

    /// Re-applies the filters and rewrites every line that depends on what's listed.
    private void Refresh()
    {
        // TextChanged can fire while InitializeComponent is still building the window, before the
        // views below exist.
        if (_full == null || _other == null || _leftovers == null) return;

        _full.Refresh();
        _other.Refresh();
        _leftovers.Refresh();

        var scanning = _vm.IsScanningGames;
        var searching = !string.IsNullOrWhiteSpace(SearchBox.Text);
        var installed = _vm.GameEntries.Count(e => e.IsInstalled);

        SubtitleText.Text = scanning
            ? "Looking through your Steam, Epic and GOG libraries…"
            : installed == 1
                ? "1 installed Unreal Engine game found"
                : $"{installed} installed Unreal Engine games found";

        RescanButton.Content = scanning ? "Scanning…" : "↻  Rescan";
        RescanButton.IsEnabled = !scanning;

        var otherCount = _other.Count;

        // While searching, a section with no matches drops its heading as well, rather than
        // leaving a title over nothing.
        var fullShown = !searching || _full.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        FullHeader.Visibility = fullShown;
        FullNote.Visibility = fullShown;
        var otherShown = !searching || otherCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        OtherHeader.Visibility = otherShown;
        OtherNote.Visibility = otherShown;

        OtherEmpty.Visibility = otherCount == 0 && !searching ? Visibility.Visible : Visibility.Collapsed;
        OtherEmptyText.Text = scanning
            ? "Looking for other Unreal Engine games…"
            : "No other Unreal Engine games were found in your Steam, Epic or GOG libraries. Use \"Add game folder…\" " +
              "for anything installed somewhere else.";

        var leftoverCount = _leftovers.Count;
        LeftoverToggle.Visibility = leftoverCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        LeftoverToggle.Content = $"{(_leftoversShown ? "▾" : "▸")}  {leftoverCount} uninstalled game{(leftoverCount == 1 ? "" : "s")} " +
                                 "left files behind";
        LeftoverList.Visibility = _leftoversShown && leftoverCount > 0 ? Visibility.Visible : Visibility.Collapsed;

        var anything = _full.Count + otherCount + leftoverCount > 0;
        NoMatchesText.Visibility = searching && !anything ? Visibility.Visible : Visibility.Collapsed;
        NoMatchesText.Text = $"Nothing matches \"{SearchBox.Text.Trim()}\".";
    }

    private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        SearchHint.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        Refresh();
    }

    private void Card_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not GameEntryViewModel entry) return;
        Choose(entry);
    }

    private void Choose(GameEntryViewModel entry)
    {
        // The open game: nothing to do, just close - it reads as "yes, this one" rather than a
        // dead click.
        if (!entry.IsActive) ChosenEntry = entry;
        Close();
    }

    private void Forget_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not GameEntryViewModel entry) return;
        _vm.ForgetGameCommand.Execute(entry);
        Refresh();
    }

    private void OpenLeftoverFolder_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is GameEntryViewModel entry)
            _vm.OpenGameFolderCommand.Execute(entry);
    }

    private void LeftoverToggle_Click(object sender, RoutedEventArgs e)
    {
        _leftoversShown = !_leftoversShown;
        Refresh();
    }

    private async void Rescan_Click(object sender, RoutedEventArgs e)
    {
        await _vm.RefreshCatalogAsync();
        Refresh();
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        AddFolderRequested = true;
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// A borderless window has no title bar to drag, so the header is one.
    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        try { DragMove(); }
        catch (InvalidOperationException) { /* the button was released before the drag began */ }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                e.Handled = true;
                break;

            case Key.F when Keyboard.Modifiers == ModifierKeys.Control:
                SearchBox.Focus();
                SearchBox.SelectAll();
                e.Handled = true;
                break;

            case Key.Enter:
                // A focused card means "that one"; otherwise the first match, preferring a game that
                // can actually be opened over a "not installed" row.
                var target = (Keyboard.FocusedElement as FrameworkElement)?.Tag as GameEntryViewModel
                             ?? _full.Cast<GameEntryViewModel>().Concat(_other.Cast<GameEntryViewModel>())
                                 .OrderByDescending(x => x.IsInstalled)
                                 .FirstOrDefault();
                if (target != null) Choose(target);
                e.Handled = true;
                break;
        }
    }
}
