using System.Globalization;

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
/// log file when reporting install problems. One file per launch, oldest pruned on startup
/// so the folder stays bounded - see MaxLogFilesToKeep.
public class LoggingService
{
    private static readonly Lazy<LoggingService> _instance = new(() => new LoggingService());
    public static LoggingService Instance => _instance.Value;

    public ObservableCollection<LogEntry> Entries { get; } = new();

    /// The three pieces of a per-session log file's name. They are here rather than inline
    /// because the same shape has to be produced when writing and recognised when pruning -
    /// a rotation rule matching a pattern the writer doesn't actually produce would silently
    /// never delete anything, and nobody would notice until the folder had hundreds of files
    /// in it again.
    private const string LogFilePrefix = "log_";
    private const string LogFileExtension = ".txt";
    private const string LogFileTimestampFormat = "yyyyMMdd_HHmmss";

    /// How many per-session log files survive a launch, this session's file included.
    ///
    /// Count-based rather than age-based, deliberately. "Delete anything older than N days"
    /// bounds nothing on the machine that needs bounding: someone fighting a broken install
    /// relaunches the manager twenty times in one afternoon and keeps all twenty, which is
    /// exactly how a real machine ended up with 41 files. It also throws away the wrong ones
    /// at the other extreme - open the manager twice a year and the age rule deletes the only
    /// previous log there is, for the sake of a few KB. A count holds under both patterns and
    /// always leaves the most recent runs, which is what a bug report needs. Twenty is about
    /// one bad troubleshooting session's worth of relaunches and still few enough to page
    /// through by hand in Explorer.
    private const int MaxLogFilesToKeep = 20;

    private readonly string _logFilePath;
    private readonly object _fileLock = new();

    private LoggingService()
    {
        var dir = AppPaths.Logs;
        Directory.CreateDirectory(dir);
        _logFilePath = Path.Combine(dir, $"{LogFilePrefix}{DateTime.Now.ToString(LogFileTimestampFormat)}{LogFileExtension}");

        PruneOldLogs(dir, _logFilePath);
    }

    /// Deletes the oldest log files until at most MaxLogFilesToKeep remain.
    ///
    /// Run synchronously on purpose, not pushed onto the thread pool: after the first sweep
    /// catches up with the backlog this is one enumeration of one small folder and exactly one
    /// delete per launch, because each launch adds exactly one file. That is cheap enough to
    /// pay on the startup path, and doing it inline means the sweep can't still be deleting
    /// files while the process is shutting down.
    ///
    /// Static, and told which folder and which file to spare, rather than reading the instance
    /// fields: it is called from the constructor, so the Lazy singleton is not published yet
    /// and touching LoggingService.Instance from in here would re-enter the value factory and
    /// throw. That is also why failures are swallowed rather than logged - there is no logger
    /// to log them to yet.
    private static void PruneOldLogs(string dir, string currentSessionFile)
    {
        try
        {
            var candidates = new List<(string Path, DateTime Stamp)>();

            // The glob is the first filter, never the last. A search pattern whose extension
            // is exactly three characters is documented to also match files whose extension
            // merely BEGINS with it - the "*.xls returns book.xlsx" case - so "log_*.txt" can
            // hand back "log_old.txt.bak". It doesn't happen on every machine (it didn't
            // reproduce on the one this was written on), which makes it worse rather than
            // better: a delete that only misfires on someone else's volume is not something
            // to leave to chance. So every candidate has to carry the exact name this service
            // writes - our prefix, our extension, and a middle that parses back as our
            // timestamp. Anything else living in this folder belongs to someone else and is
            // left alone.
            foreach (var file in Directory.EnumerateFiles(
                         dir, LogFilePrefix + "*" + LogFileExtension, SearchOption.TopDirectoryOnly))
            {
                // Never this session's own file. It normally doesn't exist yet (the first
                // Log call creates it), but two launches inside the same second produce the
                // same name, and then "a file with that name is already there" would otherwise
                // read as "an old file" - and we would delete the log we are about to write.
                if (string.Equals(file, currentSessionFile, StringComparison.OrdinalIgnoreCase)) continue;

                var name = Path.GetFileName(file);
                if (!name.StartsWith(LogFilePrefix, StringComparison.Ordinal)) continue;
                if (!name.EndsWith(LogFileExtension, StringComparison.OrdinalIgnoreCase)) continue;

                // Current culture, not invariant, because the name above was formatted with
                // the current culture and the parse has to mirror the writer exactly. On a
                // machine set to a non-Gregorian calendar (th-TH is Buddhist, ar-SA is
                // UmAlQura) DateTime.Now writes a Buddhist/Hijri year - this machine's own
                // logs are named log_25690812_... or log_14480229_..., and only a matching
                // parse maps them back to the instant they were written.
                //
                // An invariant parse mostly APPEARS to work, which is the trap: measured over
                // 365 consecutive days it rejects nothing on th-TH (it reads 2569 as a plain
                // Gregorian year, and the ordering still comes out right), so nothing looks
                // broken until a Hijri day with no Gregorian counterpart - month 2, day 30 -
                // produces a name it refuses, and that one file silently stops being prunable.
                var stampText = name[LogFilePrefix.Length..^LogFileExtension.Length];
                if (!DateTime.TryParseExact(stampText, LogFileTimestampFormat,
                        CultureInfo.CurrentCulture, DateTimeStyles.None, out var stamp))
                    continue;

                candidates.Add((file, stamp));
            }

            // Ordered by the timestamp in the name rather than LastWriteTime: the name is the
            // part this service controls, and a log restored from a backup or copied off
            // another machine carries a write time that says nothing about which session
            // produced it. Keep one fewer than the budget - this session's file is one of the
            // survivors and was skipped above.
            var doomed = candidates
                .OrderByDescending(c => c.Stamp)
                .Skip(Math.Max(MaxLogFilesToKeep - 1, 0));

            foreach (var (path, _) in doomed)
            {
                // Per file, so one log someone left open in Notepad (or that an antivirus
                // scanner still holds a handle on) doesn't abandon the rest of the sweep.
                // Whatever is missed here is simply retried on the next launch.
                try { File.Delete(path); }
                catch { /* in use, read-only, or already gone */ }
            }
        }
        catch { /* logging must never crash the app */ }
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
