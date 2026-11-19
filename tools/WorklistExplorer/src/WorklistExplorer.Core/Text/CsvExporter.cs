using System.Text;
using WorklistExplorer.Core.Query;

namespace WorklistExplorer.Core.Text;

public static class CsvExporter
{
    /// <summary>
    /// UTF-8 with a byte order mark, because without it Excel assumes the local ANSI code
    /// page and turns GONÇALVES into GONÃ‡ALVES.
    /// </summary>
    public static readonly Encoding Utf8WithBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    public static void Write(string path, IEnumerable<WorklistRow> rows, char separator = ',')
    {
        using var writer = new StreamWriter(path, append: false, Utf8WithBom);
        Write(writer, rows, separator);
    }

    public static void Write(TextWriter writer, IEnumerable<WorklistRow> rows, char separator = ',')
    {
        var columns = WorklistRow.Columns;
        writer.WriteLine(string.Join(separator, columns.Select(c => Escape(c.Header, separator))));
        foreach (var row in rows)
            writer.WriteLine(string.Join(separator, columns.Select(c => Escape(c.Get(row), separator))));
    }

    /// <summary>
    /// RFC 4180 quoting. Values are otherwise written exactly as received; leading and
    /// trailing spaces are preserved by quoting, since they can matter for exact matching.
    /// </summary>
    private static string Escape(string value, char separator)
    {
        var needsQuotes = value.IndexOfAny([separator, '"', '\r', '\n']) >= 0
                          || (value.Length > 0 && (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1])));
        return needsQuotes ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }
}
