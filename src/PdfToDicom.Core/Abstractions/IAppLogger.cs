namespace PdfToDicom.Core.Abstractions;

/// <summary>
/// Severity levels used by the application log and by the in application log viewer.
/// </summary>
public enum LogLevel
{
    /// <summary>Detailed diagnostic information, off by default in production.</summary>
    Debug = 0,

    /// <summary>Normal progress information.</summary>
    Information = 1,

    /// <summary>Something unexpected that did not stop the workflow.</summary>
    Warning = 2,

    /// <summary>A failure that stopped the current operation.</summary>
    Error = 3
}

/// <summary>
/// The application's logging surface.
/// </summary>
/// <remarks>
/// A dedicated abstraction is used rather than exposing the logging library directly.
/// That keeps the rest of the code independent of the sink implementation and makes it
/// straightforward to assert on log output in the automated tests.
/// </remarks>
public interface IAppLogger
{
    /// <summary>Writes a debug level entry.</summary>
    void Debug(string message);

    /// <summary>Writes an information level entry.</summary>
    void Information(string message);

    /// <summary>Writes a warning level entry.</summary>
    void Warning(string message);

    /// <summary>Writes an error level entry, optionally with the exception that caused it.</summary>
    void Error(string message, Exception? exception = null);

    /// <summary>Folder containing the log files.</summary>
    string LogFolder { get; }

    /// <summary>Flushes any buffered entries to disk.</summary>
    void Flush();
}
