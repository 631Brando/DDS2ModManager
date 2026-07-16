using System.Windows;
using DDS2ModManager.ViewModels;

namespace DDS2ModManager;

public partial class MainWindow : Window
{
    private MainViewModel ViewModel => (MainViewModel)DataContext;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();

        Loaded += async (_, _) =>
        {
            await ViewModel.InitializeCommand.ExecuteAsync(null);

            // If launched via "Open with DDS2 Mod Manager", install that archive now that
            // the game folder has been resolved.
            if (System.Windows.Application.Current is App app && app.PendingArchivePath is { } pending)
                await ViewModel.InstallFromPathAsync(pending);
        };

        // Auto-scroll the log to the newest entry.
        ViewModel.LogEntries.CollectionChanged += (_, _) =>
        {
            Dispatcher.InvokeAsync(() => LogScroller.ScrollToEnd());
        };
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

        var paths = (string[])e.Data.GetData(DataFormats.FileDrop);
        foreach (var path in paths)
        {
            if (Directory.Exists(path) || ArchiveExtractionService.IsSupportedArchive(path))
                await ViewModel.InstallFromPathAsync(path);
            else
                LoggingService.Instance.Warn($"Ignored '{Path.GetFileName(path)}' - not a folder or supported archive (.zip/.7z/.rar).");
        }
    }
}
