using System.Windows;
using System.Windows.Input;

namespace DDS2ModManager.Views;

public partial class UE4SSBuildSelectionWindow : Window
{
    public bool UseDevBuild { get; private set; }

    public UE4SSBuildSelectionWindow(bool preferDev)
    {
        InitializeComponent();
        if (preferDev) DevOption.IsChecked = true;
        else StandardOption.IsChecked = true;
    }

    private void StandardOption_Click(object sender, MouseButtonEventArgs e) => StandardOption.IsChecked = true;
    private void DevOption_Click(object sender, MouseButtonEventArgs e) => DevOption.IsChecked = true;

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        UseDevBuild = DevOption.IsChecked == true;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
