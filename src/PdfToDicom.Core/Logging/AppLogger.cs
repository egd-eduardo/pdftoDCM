using System.Collections.Concurrent;
using PdfToDicom.Core.Abstractions;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace PdfToDicom.Core.Logging;

/// <summary>
/// Serilog backed application logger that also retains recent entries in memory for the
/// in application log viewer.
/// </summary>
/// <remarks>
/// Log files roll daily and are retained for a configurable number of days. Writes are
/// shared so that a support engineer can copy or open the current log while the
/// application is still running, which is a routine need on a hospital workstation.
/// </remarks>
public sealed class AppLogger : IAppLogger, IDisposable
{
    private const int MaxRetainedEntries = 2000;

    private readonly Logger _logger;
    private readonly ConcurrentQueue<LogEntry> _recentEntries = new();
    private readonly bool _includeDebug;
    private bool _disposed;

    /// <summary>Raised whenever an entry is written, so the log viewer can append it live.</summary>
    public event EventHandler<LogEntry>? EntryWritten;

    /// <summary>
    /// Creates the logger.
    /// </summary>
    /// <param name="logFolder">Folder into which log files are written.</param>
    /// <param name="retentionDays">Days of history to keep. Zero keeps files indefinitely.</param>
    /// <param name="includeDebug">Include debug level entries. Enabled by a support switch.</param>
    public AppLogger(string logFolder, int retentionDays = 90, bool includeDebug = false)
    {
        LogFolder = ConfigurationPathsSafeCreate(logFolder);
        _includeDebug = includeDebug;

        var configuration = new LoggerConfiguration()
            .MinimumLevel.Is(includeDebug ? LogEventLevel.Debug : LogEventLevel.Information)
            .WriteTo.File(
                path: Path.Combine(LogFolder, "pdftodicom-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: retentionDays <= 0 ? null : retentionDays,
                shared: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}");

        _logger = configuration.CreateLogger();
    }

    /// <inheritdoc />
    public string LogFolder { get; }

    /// <summary>Recent entries, oldest first, for display in the log viewer.</summary>
    public IReadOnlyList<LogEntry> RecentEntries => _recentEntries.ToArray();

    /// <inheritdoc />
    public void Debug(string message)
    {
        if (!_includeDebug)
        {
            return;
        }

        _logger.Debug("{Message}", message);
        Record(new LogEntry(DateTime.Now, LogLevel.Debug, message, null));
    }

    /// <inheritdoc />
    public void Information(string message)
    {
        _logger.Information("{Message}", message);
        Record(new LogEntry(DateTime.Now, LogLevel.Information, message, null));
    }

    /// <inheritdoc />
    public void Warning(string message)
    {
        _logger.Warning("{Message}", message);
        Record(new LogEntry(DateTime.Now, LogLevel.Warning, message, null));
    }

    /// <inheritdoc />
    public void Error(string message, Exception? exception = null)
    {
        if (exception is null)
        {
            _logger.Error("{Message}", message);
        }
        else
        {
            _logger.Error(exception, "{Message}", message);
        }

        Record(new LogEntry(DateTime.Now, LogLevel.Error, message, exception?.ToString()));
    }

    /// <inheritdoc />
    public void Flush()
    {
        // Serilog's file sink writes synchronously in this configuration, so flushing is
        // a matter of disposing buffers held by the sink.
        Serilog.Log.CloseAndFlush();
    }

    private void Record(LogEntry entry)
    {
        _recentEntries.Enqueue(entry);
        while (_recentEntries.Count > MaxRetainedEntries && _recentEntries.TryDequeue(out _))
        {
            // Bound memory use during long running sessions.
        }

        EntryWritten?.Invoke(this, entry);
    }

    /// <summary>
    /// Creates the log folder, falling back to a temporary location if the configured
    /// folder cannot be created. An application that cannot log is far less useful than
    /// one that logs somewhere unexpected, so this never throws.
    /// </summary>
    private static string ConfigurationPathsSafeCreate(string logFolder)
    {
        try
        {
            Directory.CreateDirectory(logFolder);
            return logFolder;
        }
        catch
        {
            string fallback = Path.Combine(Path.GetTempPath(), "PdfToDicom", "Logs");
            Directory.CreateDirectory(fallback);
            return fallback;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _logger.Dispose();
    }
}
