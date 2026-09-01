using System.Windows;

namespace DDS2ModManager.Views;

public partial class VariantSelectionWindow : Window
{
    public string? SelectedPath { get; private set; }

    private readonly Dictionary<string, string> _displayToPath = new();

    public VariantSelectionWindow(List<string> candidatePaths)
    {
        InitializeComponent();

        foreach (var path in candidatePaths)
        {
            // Say what each one IS, not just what it is called. On the shapes this dialog still
            // reaches, a bare folder name gives the user nothing to decide on.
            var kind = Directory.EnumerateFiles(path, "*.pak", SearchOption.AllDirectories).Any()
                ? "pak"
                : "script mod";

            var display = $"{Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar))}  -  {kind}";
            _displayToPath[display] = path;
            VariantList.Items.Add(display);
        }

        if (VariantList.Items.Count > 0)
            VariantList.SelectedIndex = 0;
    }

    private void Install_Click(object sender, RoutedEventArgs e)
    {
        if (VariantList.SelectedItem is not string display)
        {
            MessageBox.Show("Select a version first.", AppPaths.AppDisplayName, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SelectedPath = _displayToPath[display];
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
