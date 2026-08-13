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

        // The taskbar and Alt-Tab show the title, so the version travels with any screenshot
        // somebody posts - which is usually all you get to work from in a bug report.
        Title = $"DDS2 Mod Manager  {MainViewModel.AppVersionDisplay}";

        RestoreWindowSize();
        Closing += (_, _) => SaveWindowSize();

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

    /// Restores the size the user last left the window at, or picks a sensible large default.
    ///
    /// The XAML default (1560x980) is deliberately bigger than the old 1200x780, but can't be
    /// trusted blindly - on a 1366x768 laptop it would open larger than the screen. So it's
    /// clamped to the actual work area, which also keeps the taskbar clear.
    private void RestoreWindowSize()
    {
        var settings = AppSettingsService.Instance.Current;

        var maxWidth = SystemParameters.WorkArea.Width;
        var maxHeight = SystemParameters.WorkArea.Height;

        var desiredWidth = settings.WindowWidth ?? Width;
        var desiredHeight = settings.WindowHeight ?? Height;

        Width = Math.Max(MinWidth, Math.Min(desiredWidth, maxWidth));
        Height = Math.Max(MinHeight, Math.Min(desiredHeight, maxHeight));

        if (settings.WindowMaximized) WindowState = WindowState.Maximized;
    }

    private void SaveWindowSize()
    {
        try
        {
            var settings = AppSettingsService.Instance.Current;
            settings.WindowMaximized = WindowState == WindowState.Maximized;

            // RestoreBounds holds the pre-maximize size; Width/Height would just report the
            // maximized dimensions, which would make un-maximizing later snap to full screen.
            var bounds = WindowState == WindowState.Normal
                ? new Rect(Left, Top, Width, Height)
                : RestoreBounds;

            if (bounds.Width > 0 && bounds.Height > 0)
            {
                settings.WindowWidth = bounds.Width;
                settings.WindowHeight = bounds.Height;
            }

            AppSettingsService.Instance.SaveQuiet();
        }
        catch (Exception ex)
        {
            // Never let a failure here block the app from closing.
            LoggingService.Instance.Warn($"Couldn't save window size: {ex.Message}");
        }
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// Hands the grid's selection to the ViewModel.
    ///
    /// DataGrid.SelectedItems is a plain property rather than a dependency property, so it cannot
    /// be bound. Pushing it across on change is the standard way round that, and it keeps the bulk
    /// commands free of any dependency on the window.
    private void ModGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (sender is not System.Windows.Controls.DataGrid grid) return;

        ViewModel.SetSelection(grid.SelectedItems.OfType<ModInfo>());
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
