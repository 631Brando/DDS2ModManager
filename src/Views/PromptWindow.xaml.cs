using System.Windows;
using System.Windows.Input;

namespace DDS2ModManager.Views;

/// A one-line text prompt.
///
/// WPF has no equivalent of a simple input box, and the alternative is referencing the VB
/// interop assembly for InputBox - which drags a whole assembly in for one dialog and looks
/// nothing like the rest of the app.
public partial class PromptWindow : Window
{
    private PromptWindow(string title, string message, string initial)
    {
        InitializeComponent();

        Title = title;
        MessageText.Text = message;
        InputBox.Text = initial;

        // Focus and select on open, so typing replaces the suggestion rather than appending to it.
        Loaded += (_, _) => { InputBox.Focus(); InputBox.SelectAll(); };
    }

    /// Returns what the user typed, or null if they cancelled or left it blank.
    public static string? Ask(string title, string message, string initial = "")
    {
        var window = new PromptWindow(title, message, initial)
        {
            Owner = Application.Current.MainWindow
        };

        if (window.ShowDialog() != true) return null;

        var text = window.InputBox.Text?.Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        DialogResult = true;
        Close();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
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
