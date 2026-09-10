namespace PdfToDicom.Core.Models;

/// <summary>
/// What was found inside an image file, gathered before any conversion is attempted.
/// </summary>
/// <param name="FullPath">Absolute path of the file.</param>
/// <param name="FileName">File name alone, for display.</param>
/// <param name="SizeInBytes">Size of the file on disk.</param>
/// <param name="ModifiedUtc">Last write timestamp reported by the file system, in UTC.</param>
/// <param name="FormatName">
/// The format actually found in the file, for example "JPEG". Determined from the
/// content, not from the extension.
/// </param>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
/// <param name="PageCount">
/// Number of pages or frames. Greater than one for a multi page TIFF or an animated GIF,
/// each of which becomes its own DICOM instance within a single series.
/// </param>
/// <param name="IsColour">
/// True when the source carries colour. A greyscale source is kept greyscale rather than
/// being expanded to three identical channels, which would triple the size of the object
/// for no gain.
/// </param>
public sealed record ImageDocumentInfo(
    string FullPath,
    string FileName,
    long SizeInBytes,
    DateTime ModifiedUtc,
    string FormatName,
    int Width,
    int Height,
    int PageCount,
    bool IsColour)
{
    /// <summary>
    /// Human readable file size, for example "1.4 MB". Uses binary multiples because that
    /// is what Windows Explorer reports, avoiding confusion for the operator.
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

    /// <summary>
    /// Size the uncompressed pixel data will occupy in the DICOM object, in bytes.
    /// </summary>
    /// <remarks>
    /// Worth showing the operator. A JPEG of two megabytes becomes an object of thirty,
    /// because DICOM stores the pixels uncompressed, and somebody watching a transfer
    /// should not be surprised by that.
    /// </remarks>
    public long UncompressedPixelBytes => (long)Width * Height * (IsColour ? 3 : 1) * PageCount;

    /// <summary>Description of the pixel data, for the operator.</summary>
    public string PixelSummary =>
        $"{Width} x {Height}, {(IsColour ? "colour" : "greyscale")}" +
        (PageCount > 1 ? $", {PageCount} pages" : string.Empty);
}
