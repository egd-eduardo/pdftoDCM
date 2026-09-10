using System.Text;
using FellowOakDicom;

namespace PdfToDicom.Core.Dicom;

/// <summary>
/// One row in the tag viewer.
/// </summary>
/// <param name="Group">Tag group, as a four digit hexadecimal string.</param>
/// <param name="Element">Tag element, as a four digit hexadecimal string.</param>
/// <param name="TagDisplay">The tag in the conventional "(0010,0020)" notation.</param>
/// <param name="Vr">Value Representation, for example "LO".</param>
/// <param name="Name">Dictionary name of the attribute.</param>
/// <param name="Value">The value, rendered for display.</param>
/// <param name="Length">Length of the encoded value in bytes, where known.</param>
/// <param name="Depth">Nesting depth, used to indent items inside sequences.</param>
/// <param name="Section">Whether the row belongs to the file meta group or to the data set.</param>
public sealed record DicomTagRow(
    string Group,
    string Element,
    string TagDisplay,
    string Vr,
    string Name,
    string Value,
    int Length,
    int Depth,
    string Section);

/// <summary>
/// Flattens a DICOM object into a list of rows for the tag viewer, allowing an advanced
/// user to inspect every generated attribute before the object is transmitted.
/// </summary>
public sealed class DicomTagInspector
{
    /// <summary>
    /// Maximum number of characters rendered for a single value. Binary attributes such
    /// as the encapsulated document are summarised rather than dumped in full.
    /// </summary>
    private const int MaximumRenderedValueLength = 512;

    /// <summary>Section label applied to rows from the file meta information group.</summary>
    public const string FileMetaSection = "File Meta Information";

    /// <summary>Section label applied to rows from the main data set.</summary>
    public const string DatasetSection = "Data Set";

    /// <summary>Produces the rows for a complete file, meta information first.</summary>
    public IReadOnlyList<DicomTagRow> Inspect(DicomFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        var rows = new List<DicomTagRow>();
        AppendDataset(file.FileMetaInfo, rows, depth: 0, section: FileMetaSection);
        AppendDataset(file.Dataset, rows, depth: 0, section: DatasetSection);

        return rows;
    }

    /// <summary>Produces the rows for a bare data set.</summary>
    public IReadOnlyList<DicomTagRow> Inspect(DicomDataset dataset)
    {
        ArgumentNullException.ThrowIfNull(dataset);

        var rows = new List<DicomTagRow>();
        AppendDataset(dataset, rows, depth: 0, section: DatasetSection);

        return rows;
    }

    /// <summary>
    /// Renders the rows as text, in a layout close to that produced by the common command
    /// line dump utilities, so it can be pasted into a support ticket.
    /// </summary>
    public string ToText(IReadOnlyList<DicomTagRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var builder = new StringBuilder();
        string? currentSection = null;

        foreach (DicomTagRow row in rows)
        {
            if (row.Section != currentSection)
            {
                if (currentSection is not null)
                {
                    builder.AppendLine();
                }

                builder.AppendLine($"# {row.Section}");
                currentSection = row.Section;
            }

            builder.Append(new string(' ', row.Depth * 2));
            builder.AppendLine($"{row.TagDisplay} {row.Vr} {row.Name,-42} [{row.Value}]");
        }

        return builder.ToString();
    }

    /// <summary>Walks a data set, recursing into sequence items.</summary>
    private void AppendDataset(DicomDataset dataset, List<DicomTagRow> rows, int depth, string section)
    {
        foreach (DicomItem item in dataset)
        {
            if (item is DicomSequence sequence)
            {
                rows.Add(CreateRow(
                    item,
                    value: $"{sequence.Items.Count} item(s)",
                    length: 0,
                    depth: depth,
                    section: section));

                int itemIndex = 0;
                foreach (DicomDataset sequenceItem in sequence.Items)
                {
                    rows.Add(new DicomTagRow(
                        Group: string.Empty,
                        Element: string.Empty,
                        TagDisplay: $"Item {++itemIndex}",
                        Vr: string.Empty,
                        Name: string.Empty,
                        Value: string.Empty,
                        Length: 0,
                        Depth: depth + 1,
                        Section: section));

                    AppendDataset(sequenceItem, rows, depth + 2, section);
                }

                continue;
            }

            rows.Add(CreateRow(item, RenderValue(dataset, item), GetLength(item), depth, section));
        }
    }

    private static DicomTagRow CreateRow(DicomItem item, string value, int length, int depth, string section)
    {
        DicomTag tag = item.Tag;

        return new DicomTagRow(
            Group: tag.Group.ToString("X4"),
            Element: tag.Element.ToString("X4"),
            TagDisplay: $"({tag.Group:X4},{tag.Element:X4})",
            Vr: item.ValueRepresentation?.Code ?? "UN",
            Name: ResolveName(tag),
            Value: value,
            Length: length,
            Depth: depth,
            Section: section);
    }

    /// <summary>
    /// Resolves the dictionary name of a tag, falling back to a readable placeholder for
    /// private attributes that no dictionary describes.
    /// </summary>
    private static string ResolveName(DicomTag tag)
    {
        try
        {
            DicomDictionaryEntry entry = DicomDictionary.Default[tag];

            if (entry is not null &&
                !string.IsNullOrWhiteSpace(entry.Name) &&
                !entry.Name.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
            {
                return entry.Name;
            }
        }
        catch
        {
            // A tag missing from the dictionary is expected for private attributes.
        }

        return tag.IsPrivate ? "Private attribute" : "Unknown attribute";
    }

    /// <summary>
    /// Renders a value for display. Binary attributes are summarised by their length
    /// rather than dumped, because the encapsulated document can be several megabytes.
    /// </summary>
    private static string RenderValue(DicomDataset dataset, DicomItem item)
    {
        try
        {
            if (item is DicomOtherByte or DicomOtherWord or DicomOtherLong or DicomOtherDouble or DicomOtherFloat)
            {
                return $"<binary, {GetLength(item)} bytes>";
            }

            if (item is DicomElement element)
            {
                if (element.Count == 0)
                {
                    return string.Empty;
                }

                string[] values = dataset.GetValues<string>(item.Tag);
                string joined = string.Join('\\', values);

                return joined.Length > MaximumRenderedValueLength
                    ? joined[..MaximumRenderedValueLength] + "..."
                    : joined;
            }

            return string.Empty;
        }
        catch (Exception ex)
        {
            // The viewer must never throw: an unreadable value is reported in place.
            return $"<could not be rendered: {ex.GetType().Name}>";
        }
    }

    private static int GetLength(DicomItem item)
    {
        try
        {
            return item is DicomElement element ? (int)element.Buffer.Size : 0;
        }
        catch
        {
            return 0;
        }
    }
}
