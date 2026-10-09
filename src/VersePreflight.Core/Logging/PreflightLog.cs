namespace VersePreflight.Core.Logging;

public enum PreflightLogLevel { Info, Success, Warning, Error }

public sealed record LogEntry(DateTimeOffset Time, PreflightLogLevel Level, string Message);

/// <summary>
/// Log shared by all features. The GUI subscribes to <see cref="EntryWritten"/>;
/// entries are also appended to a per-machine log file (rotated at <c>maxBytes</c>).
/// </summary>
public sealed class PreflightLog
{
    private readonly string? _filePath;
    private readonly Lock _lock = new();
    private int _errorCount;

    public PreflightLog(string? filePath = null, long maxBytes = 1024 * 1024)
    {
        _filePath = filePath;
        if (_filePath is null) return;

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_filePath))!);
        var info = new FileInfo(_filePath);
        if (info.Exists && info.Length > maxBytes)
            File.Move(_filePath, _filePath + ".old", overwrite: true);
    }

    public event Action<LogEntry>? EntryWritten;

    public int ErrorCount => Volatile.Read(ref _errorCount);

    public string? FilePath => _filePath;

    public void Info(string message) => Write(PreflightLogLevel.Info, message);
    public void Success(string message) => Write(PreflightLogLevel.Success, message);
    public void Warning(string message) => Write(PreflightLogLevel.Warning, message);
    public void Error(string message) => Write(PreflightLogLevel.Error, message);

    public void Write(PreflightLogLevel level, string message)
    {
        var entry = new LogEntry(DateTimeOffset.Now, level, message);
        if (level == PreflightLogLevel.Error) Interlocked.Increment(ref _errorCount);

        if (_filePath is not null)
        {
            var line = $"[{entry.Time:yyyy-MM-dd HH:mm:ss}] [{level.ToString().ToUpperInvariant()}] {message}{Environment.NewLine}";
            lock (_lock)
            {
                try { File.AppendAllText(_filePath, line); }
                catch (IOException) { /* logging must never break an operation */ }
            }
        }

        EntryWritten?.Invoke(entry);
    }
}
