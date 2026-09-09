using PdfToDicom.Core.Abstractions;

namespace PdfToDicom.Core.Logging;

/// <summary>
/// A single log entry retained in memory for display in the application's log viewer.
/// </summary>
/// <param name="Timestamp">Local time at which the entry was written.</param>
/// <param name="Level">Severity of the entry.</param>
/// <param name="Message">The message text.</param>
/// <param name="ExceptionDetail">Exception detail, when the entry recorded one.</param>
public sealed record LogEntry(DateTime Timestamp, LogLevel Level, string Message, string? ExceptionDetail)
{
    /// <summary>Formats the entry the same way it appears in the log file.</summary>
    public override string ToString()
    {
        string line = $"{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level.ToString().ToUpperInvariant()}] {Message}";
        return ExceptionDetail is null ? line : line + Environment.NewLine + ExceptionDetail;
    }
}
