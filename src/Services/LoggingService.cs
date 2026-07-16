namespace DDS2ModManager.Services;

public enum LogLevel { Info, Warning, Error, Success }

public class LogEntry
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public LogLevel Level { get; set; }
    public string Message { get; set; } = "";
}

/// Single shared logger: writes to an ObservableCollection (for the on-screen log panel)
/// and to a timestamped file under %AppData%\DDS2ModManager\Logs, so users can attach a
/// log file when reporting install problems.
public class LoggingService
{
    private static readonly Lazy<LoggingService> _instance = new(() => new LoggingService());
    public static LoggingService Instance => _instance.Value;

    public ObservableCollection<LogEntry> Entries { get; } = new();

    private readonly string _logFilePath;
    private readonly object _fileLock = new();

    private LoggingService()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DDS2ModManager", "Logs");
        Directory.CreateDirectory(dir);
        _logFilePath = Path.Combine(dir, $"log_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
    }

    public void Log(string message, LogLevel level = LogLevel.Info)
    {
        var entry = new LogEntry { Message = message, Level = level };

        // Entries is bound to the UI, so mutate it on the UI thread.
        if (System.Windows.Application.Current?.Dispatcher.CheckAccess() == false)
            System.Windows.Application.Current.Dispatcher.Invoke(() => Entries.Add(entry));
        else
            Entries.Add(entry);

        lock (_fileLock)
        {
            try
            {
                File.AppendAllText(_logFilePath, $"[{entry.Timestamp:HH:mm:ss}] [{level}] {message}{Environment.NewLine}");
            }
            catch { /* logging must never crash the app */ }
        }
    }

    public void Info(string m) => Log(m, LogLevel.Info);
    public void Warn(string m) => Log(m, LogLevel.Warning);
    public void Error(string m) => Log(m, LogLevel.Error);
    public void Success(string m) => Log(m, LogLevel.Success);

    /// Exports everything currently shown in the log panel to a plain text file
    /// (separate from the automatic per-session log file in %AppData%).
    public void ExportToFile(string path)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var entry in Entries)
            sb.AppendLine($"[{entry.Timestamp:yyyy-MM-dd HH:mm:ss}] [{entry.Level}] {entry.Message}");
        File.WriteAllText(path, sb.ToString());
    }
}
