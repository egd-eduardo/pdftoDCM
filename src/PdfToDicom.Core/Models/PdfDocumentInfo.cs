namespace PdfToDicom.Core.Models;

/// <summary>
/// Descriptive information about a selected PDF file, gathered before conversion so
/// that the operator can confirm the correct document was chosen.
/// </summary>
/// <param name="FullPath">Absolute path of the file on disk.</param>
/// <param name="FileName">File name without the directory component.</param>
/// <param name="SizeInBytes">File length in bytes.</param>
/// <param name="CreatedUtc">Creation timestamp reported by the file system, in UTC.</param>
/// <param name="ModifiedUtc">Last write timestamp reported by the file system, in UTC.</param>
/// <param name="PageCount">Number of pages, or null when the count could not be determined.</param>
/// <param name="Title">Document title from the PDF metadata, when present.</param>
/// <param name="IsEncrypted">True when the PDF declares encryption.</param>
/// <param name="PdfVersion">PDF specification version declared in the header, when known.</param>
public sealed record PdfDocumentInfo(
    string FullPath,
    string FileName,
    long SizeInBytes,
    DateTime CreatedUtc,
    DateTime ModifiedUtc,
    int? PageCount,
    string? Title,
    bool IsEncrypted,
    string? PdfVersion)
{
    /// <summary>
    /// Human readable file size, for example "1.4 MB". Uses binary multiples because
    /// that is what Windows Explorer reports, avoiding confusion for the operator.
    /// </summary>
    public string FormattedSize
    {
        get
        {
            string[] units = { "bytes", "KB", "MB", "GB" };
            double size = SizeInBytes;
            int unit = 0;
            while (size >= 1024 && unit < units.Length - 1)
            {
                size /= 1024;
                unit++;
            }

            return unit == 0
                ? $"{SizeInBytes} {units[unit]}"
                : $"{size:0.#} {units[unit]}";
        }
    }
}
