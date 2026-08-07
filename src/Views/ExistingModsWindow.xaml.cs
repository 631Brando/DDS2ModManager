using System.Windows;

namespace DDS2ModManager.Views;

public partial class ExistingModsWindow : Window
{
    private readonly List<UnmanagedMod> _found;

    /// The mods the user ticked. Only meaningful when ShowDialog() returned true.
    public List<UnmanagedMod> SelectedMods => _found.Where(m => m.Selected).ToList();

    /// Whether to also move misplaced mods into the folder their type actually belongs in.
    public bool FixMisplaced => FixMisplacedBox.IsChecked == true;

    public ExistingModsWindow(List<UnmanagedMod> found)
    {
        InitializeComponent();
        _found = found;
        ModList.ItemsSource = found;

        HeaderText.Text = found.Count == 1
            ? "1 existing mod found"
            : $"{found.Count} existing mods found";

        if (found.Any(m => m.IsMisplaced))
            FixMisplacedBox.Visibility = Visibility.Visible;
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var m in _found) m.Selected = true;
    }

    private void SelectNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var m in _found) m.Selected = false;
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
