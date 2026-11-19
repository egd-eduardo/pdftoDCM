using System.Text;
using FellowOakDicom;

namespace WorklistExplorer.Core.Dicom;

/// <summary>
/// Writes a dataset as readable text, one attribute per line, nested sequences indented.
/// </summary>
public static class DatasetFormatter
{
    private const int MaximumValueLength = 1024;

    /// <param name="emptyMeaning">
    /// What an empty value means in this context. In a query it is universal matching; in a
    /// response it means the server returned the attribute without a value. Saying which
    /// avoids the reader having to remember the rule.
    /// </param>
    public static string Format(DicomDataset dataset, string emptyMeaning)
    {
        var sb = new StringBuilder();
        Append(sb, dataset, 0, emptyMeaning);
        return sb.ToString();
    }

    private static void Append(StringBuilder sb, DicomDataset ds, int depth, string emptyMeaning)
    {
        var indent = new string(' ', depth * 2);
        foreach (var item in ds)
        {
            var tag = item.Tag;
            var keyword = tag.DictionaryEntry?.Keyword ?? "";
            if (string.IsNullOrEmpty(keyword)) keyword = "Unknown or private";
            var head = $"{indent}{tag} {item.ValueRepresentation.Code} {keyword}";

            switch (item)
            {
                case DicomSequence seq:
                    sb.AppendLine($"{head}: {seq.Items.Count} item(s)");
                    for (var i = 0; i < seq.Items.Count; i++)
                    {
                        sb.AppendLine($"{indent}  item {i + 1}");
                        Append(sb, seq.Items[i], depth + 2, emptyMeaning);
                    }
                    break;

                case DicomElement element when element.Buffer is null || element.Buffer.Size == 0:
                    sb.AppendLine($"{head} = <empty: {emptyMeaning}>");
                    break;

                default:
                    sb.AppendLine($"{head} = {ValueText(ds, tag)}");
                    break;
            }
        }
    }

    private static string ValueText(DicomDataset ds, DicomTag tag)
    {
        try
        {
            if (!ds.TryGetString(tag, out var value)) return "<value could not be read as text>";
            // Quotes make leading and trailing spaces visible, which matters for exact matching.
            if (value.Length > MaximumValueLength)
                return $"'{value[..MaximumValueLength]}' <truncated for display, {value.Length} characters>";
            return $"'{value}'";
        }
        catch (Exception ex)
        {
            // A broken value in one attribute must not hide the others.
            return $"<value could not be decoded: {ex.Message}>";
        }
    }
}
