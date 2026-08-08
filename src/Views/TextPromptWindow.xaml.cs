using System.Windows;

namespace DDS2ModManager.Views;

/// Small reusable "type a name" dialog - WPF has no built-in equivalent of an input box.
public partial class TextPromptWindow : Window
{
    public string EnteredText => InputBox.Text.Trim();

    /// <param name="note">
    /// Optional caveat shown under the input. Used where the consequence of confirming isn't
    /// obvious from the prompt alone - a clone being undone by Steam Cloud, for instance - so it
    /// appears while the decision is being made rather than after it.
    /// </param>
    public TextPromptWindow(string title, string prompt, string initialValue = "", string? note = null)
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        InputBox.Text = initialValue;

        if (!string.IsNullOrWhiteSpace(note))
        {
            NoteText.Text = note;
            NoteBox.Visibility = Visibility.Visible;
        }

        Loaded += (_, _) => { InputBox.Focus(); InputBox.SelectAll(); };
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(InputBox.Text))
        {
            MessageBox.Show("Enter a name.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
