using System.Windows;

namespace DDS2ModManager.Views;

/// Shows what has happened to the user's mods, newest first.
public partial class ModHistoryWindow : Window
{
    public ModHistoryWindow()
    {
        InitializeComponent();
        Reload();
    }

    private void Reload()
    {
        var entries = ModHistoryService.Instance.Entries;
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

        ModHistoryService.Instance.Clear();
        Reload();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
