using System.Windows;

namespace DDS2ModManager;

public partial class App : Application
{
    /// A file path passed on the command line (from the "Open with DDS2 Mod Manager"
    /// right-click entry). MainWindow picks this up once it has finished initializing
    /// and located the game folder.
    public string? PendingArchivePath { get; private set; }

    private DateTime _lastExceptionDialogAt = DateTime.MinValue;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Windows "Apps & Features" -> Uninstall runs "<exe>" --uninstall (see the UninstallString
        // the Setup installer writes to the registry). Handle it before anything else initializes
        // and exit without ever showing the main window.
        if (e.Args.Any(a => string.Equals(a, "--uninstall", StringComparison.OrdinalIgnoreCase)))
        {
            var result = MessageBox.Show(
                "Uninstall DDS2 Mod Manager?\n\nThis removes the installed program and its shortcuts. Your settings, " +
                "mod tracking, logs, and any disabled-mod files stay in %AppData%\\DDS2ModManager in case you reinstall later.",
                "Uninstall DDS2 Mod Manager", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result == MessageBoxResult.Yes)
                AppUninstaller.Run();

            Shutdown();
            return;
        }

        // Any unhandled exception should be logged, not silently crash the process.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            LoggingService.Instance.Error($"Unhandled exception: {args.ExceptionObject}");
        };
        DispatcherUnhandledException += (_, args) =>
        {
            LoggingService.Instance.Error($"UI exception: {args.Exception}");

            // A single bad binding (or any other recoverable UI-thread exception) can refire on
            // every subsequent layout pass - e.g. once per item in a list - which previously meant
            // one bug produced dozens of MessageBox.Show() calls in under a second. Each dialog
            // pumps the message loop, which lets layout continue and immediately throw again before
            // the user can even read/dismiss the first one - indistinguishable from a hard crash and
            // usually only resolved by killing the process. Throttle to at most one dialog per burst;
            // every occurrence still gets logged in full (with stack trace) either way.
            var now = DateTime.UtcNow;
            if (now - _lastExceptionDialogAt > TimeSpan.FromSeconds(3))
            {
                _lastExceptionDialogAt = now;
                MessageBox.Show(
                    $"{args.Exception.Message}\n\nThis has been logged. If it keeps happening, use Settings > Reset App Data to clear cached state.",
                    "DDS2 Mod Manager - Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }

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
