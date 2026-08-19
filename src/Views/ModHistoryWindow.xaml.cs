using System.Windows;

namespace DDS2ModManager.Views;

/// Shows what has happened to the user's mods, newest first.
public partial class ModHistoryWindow : Window
{
    private readonly ModHistoryService _history;

    /// Takes the history for the active game rather than reaching for a singleton: the record is
    /// per game install now, and a window that resolved its own would show the wrong game's.
    public ModHistoryWindow(ModHistoryService history)
    {
        _history = history;
        InitializeComponent();
        Reload();
    }

    private void Reload()
    {
        var entries = _history.Entries;
        HistoryList.ItemsSource = entries;

        // An empty list with a heading above it reads as broken; say why it is empty instead.
        EmptyText.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(
                "Clear the mod history? This only forgets the record of what changed - it does not touch any mod.",
                "Clear history", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        _history.Clear();
        Reload();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
