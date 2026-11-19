using System.Text;

namespace WorklistExplorer.Core.Text;

/// <summary>
/// Append only log of diagnostic reports. Reports contain association details, the query
/// sent, counts and statuses; they never contain patient data, so the log can be kept and
/// shared. Patient data only ever goes to the raw folder the user chose.
/// </summary>
public sealed class DiagnosticLog
{
    private readonly object _sync = new();

    public DiagnosticLog(string? folder = null)
    {
        folder ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WorklistExplorer", "logs");
        Directory.CreateDirectory(folder);
        FilePath = Path.Combine(folder, $"worklist_explorer_{DateTime.Now:yyyyMMdd_HHmmss}.log");
    }

    public string FilePath { get; }

    public void Write(string text)
    {
        lock (_sync)
        {
            File.AppendAllText(FilePath, text.EndsWith(Environment.NewLine) ? text : text + Environment.NewLine,
                // No byte order mark: appending must not scatter BOMs through the file.
                new UTF8Encoding(false));
        }
    }
}
