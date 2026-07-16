using System.Windows;

namespace DDS2ModManager;

public partial class App : Application
{
    /// A file path passed on the command line (from the "Open with DDS2 Mod Manager"
    /// right-click entry). MainWindow picks this up once it has finished initializing
    /// and located the game folder.
    public string? PendingArchivePath { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Any unhandled exception should be logged, not silently crash the process.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            LoggingService.Instance.Error($"Unhandled exception: {args.ExceptionObject}");
        };
        DispatcherUnhandledException += (_, args) =>
        {
            LoggingService.Instance.Error($"UI exception: {args.Exception.Message}");
            MessageBox.Show(args.Exception.Message, "DDS2 Mod Manager - Error", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        // First non-switch argument that points at a real file is treated as a mod to install.
        foreach (var arg in e.Args)
        {
            if (!arg.StartsWith("-") && File.Exists(arg))
            {
                PendingArchivePath = arg;
                break;
            }
        }

        var window = new MainWindow();
        window.Show();
    }
}
